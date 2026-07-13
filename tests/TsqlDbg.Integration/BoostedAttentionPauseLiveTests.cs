using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;

namespace TsqlDbg.Integration;

// M6 boosted-attention triage (docs/archive/reviews/m6-boosted-attention-triage-fable.md,
// resolving docs/archive/reviews/m6-item7-boosted-attention-pause-engine-fact-escalation.md):
// the two live pins for the pause-into-a-boosted-batch path over real stdio DAP.
//
// Fact 30 background: on the ASYNC driver path (ExecuteReaderAsync — the executor's
// current shape) SqlClient cannot deliver the attention while awaiting a batch that
// streams nothing, so a §10.5 pause takes effect only around the batch's natural end.
// These tests therefore do NOT pin pause LATENCY — they pin (a) the protocol thread
// never blocks on a pause (A15; pre-fix, Cancel() froze it for the batch's remaining
// runtime), and (b) when the attention finally lands, B7's recovery genuinely runs
// (pre-fix, the raw-OperationCanceledException surface skipped it, leaving the cursor
// ON the node with the subtree's completed work persisted — re-fire = double-apply).
public sealed class BoostedAttentionPauseLiveTests
{
    private const string ConnEnvVar = "TSQLDBG_TEST_CONN";

    // Boost-eligible loop (mirrors BoostAdapterBreakpointRefusalLiveTests' script,
    // bigger): per-iteration cost with B4 markers is ~0.2-0.5 ms live (fact 30
    // calibration), so 30k iterations ≈ 10-20 s of one boosted batch — long enough
    // that a pause at 2 s is unambiguously mid-batch, short enough for the suite.
    private static string LoopScript(int iterations) => $"""
        DECLARE @i int, @n int;
        SET @i = 0;
        CREATE TABLE #work (v int);
        WHILE @i < {iterations}
        BEGIN
            SET @i = @i + 1;
            INSERT #work VALUES (@i);
        END
        SELECT @n = COUNT(*) FROM #work;
        """;

    private static (string? ConnString, string TracePath, string ScriptPath) SkipUnlessLive(string traceFileName, int iterations)
    {
        var raw = Environment.GetEnvironmentVariable(ConnEnvVar);
        Skip.If(string.IsNullOrWhiteSpace(raw), $"{ConnEnvVar} is not set; skipping live probe (never fake a pass — CLAUDE.md).");

        var tracePath = Path.Combine(Path.GetTempPath(), traceFileName);
        if (File.Exists(tracePath))
        {
            File.Delete(tracePath);
        }

        var scriptPath = Path.Combine(Path.GetTempPath(), $"{Path.GetFileNameWithoutExtension(traceFileName)}.sql");
        File.WriteAllText(scriptPath, LoopScript(iterations));
        return (raw, tracePath, scriptPath);
    }

    // Polls the adapter's live trace file (per-line AutoFlush, FileShare.Read) until a
    // line containing `needle` appears. Opens with FileShare.ReadWrite because the
    // adapter process still holds the write handle.
    private static async Task WaitForTraceLineAsync(string tracePath, string needle, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(tracePath))
            {
                using var stream = new FileStream(tracePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                var text = await reader.ReadToEndAsync();
                if (text.Contains(needle))
                {
                    return;
                }
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"Trace line containing {needle} did not appear within {timeout.TotalSeconds:0} s ({tracePath}).");
    }

    private static async Task<DapStdioHarness> LaunchBoostedScriptAsync(
        string connectionString, string tracePath, string scriptPath)
    {
        var csb = new SqlConnectionStringBuilder(connectionString);
        var targetsFile = Path.Combine(AppContext.BaseDirectory, "targets.json");

        var dap = DapStdioHarness.Launch(tracePath);
        await dap.SendRequestAsync("initialize", new JsonObject { ["clientID"] = "m6-boosted-attention-live-test", ["adapterID"] = "tsqldbg" });
        await dap.SendRequestAsync("launch", new JsonObject
        {
            ["server"] = csb.DataSource,
            ["database"] = csb.InitialCatalog,
            ["mode"] = "script",
            ["script"] = scriptPath,
            ["targetsFile"] = targetsFile,
            ["stopOnEntry"] = true,
            ["boost"] = true,
        });
        await dap.WaitForEventAsync("initialized");
        await dap.SendRequestAsync("configurationDone", new JsonObject());
        await dap.WaitForEventAsync("stopped", e => e["body"]!["reason"]!.GetValue<string>() == "entry");
        return dap;
    }

