using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;

namespace TsqlDbg.Integration;

// M7 hardening (design note §4/§8-A29, C4): a live DapStdioHarness smoke for the
// executeAs plumbing -- launch `executeAs: "USER = 'dbo'"` and confirm the debuggee
// itself observes the impersonated principal (proving EXECUTE AS ran at session init,
// BEFORE frame 0's own statements), then disconnect cleanly (proving REVERT in
// teardown doesn't fault the session end). Mirrors the M1-M6 real-stdio-DAP precedent
// (BoostAdapterBreakpointRefusalLiveTests et al.) rather than a Core-only unit test,
// since C4 is specifically about the session-init/teardown ORDERING around a real
// connection's security context.
public sealed class C4ExecuteAsLiveTests
{
    private const string ConnEnvVar = "TSQLDBG_TEST_CONN";

    // Line 1 DECLARE, 2 SET @u = USER_NAME() (observes the impersonated principal),
    // 3 a trailing no-op stop so `next` past line 2 lands somewhere inspectable
    // BEFORE session completion (RunUntilAsync ends the session, never a stop, on
    // the SAME next() that finishes the last statement).
    private const string Script = """
        DECLARE @u sysname;
        SET @u = USER_NAME();
        SELECT 1;
        """;

    [SkippableFact]
    public async Task ExecuteAs_ImpersonatesBeforeFrameZero_AndRevertsCleanlyOnDisconnect()
    {
        var raw = Environment.GetEnvironmentVariable(ConnEnvVar);
        Skip.If(string.IsNullOrWhiteSpace(raw), $"{ConnEnvVar} is not set; skipping live probe (never fake a pass — CLAUDE.md).");

        var tracePath = Path.Combine(Path.GetTempPath(), "m7-c4-executeas.jsonl");
        if (File.Exists(tracePath))
        {
            File.Delete(tracePath);
        }

        var scriptPath = Path.Combine(Path.GetTempPath(), "m7-c4-executeas.sql");
        File.WriteAllText(scriptPath, Script);

        var csb = new SqlConnectionStringBuilder(raw);
        var targetsFile = Path.Combine(AppContext.BaseDirectory, "targets.json");

        await using var dap = DapStdioHarness.Launch(tracePath);
        await dap.SendRequestAsync("initialize", new JsonObject { ["clientID"] = "m7-c4-executeas-live-test", ["adapterID"] = "tsqldbg" });
        await dap.SendRequestAsync("launch", new JsonObject
        {
            ["server"] = csb.DataSource,
            ["database"] = csb.InitialCatalog,
            ["mode"] = "script",
            ["script"] = scriptPath,
            ["targetsFile"] = targetsFile,
            ["stopOnEntry"] = true,
            ["executeAs"] = "USER = 'dbo'",
        });
        await dap.WaitForEventAsync("initialized");
        await dap.SendRequestAsync("configurationDone", new JsonObject());
        await dap.WaitForEventAsync("stopped", e => e["body"]!["reason"]!.GetValue<string>() == "entry");

        await dap.SendRequestAsync("next", new JsonObject { ["threadId"] = 1 });   // DECLARE
        await dap.WaitForEventAsync("stopped", e => e["body"]!["reason"]!.GetValue<string>() == "step");
        await dap.SendRequestAsync("next", new JsonObject { ["threadId"] = 1 });   // SET @u = USER_NAME()
        await dap.WaitForEventAsync("stopped", e => e["body"]!["reason"]!.GetValue<string>() == "step");

        var frames = await dap.SendRequestAsync("stackTrace", new JsonObject { ["threadId"] = 1 });
        var frameId = frames["body"]!["stackFrames"]!.AsArray()[0]!["id"]!.GetValue<int>();
        var scopes = await dap.SendRequestAsync("scopes", new JsonObject { ["frameId"] = frameId });
        var varsRef = scopes["body"]!["scopes"]!.AsArray()[0]!["variablesReference"]!.GetValue<int>();
        var vars = await dap.SendRequestAsync("variables", new JsonObject { ["variablesReference"] = varsRef });
        var u = vars["body"]!["variables"]!.AsArray()
            .First(v => v!["name"]!.GetValue<string>() == "@u")!["value"]!.GetValue<string>();

        // §16/C4: EXECUTE AS ran between §4 step 2 and step 3 (frame 0 resolution) --
        // the debuggee's own USER_NAME() (evaluated server-side, live truth) must
        // already reflect the impersonated principal, not the login's real identity.
        Assert.Equal("dbo", u);

        // Disconnect (not terminate/commit -- always rolls back per CLAUDE.md safety
        // rule 7) must complete without hanging or faulting on the REVERT teardown
        // added ahead of the rollback -- DisposeAsync sends it.
        await dap.DisposeAsync();

        var traceLines = await File.ReadAllLinesAsync(tracePath);
        Assert.Contains(traceLines, l => l.Contains("\"session.revert\""));
        Assert.Contains(traceLines, l => l.Contains("\"session.rollback\""));
    }
}
