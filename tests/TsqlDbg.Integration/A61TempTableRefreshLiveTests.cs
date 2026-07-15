using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Xunit;

namespace TsqlDbg.Integration;

// A61 (§12.3): editing a temp object's CONTENTS via the Debug Console must refresh the
// Variables pane's Temp Tables rowcount. A46 already persisted the write to the real
// backing object, and a console read saw it — but the Temp Tables scope caches each
// object's "(N rows)" display ONCE per stop (§12.1 fill-once per epoch), so within the
// same stop the pane kept showing the stale count. The reported gesture: `DELETE TOP(1)
// FROM @tv` leaves the pane showing one row too many. A61 drops that cache and emits
// `invalidated` (variables) so the pane re-reads the live count. This pins it over the
// real DAP wire — the fidelity harness (RunToEnd, no console, no `variables` requests)
// cannot see this at all (the A44/A46/A56 lesson).
public sealed class A61TempTableRefreshLiveTests
{
    private const string ConnEnvVar = "TSQLDBG_TEST_CONN";

    private static async Task StepAsync(DapStdioHarness dap)
    {
        await dap.SendRequestAsync("next", new JsonObject { ["threadId"] = 1 });
        await dap.WaitForEventAsync("stopped");
    }

    private static async Task<string> EvaluateReplAsync(DapStdioHarness dap, string expression)
    {
        var frames = await dap.SendRequestAsync("stackTrace", new JsonObject { ["threadId"] = 1 });
        var frameId = frames["body"]!["stackFrames"]!.AsArray()[0]!["id"]!.GetValue<int>();
        var eval = await dap.SendRequestAsync("evaluate", new JsonObject
        {
            ["expression"] = expression,
            ["context"] = "repl",
            ["frameId"] = frameId,
        });
        return eval["body"]!["result"]!.GetValue<string>();
    }

    // Reads the Temp Tables scope entry's displayed value ("(N rows)") for a given object,
    // exactly as the Variables pane does: stackTrace -> scopes -> the "Temp Tables" scope's
    // variablesReference -> variables -> the entry whose name is the original object name.
    private static async Task<string> TempTableValueAsync(DapStdioHarness dap, string originalName)
    {
        var frames = await dap.SendRequestAsync("stackTrace", new JsonObject { ["threadId"] = 1 });
        var frameId = frames["body"]!["stackFrames"]!.AsArray()[0]!["id"]!.GetValue<int>();
        var scopes = await dap.SendRequestAsync("scopes", new JsonObject { ["frameId"] = frameId });
        var tempRef = scopes["body"]!["scopes"]!.AsArray()
            .First(s => s!["name"]!.GetValue<string>() == "Temp Tables")!["variablesReference"]!.GetValue<int>();
        var vars = await dap.SendRequestAsync("variables", new JsonObject { ["variablesReference"] = tempRef });
        return vars["body"]!["variables"]!.AsArray()
            .First(v => v!["name"]!.GetValue<string>() == originalName)!["value"]!.GetValue<string>();
    }

    private static bool IsVariablesInvalidation(JsonNode e)
        => e["event"]?.GetValue<string>() == "invalidated"
           && (e["body"]?["areas"]?.AsArray().Any(a => a!.GetValue<string>() == "variables") ?? false);

    [SkippableFact]
    public async Task WriteMode_ConsoleTableVariableDelete_RefreshesTempTablesRowcount()
    {
        var raw = Environment.GetEnvironmentVariable(ConnEnvVar);
        Skip.If(string.IsNullOrWhiteSpace(raw), $"{ConnEnvVar} is not set; skipping live probe (never fake a pass — CLAUDE.md).");

        var csb = new SqlConnectionStringBuilder(raw);
        var tracePath = Path.Combine(Path.GetTempPath(), "a61-temp-refresh.jsonl");
        if (File.Exists(tracePath))
        {
            File.Delete(tracePath);
        }

        var scriptPath = Path.Combine(Path.GetTempPath(), "a61-temp-refresh.sql");
        await File.WriteAllTextAsync(scriptPath,
            "DECLARE @tv TABLE (v int);\n" +
            "INSERT INTO @tv (v) VALUES (1), (2);\n" +
            "SELECT v FROM @tv;\n");
        var targetsFile = Path.Combine(AppContext.BaseDirectory, "targets.json");

        await using var dap = DapStdioHarness.Launch(tracePath);
        await dap.SendRequestAsync("initialize", new JsonObject { ["clientID"] = "a61", ["adapterID"] = "tsqldbg" });
        await dap.SendRequestAsync("launch", new JsonObject
        {
            ["server"] = csb.DataSource,
            ["database"] = csb.InitialCatalog,
            ["mode"] = "script",
            ["script"] = scriptPath,
            ["targetsFile"] = targetsFile,
            ["stopOnEntry"] = true,
            ["allowConsoleWrites"] = true,
        });
        await dap.WaitForEventAsync("initialized");
        await dap.SendRequestAsync("configurationDone", new JsonObject());
        await dap.WaitForEventAsync("stopped", e => e["body"]!["reason"]!.GetValue<string>() == "entry");

        await StepAsync(dap);   // DECLARE @tv TABLE (v int)
        await StepAsync(dap);   // INSERT @tv (1),(2); now stopped at SELECT with @tv holding 2 rows

        // Baseline: the pane reads "(2 rows)". This ALSO fills the fill-once display cache
        // for @tv in the current stop — the exact cache A61 must drop.
        Assert.Contains("2 rows", await TempTableValueAsync(dap, "@tv"));

        // The reported gesture: delete one row via the console (same stop, no step between).
        var deleteRendered = await EvaluateReplAsync(dap, "DELETE TOP(1) FROM @tv;");
        Assert.DoesNotContain("faulted", deleteRendered);

        // A61: the adapter emitted `invalidated` (variables) so the client refetches now.
        await dap.WaitForEventAsync("invalidated", IsVariablesInvalidation, TimeSpan.FromSeconds(5));

        // The payoff: a re-read in the SAME stop now shows the live count, not the cached
        // one. Before A61 this returned "(2 rows)" (a fill-once cache hit).
        var afterDelete = await TempTableValueAsync(dap, "@tv");
        Assert.Contains("1 rows", afterDelete);
        Assert.DoesNotContain("2 rows", afterDelete);

        await dap.DisposeAsync();
    }
}
