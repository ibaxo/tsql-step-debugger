using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Xunit;

namespace TsqlDbg.Integration;

// A72 (§13): VS Code's Run to Cursor is a transient breakpoint + continue. Clicking a
// CONTINUATION line of a multi-line statement used to fall forward to the NEXT unit
// (the §13 forward-only scan), so the run overshot the clicked statement — the reported
// symptom. Now any line of a multi-line leaf statement binds to that statement. Pinned
// over the real DAP wire: setBreakpoints on the continuation line must verify AT the
// statement's start line, and continue must stop there.
public sealed class A72RunToCursorLiveTests
{
    private const string ConnEnvVar = "TSQLDBG_TEST_CONN";

    [SkippableFact]
    public async Task RunToCursor_OnContinuationLineOfMultiLineStatement_StopsAtThatStatement()
    {
        var raw = Environment.GetEnvironmentVariable(ConnEnvVar);
        Skip.If(string.IsNullOrWhiteSpace(raw), $"{ConnEnvVar} is not set; skipping live probe (never fake a pass — CLAUDE.md).");

        var csb = new SqlConnectionStringBuilder(raw);
        var tracePath = Path.Combine(Path.GetTempPath(), "a72-run-to-cursor.jsonl");
        if (File.Exists(tracePath))
        {
            File.Delete(tracePath);
        }

        var scriptPath = Path.Combine(Path.GetTempPath(), "a72-run-to-cursor.sql");
        await File.WriteAllTextAsync(scriptPath,
            "DECLARE @a int = 0;\n" +          // L1
            "SELECT @a AS a\n" +               // L2..L4: the multi-line statement
            "     , 2 AS b\n" +
            "  FROM (VALUES (0)) v(x);\n" +
            "PRINT 'done';\n");                // L5: pre-A72 the run overshot to here
        var targetsFile = Path.Combine(AppContext.BaseDirectory, "targets.json");

        await using var dap = DapStdioHarness.Launch(tracePath);
        await dap.SendRequestAsync("initialize", new JsonObject { ["clientID"] = "a72", ["adapterID"] = "tsqldbg" });
        await dap.SendRequestAsync("launch", new JsonObject
        {
            ["server"] = csb.DataSource,
            ["database"] = csb.InitialCatalog,
            ["mode"] = "script",
            ["script"] = scriptPath,
            ["targetsFile"] = targetsFile,
            ["stopOnEntry"] = true,
        });
        await dap.WaitForEventAsync("initialized");

        // Run to Cursor on L3 — a continuation line of the L2..L4 SELECT.
        var bpResponse = await dap.SendRequestAsync("setBreakpoints", new JsonObject
        {
            ["source"] = new JsonObject { ["path"] = scriptPath },
            ["breakpoints"] = new JsonArray(new JsonObject { ["line"] = 3 }),
        });
        var bp = bpResponse["body"]!["breakpoints"]!.AsArray()[0]!;
        Assert.True(bp["verified"]!.GetValue<bool>());
        Assert.Equal(2, bp["line"]!.GetValue<int>());   // the dot moves to the SELECT, not PRINT (L5)

        await dap.SendRequestAsync("configurationDone", new JsonObject());
        await dap.WaitForEventAsync("stopped", e => e["body"]!["reason"]!.GetValue<string>() == "entry");

        await dap.SendRequestAsync("continue", new JsonObject { ["threadId"] = 1 });
        await dap.WaitForEventAsync("stopped", e => e["body"]!["reason"]!.GetValue<string>() == "breakpoint");

        var frames = await dap.SendRequestAsync("stackTrace", new JsonObject { ["threadId"] = 1 });
        var top = frames["body"]!["stackFrames"]!.AsArray()[0]!;
        Assert.Equal(2, top["line"]!.GetValue<int>());  // stopped AT the multi-line SELECT

        await dap.DisposeAsync();
    }
}
