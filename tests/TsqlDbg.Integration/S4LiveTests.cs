using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;

namespace TsqlDbg.Integration;

// M7 hardening (design note §5.3/§5.4, S4): live DapStdioHarness pins for terminate/
// cancel/disconnect-pre-cancel over the real DAP wire. Terminate's OWN commit-modal
// composition is already live-pinned end-to-end by CommitModalLiveTests (which calls
// `terminate` and then relies on DapStdioHarness.DisposeAsync's own follow-up
// `disconnect` completing cleanly — an implicit idempotency proof); this file covers
// the two pins §5.3/§5.4 name specifically: cancel of a slow REPL evaluate, and
// disconnect's pre-cancel racing a long in-flight debuggee batch.
public sealed class S4LiveTests
{
    private const string ConnEnvVar = "TSQLDBG_TEST_CONN";

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

    // §5.4 (S4 cancel): a REPL evaluate is genuinely slow server-side (WAITFOR DELAY,
    // dispatched on the SAME sync executor path §10.5/fact 30 already made
    // millisecond-cancellable) -- `cancel` must return the evaluate's OWN response
    // promptly (well under the full delay), and the session must step normally
    // afterward (the §3 "local cancelled-item result, never the §10.5 session path"
    // contract, now wire-visible).
    [SkippableFact]
    public async Task Cancel_OfASlowReplEvaluate_ReturnsPromptly_AndSessionStepsOnNormally()
    {
        var raw = Environment.GetEnvironmentVariable(ConnEnvVar);
        Skip.If(string.IsNullOrWhiteSpace(raw), $"{ConnEnvVar} is not set; skipping live probe (never fake a pass — CLAUDE.md).");

        var csb = new SqlConnectionStringBuilder(raw);
        var tracePath = Path.Combine(Path.GetTempPath(), "m7-s4-cancel-repl.jsonl");
        if (File.Exists(tracePath))
        {
            File.Delete(tracePath);
        }

        var scriptPath = Path.Combine(Path.GetTempPath(), "m7-s4-cancel-repl.sql");
        await File.WriteAllTextAsync(scriptPath, "DECLARE @x int = 1;\nSET @x = 2;\n");

        var targetsFile = Path.Combine(AppContext.BaseDirectory, "targets.json");

        await using var dap = DapStdioHarness.Launch(tracePath);
        await dap.SendRequestAsync("initialize", new JsonObject { ["clientID"] = "m7-s4-cancel-live-test", ["adapterID"] = "tsqldbg" });
        await dap.SendRequestAsync("launch", new JsonObject
        {
            ["server"] = csb.DataSource,
            ["database"] = csb.InitialCatalog,
            ["mode"] = "script",
            ["script"] = scriptPath,
            ["targetsFile"] = targetsFile,
            ["stopOnEntry"] = true,
            // §12.3: the REPL write-mode gate treats any non-SELECT shape (WAITFOR
            // included) as a write — needed here purely to let the slow statement
            // dispatch at all, unrelated to what this test is pinning.
            ["allowConsoleWrites"] = true,
        });
        await dap.WaitForEventAsync("initialized");
        await dap.SendRequestAsync("configurationDone", new JsonObject());
        await dap.WaitForEventAsync("stopped", e => e["body"]!["reason"]!.GetValue<string>() == "entry");

        var frames = await dap.SendRequestAsync("stackTrace", new JsonObject { ["threadId"] = 1 });
        var frameId = frames["body"]!["stackFrames"]!.AsArray()[0]!["id"]!.GetValue<int>();

        // Fire the slow evaluate WITHOUT awaiting it yet.
        var evalTask = dap.SendRequestAsync("evaluate", new JsonObject
        {
            ["expression"] = "WAITFOR DELAY '00:00:10';",
            ["context"] = "repl",
            ["frameId"] = frameId,
        }, TimeSpan.FromSeconds(30));

        // Deterministic mid-flight precondition (the M7-note §0.2 pattern): wait for
        // the actual COMPOSED batch to have been dispatched server-side -- NOT just
        // "WAITFOR DELAY", which ALSO appears in the dap.request echo of the
        // evaluate's own expression text (logged the instant the request arrives,
        // long before EvaluateReplAsync's parse/classify/compose work even starts).
        // "BEGIN TRY" is unique to the composed batch itself; nothing SU ahead of
        // this point in the test emits one (no `next` has run yet).
        await WaitForTraceLineAsync(tracePath, "BEGIN TRY", TimeSpan.FromSeconds(15));

        var stopwatch = Stopwatch.StartNew();
        await dap.SendRequestAsync("cancel", new JsonObject(), TimeSpan.FromSeconds(10));

        // The evaluate's OWN response: an error, arriving well before the full 10s
        // delay would naturally elapse -- proof the sync-path attention (fact 30a)
        // genuinely killed the server-side WAITFOR rather than the request just
        // being abandoned client-side while the batch runs to term. Genuinely
        // mid-flight cancellation resolves through EvaluateReplAsync's OWN
        // pre-existing StatementExecutionException handling (a real server-side
        // attention -- SqlException Number 0 -- caught there and returned as a
        // normal Refused ReplResult, §12.3), not through the NEW cancel-fence's
        // own Cancelled outcome (that mechanism's own value is queued-but-
        // not-yet-started work and gate-wait preemption -- both covered by
        // InspectionExecutorTests directly); either way the message names the
        // cancellation.
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(async () => await evalTask);
        stopwatch.Stop();
        Assert.Contains("cancel", thrown.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(8),
            $"evaluate should have returned promptly after cancel, took {stopwatch.Elapsed}.");

        // The session itself is untouched: next steps normally onto the SET. A
        // generous timeout here (not a "should be instant" assertion) -- under the
        // full parallel suite's own SQL Server load, the attention's ack on this
        // connection can take longer than an isolated run would suggest (the same
        // class of margin the M7-note §0.2 fix and BoostedAttentionPauseLiveTests'
        // own long timeouts already account for elsewhere).
        await dap.SendRequestAsync("next", new JsonObject { ["threadId"] = 1 });
        await dap.WaitForEventAsync("stopped", e => e["body"]!["reason"]!.GetValue<string>() == "step", TimeSpan.FromSeconds(30));

        await dap.DisposeAsync();
    }