    // Anomaly-2 control (escalation doc §5-Q3): a LONG boosted loop under continue
    // with NO pause completes and publishes normally — there is no lost-completion
    // path; the original 60 s observation was the 4M-iteration batch still being
    // ~7 % done (fact 30 calibration: ~213 µs/marker-iteration, ~14 min natural, not
    // the extrapolated ~9-10 s).
    [SkippableFact]
    public async Task LongBoostedLoop_UnderContinue_NoPause_CompletesAndTerminates()
    {
        var (connString, tracePath, scriptPath) = SkipUnlessLive("m6-boosted-attention-control.jsonl", iterations: 10_000);

        await using var dap = await LaunchBoostedScriptAsync(connString!, tracePath, scriptPath);
        await dap.SendRequestAsync("continue", new JsonObject { ["threadId"] = 1 });
        await dap.WaitForEventAsync("terminated", timeout: TimeSpan.FromSeconds(120));
        await dap.DisposeAsync();

        var traceLines = await File.ReadAllLinesAsync(tracePath);
        Assert.Contains(traceLines, l => l.Contains("\"boost.fire\""));
        Assert.Contains(traceLines, l => l.Contains("\"boost.complete\""));
    }

    [SkippableFact]
    public async Task PauseIntoBoostedLoop_RespondsPromptly_AndRecoversPosition()
    {
        var (connString, tracePath, scriptPath) = SkipUnlessLive("m6-boosted-attention-pause.jsonl", iterations: 30_000);

        await using var dap = await LaunchBoostedScriptAsync(connString!, tracePath, scriptPath);
        await dap.SendRequestAsync("continue", new JsonObject { ["threadId"] = 1 });

        // Deterministic mid-batch precondition (M7 design note §0.2): the original
        // fixed 2 s delay raced session progress — under full parallel-suite tempdb
        // load the FIRST interpreted batch (SET @i = 0) once sat in flight past the
        // delay, the pause cancelled IT, and the stop was the legal §10.5 nothing-ran
        // shape (@i NULL) — the pin then tested the trivial interpreted-pause path
        // instead of B7. FileTraceSink is per-line AutoFlush + FileShare.Read, so the
        // live trace is pollable: wait for boost.fire (dispatch has happened, the
        // boosted batch is the in-flight command), then one beat so markers have
        // persisted (~0.2-0.5 ms/iteration live → hundreds of markers; the 30k-loop
        // batch itself runs ~6-15 s, so fire+1s is unambiguously mid-batch).
        await WaitForTraceLineAsync(tracePath, "\"boost.fire\"", TimeSpan.FromSeconds(60));
        await Task.Delay(1000);

        // (a) The A15 pin: the pause RESPONSE must come back immediately even though
        // the attention won't land for many more seconds (pre-fix: the protocol
        // thread sat inside SqlCommand.Cancel until the batch ended — this request
        // timed out, and so did every request after it).
        await dap.SendRequestAsync("pause", new JsonObject { ["threadId"] = 1 }, TimeSpan.FromSeconds(5));

        // ...and the protocol thread keeps serving other requests while the
        // cancelled batch is still draining.
        await dap.SendRequestAsync("threads", new JsonObject(), TimeSpan.FromSeconds(5));

        // (b) The B7 pin: when the attention lands, the stop publishes as a pause and
        // the recovery read re-established position + variables — @i must reflect the
        // last persisted marker (> 0), not the pre-loop snapshot 0 the cursor-ON-node
        // no-recovery failure mode would show.
        var stop = await dap.WaitForEventAsync("stopped",
            e => e["body"]!["reason"]!.GetValue<string>() == "pause", TimeSpan.FromSeconds(120));
        Assert.Equal(1, stop["body"]!["threadId"]!.GetValue<int>());

        var frames = await dap.SendRequestAsync("stackTrace", new JsonObject { ["threadId"] = 1 });
        var frameId = frames["body"]!["stackFrames"]!.AsArray()[0]!["id"]!.GetValue<int>();
        var scopes = await dap.SendRequestAsync("scopes", new JsonObject { ["frameId"] = frameId });
        var varsRef = scopes["body"]!["scopes"]!.AsArray()[0]!["variablesReference"]!.GetValue<int>();
        var vars = await dap.SendRequestAsync("variables", new JsonObject { ["variablesReference"] = varsRef });
        var i = vars["body"]!["variables"]!.AsArray()
            .First(v => v!["name"]!.GetValue<string>() == "@i")!["value"]!.GetValue<string>();
        Assert.True(int.TryParse(i, out var iValue) && iValue > 0,
            $"@i at the pause stop should reflect the recovery read's marker state, got '{i}'");

        // The session stays coherent: continue runs the loop's remainder (interpreted
        // to the WHILE re-arrival, then a second boosted batch) to natural completion.
        await dap.SendRequestAsync("continue", new JsonObject { ["threadId"] = 1 });
        await dap.WaitForEventAsync("terminated", timeout: TimeSpan.FromSeconds(180));
        await dap.DisposeAsync();

        var traceLines = await File.ReadAllLinesAsync(tracePath);
        Assert.Contains(traceLines, l => l.Contains("\"boost.fire\""));
        Assert.Contains(traceLines, l => l.Contains("\"boost.recovery\"") || l.Contains("\"boost.complete\""));
    }
}
