using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;

namespace TsqlDbg.Integration;

// M6 item 6 (organizer-review follow-up, docs/archive/reviews/
// m6-item6-adapter-boost-wiring-sonnet-escalation.md item 2's required tests): the
// adapter-level pin that IsSuBlocked/TryStepBoostedAsync wiring in
// TsqlDbgDebugSession.RunUntilAsync actually refuses boost through the REAL DAP
// continue path when a breakpoint or logpoint binds to a member SU inside an
// otherwise-eligible subtree -- proven by driving the compiled adapter over real
// stdio (DapStdioHarness), not just Core-level BoostPlanner unit tests (which cover
// the isBlocked CONTRACT but never exercise the adapter's own predicate wiring).
public sealed class BoostAdapterBreakpointRefusalLiveTests
{
    private const string ConnEnvVar = "TSQLDBG_TEST_CONN";

    // Lines: 1 DECLARE, 2 SET @i, 3 CREATE TABLE, 4 WHILE, 5 BEGIN, 6 SET @i (the
    // line a breakpoint/logpoint binds to below), 7 INSERT, 8 END, 9 the post-loop
    // reader -- otherwise boost-eligible in full (mirrors BoostSessionLiveTests'
    // clean-loop script; script mode's frame 0 uses these line numbers verbatim, no
    // module-relative offset the way a stored procedure's OBJECT_DEFINITION has).
    private const string LoopScript = """
        DECLARE @i int, @n int;
        SET @i = 0;
        CREATE TABLE #work (v int);
        WHILE @i < 4
        BEGIN
            SET @i = @i + 1;
            INSERT #work VALUES (@i);
        END
        SELECT @n = COUNT(*) FROM #work;
        """;

    private static (string? ConnString, string TracePath, string ScriptPath) SkipUnlessLive(string traceFileName)
    {
        var raw = Environment.GetEnvironmentVariable(ConnEnvVar);
        Skip.If(string.IsNullOrWhiteSpace(raw), $"{ConnEnvVar} is not set; skipping live probe (never fake a pass — CLAUDE.md).");

        var tracePath = Path.Combine(Path.GetTempPath(), traceFileName);
        if (File.Exists(tracePath))
        {
            File.Delete(tracePath);
        }

        var scriptPath = Path.Combine(Path.GetTempPath(), $"{Path.GetFileNameWithoutExtension(traceFileName)}.sql");
        File.WriteAllText(scriptPath, LoopScript);
        return (raw, tracePath, scriptPath);
    }

    private static async Task<DapStdioHarness> LaunchBoostedScriptAsync(
        string connectionString, string tracePath, string scriptPath)
    {
        var csb = new SqlConnectionStringBuilder(connectionString);
        var targetsFile = Path.Combine(AppContext.BaseDirectory, "targets.json");

        var dap = DapStdioHarness.Launch(tracePath);
        await dap.SendRequestAsync("initialize", new JsonObject { ["clientID"] = "m6-boost-refusal-live-test", ["adapterID"] = "tsqldbg" });
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
        return dap;
    }

    [SkippableFact]
    public async Task BreakpointInsideEligibleLoop_RefusesBoost_AndStopsAtIt()
    {
        var (connString, tracePath, scriptPath) = SkipUnlessLive("m6-boost-refusal-breakpoint.jsonl");

        await using var dap = await LaunchBoostedScriptAsync(connString!, tracePath, scriptPath);

        var setBp = await dap.SendRequestAsync("setBreakpoints", new JsonObject
        {
            ["source"] = new JsonObject { ["path"] = scriptPath },
            ["breakpoints"] = new JsonArray(new JsonObject { ["line"] = 6 }),
        });
        var bp = setBp["body"]!["breakpoints"]!.AsArray()[0]!;
        Assert.True(bp["verified"]!.GetValue<bool>());
        Assert.Equal(6, bp["line"]!.GetValue<int>());

        await dap.SendRequestAsync("configurationDone", new JsonObject());
        await dap.WaitForEventAsync("stopped", e => e["body"]!["reason"]!.GetValue<string>() == "entry");

        await dap.SendRequestAsync("continue", new JsonObject { ["threadId"] = 1 });
        var stop = await dap.WaitForEventAsync("stopped", e => e["body"]!["reason"]!.GetValue<string>() == "breakpoint");
        Assert.Equal(1, stop["body"]!["threadId"]!.GetValue<int>());

        var trace = await dap.SendRequestAsync("stackTrace", new JsonObject { ["threadId"] = 1 });
        Assert.Equal(6, trace["body"]!["stackFrames"]!.AsArray()[0]!["line"]!.GetValue<int>());

        await dap.DisposeAsync();

        var traceLines = await File.ReadAllLinesAsync(tracePath);
        Assert.Contains(traceLines, l => l.Contains("\"boost.refuse\"") && l.Contains("breakpoint-or-logpoint"));
        Assert.DoesNotContain(traceLines, l => l.Contains("\"boost.fire\"") || l.Contains("\"boost.plan\""));
    }

    [SkippableFact]
    public async Task LogpointInsideEligibleLoop_RefusesBoost_AndLogsWithoutStopping()
    {
        var (connString, tracePath, scriptPath) = SkipUnlessLive("m6-boost-refusal-logpoint.jsonl");

        await using var dap = await LaunchBoostedScriptAsync(connString!, tracePath, scriptPath);

        var setBp = await dap.SendRequestAsync("setBreakpoints", new JsonObject
        {
            ["source"] = new JsonObject { ["path"] = scriptPath },
            ["breakpoints"] = new JsonArray(new JsonObject { ["line"] = 6, ["logMessage"] = "i={@i}" }),
        });
        var bp = setBp["body"]!["breakpoints"]!.AsArray()[0]!;
        Assert.True(bp["verified"]!.GetValue<bool>());

        await dap.SendRequestAsync("configurationDone", new JsonObject());
        await dap.WaitForEventAsync("stopped", e => e["body"]!["reason"]!.GetValue<string>() == "entry");

        await dap.SendRequestAsync("continue", new JsonObject { ["threadId"] = 1 });

        // Every one of the 4 loop iterations should log -- proof the loop ran
        // INTERPRETED (a boosted run would compose the whole WHILE into one server
        // batch and never evaluate a client-side logpoint per iteration at all).
        // The logpoint's qualification check runs BEFORE the SU it's bound to
        // executes (RunUntilAsync checks `current` ahead of stepping it), so it logs
        // @i's PRE-increment value each time: i=0,1,2,3 (ground-truthed via a
        // throwaway diagnostic DAP session, not assumed).
        for (var i = 0; i <= 3; i++)
        {
            var expected = $"i={i}";
            var output = await dap.WaitForEventAsync("output", e =>
                e["body"]!["category"]?.GetValue<string>() == "console"
                && (e["body"]!["output"]?.GetValue<string>().Contains(expected) ?? false));
            Assert.Contains(expected, output["body"]!["output"]!.GetValue<string>());
        }

        await dap.WaitForEventAsync("terminated", timeout: TimeSpan.FromSeconds(15));

        await dap.DisposeAsync();

        var traceLines = await File.ReadAllLinesAsync(tracePath);
        Assert.Contains(traceLines, l => l.Contains("\"boost.refuse\"") && l.Contains("breakpoint-or-logpoint"));
        Assert.DoesNotContain(traceLines, l => l.Contains("\"boost.fire\"") || l.Contains("\"boost.plan\""));
    }
}
