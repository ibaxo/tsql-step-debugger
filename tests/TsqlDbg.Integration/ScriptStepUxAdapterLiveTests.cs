using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;

namespace TsqlDbg.Integration;

// A50/A51/A52 adapter lane: three step-UX surfacings that are ONLY observable over the DAP
// wire — the Core Session API and the fidelity harness (RunToEndAsync) never see the
// stackTrace range, the OutputEvent stream, or the System scope's rendered variables (the
// same "adapter-only" lesson as A44's MultibatchAdapterLiveTests). Drives the REAL compiled
// adapter over stdio via DapStdioHarness.
//   A51 (§13): stackTrace reports the FULL statement span (endLine/endColumn), so VS Code
//              boxes the whole multi-line statement about to execute, not just its first line.
//   A50 (§12.3): a stepped SELECT's own result set renders to the Debug Console (stdout).
//   A52 (§12.1): the System scope shows QUOTED_IDENTIFIER / ANSI_NULLS.
public sealed class ScriptStepUxAdapterLiveTests
{
    private const string ConnEnvVar = "TSQLDBG_TEST_CONN";

    // A single 3-line SELECT (so endLine != line proves the whole-statement span), then GO.
    //   1  SELECT 111 AS n, 222 AS m
    //   2  FROM (SELECT 1 AS x) t
    //   3  WHERE t.x = 1;
    private const string Script =
        "SELECT 111 AS n, 222 AS m\nFROM (SELECT 1 AS x) t\nWHERE t.x = 1;\nGO\n";

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
        File.WriteAllText(scriptPath, Script);
        return (raw, tracePath, scriptPath);
    }

    private static async Task<DapStdioHarness> LaunchScriptAsync(string connectionString, string tracePath, string scriptPath)
    {
        var csb = new SqlConnectionStringBuilder(connectionString);
        var targetsFile = Path.Combine(AppContext.BaseDirectory, "targets.json");

        var dap = DapStdioHarness.Launch(tracePath);
        await dap.SendRequestAsync("initialize", new JsonObject { ["clientID"] = "a50-a52-step-ux-live-test", ["adapterID"] = "tsqldbg" });
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
        return dap;
    }

    [SkippableFact]
    public async Task StackTrace_ReportsWholeStatementSpan_StepShowsResultSet_SystemScopeShowsParseOptions()
    {
        var (connString, tracePath, scriptPath) = SkipUnlessLive("a50-a52-step-ux.jsonl");
        await using var dap = await LaunchScriptAsync(connString!, tracePath, scriptPath);

        await dap.SendRequestAsync("configurationDone", new JsonObject());
        await dap.WaitForEventAsync("stopped", e => e["body"]!["reason"]!.GetValue<string>() == "entry");

        // A51: the top frame's range spans the WHOLE statement (lines 1-3), not just line 1.
        var trace = await dap.SendRequestAsync("stackTrace", new JsonObject { ["threadId"] = 1 });
        var top = trace["body"]!["stackFrames"]!.AsArray()[0]!;
        var frameId = top["id"]!.GetValue<int>();
        Assert.Equal(1, top["line"]!.GetValue<int>());
        Assert.Equal(3, top["endLine"]!.GetValue<int>());              // WHERE clause on line 3
        Assert.True(top["endColumn"]!.GetValue<int>() > 0, "endColumn must be set so the range is a real box");

        // A52: the System scope exposes the parse-time options (defaults ON/ON here).
        var options = await CollectAllVariablesAsync(dap, frameId);
        Assert.Equal("ON", options.GetValueOrDefault("QUOTED_IDENTIFIER"));
        Assert.Equal("ON", options.GetValueOrDefault("ANSI_NULLS"));
        // A53: the value-carrying options show their seeded defaults in the System scope too.
        Assert.Equal("7", options.GetValueOrDefault("DATEFIRST"));
        Assert.Equal("-1", options.GetValueOrDefault("LOCK_TIMEOUT"));
        Assert.Equal("-1", options.GetValueOrDefault("TEXTSIZE"));
        Assert.Equal("NORMAL", options.GetValueOrDefault("DEADLOCK_PRIORITY"));

        // A50: stepping the SELECT surfaces its own result set to the Debug Console (stdout),
        // BEFORE the session terminates. Both projected columns (111, 222) must appear.
        await dap.SendRequestAsync("next", new JsonObject { ["threadId"] = 1 });
        var output = await dap.WaitForEventAsync("output",
            e => e["body"]?["category"]?.GetValue<string>() == "stdout"
                 && (e["body"]?["output"]?.GetValue<string>()?.Contains("111") ?? false));
        var text = output["body"]!["output"]!.GetValue<string>();
        Assert.Contains("222", text);
        Assert.Contains("n", text);      // the column header is rendered too

        await dap.WaitForEventAsync("terminated", timeout: TimeSpan.FromSeconds(15));
        await dap.DisposeAsync();
    }

    // Flatten every scope's variables at a frame into name->value (System scope among them).
    private static async Task<Dictionary<string, string>> CollectAllVariablesAsync(DapStdioHarness dap, int frameId)
    {
        var scopes = await dap.SendRequestAsync("scopes", new JsonObject { ["frameId"] = frameId });
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var scope in scopes["body"]!["scopes"]!.AsArray())
        {
            var reference = scope!["variablesReference"]!.GetValue<int>();
            if (reference == 0)
            {
                continue;
            }

            var vars = await dap.SendRequestAsync("variables", new JsonObject { ["variablesReference"] = reference });
            foreach (var v in vars["body"]!["variables"]!.AsArray())
            {
                result[v!["name"]!.GetValue<string>()] = v["value"]!.GetValue<string>();
            }
        }

        return result;
    }
}