    // §5.3 rider (ii): disconnect used to queue teardown behind the executor gate
    // with nothing preempting a long in-flight debuggee batch first. `waitfor:
    // "honor"` sends a genuine, long server-side WAITFOR; disconnecting mid-flight
    // must roll back within a few seconds, not the batch's remaining ~14s.
    [SkippableFact]
    public async Task Disconnect_DuringALongWaitforBatch_RollsBackWithinSeconds_NotBatchRemaining()
    {
        var raw = Environment.GetEnvironmentVariable(ConnEnvVar);
        Skip.If(string.IsNullOrWhiteSpace(raw), $"{ConnEnvVar} is not set; skipping live probe (never fake a pass — CLAUDE.md).");

        var csb = new SqlConnectionStringBuilder(raw);
        var tracePath = Path.Combine(Path.GetTempPath(), "m7-s4-disconnect-precancel.jsonl");
        if (File.Exists(tracePath))
        {
            File.Delete(tracePath);
        }

        var scriptPath = Path.Combine(Path.GetTempPath(), "m7-s4-disconnect-precancel.sql");
        await File.WriteAllTextAsync(scriptPath, "WAITFOR DELAY '00:00:15';\nSELECT 1;\n");

        var targetsFile = Path.Combine(AppContext.BaseDirectory, "targets.json");

        await using var dap = DapStdioHarness.Launch(tracePath);
        await dap.SendRequestAsync("initialize", new JsonObject { ["clientID"] = "m7-s4-disconnect-live-test", ["adapterID"] = "tsqldbg" });
        await dap.SendRequestAsync("launch", new JsonObject
        {
            ["server"] = csb.DataSource,
            ["database"] = csb.InitialCatalog,
            ["mode"] = "script",
            ["script"] = scriptPath,
            ["targetsFile"] = targetsFile,
            ["stopOnEntry"] = true,
            ["waitfor"] = "honor",
        });
        await dap.WaitForEventAsync("initialized");
        await dap.SendRequestAsync("configurationDone", new JsonObject());
        await dap.WaitForEventAsync("stopped", e => e["body"]!["reason"]!.GetValue<string>() == "entry");

        await dap.SendRequestAsync("continue", new JsonObject { ["threadId"] = 1 });
        // NOTE: the trace file's default JSON encoder escapes apostrophes, so the
        // needle deliberately avoids the quoted delay literal — plain "WAITFOR
        // DELAY" has no characters the encoder would touch (BoostedAttentionPauseLiveTests precedent).
        await WaitForTraceLineAsync(tracePath, "WAITFOR DELAY", TimeSpan.FromSeconds(15));

        var stopwatch = Stopwatch.StartNew();
        await dap.SendRequestAsync("disconnect", new JsonObject(), TimeSpan.FromSeconds(10));

        // The rollback (server-side teardown) must land within a handful of
        // seconds -- the pre-cancel mechanism killed the in-flight WAITFOR via the
        // same sync-path attention `pause` uses, rather than waiting out its
        // remaining ~14s runtime.
        await WaitForTraceLineAsync(tracePath, "\"session.rollback\"", TimeSpan.FromSeconds(8));
        stopwatch.Stop();
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10),
            $"disconnect should roll back within a few seconds, took {stopwatch.Elapsed}.");

        await dap.DisposeAsync();
    }
}
