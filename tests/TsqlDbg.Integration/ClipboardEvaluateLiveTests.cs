using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Xunit;

namespace TsqlDbg.Integration;

// A71 (§12.4/§18): evaluate context:"clipboard" — VS Code's Copy Value gesture. Without
// supportsClipboardContext the client copies the RENDERED row text, so a watch that
// overflowed the shared budget copies the literal placeholder "⏱ (click to evaluate)"
// (the reported symptom). Clipboard evaluation resolves variables/temp objects like
// hover, and treats anything else as the §12.4 click-to-evaluate class: the watch
// pipeline OUTSIDE the shared budget. Pinned over the real DAP wire; watchBudgetMs:0
// makes every watch-context request overflow deterministically.
public sealed class ClipboardEvaluateLiveTests
{
    private const string ConnEnvVar = "TSQLDBG_TEST_CONN";

    private static async Task StepAsync(DapStdioHarness dap)
    {
        await dap.SendRequestAsync("next", new JsonObject { ["threadId"] = 1 });
        await dap.WaitForEventAsync("stopped");
    }

    private static async Task<int> TopFrameIdAsync(DapStdioHarness dap)
    {
        var frames = await dap.SendRequestAsync("stackTrace", new JsonObject { ["threadId"] = 1 });
        return frames["body"]!["stackFrames"]!.AsArray()[0]!["id"]!.GetValue<int>();
    }

    private static async Task<string> EvaluateAsync(DapStdioHarness dap, string context, string expression)
    {
        var eval = await dap.SendRequestAsync("evaluate", new JsonObject
        {
            ["expression"] = expression,
            ["context"] = context,
            ["frameId"] = await TopFrameIdAsync(dap),
        });
        return eval["body"]!["result"]!.GetValue<string>();
    }

    private static async Task<JsonNode> ScopeVariableAsync(DapStdioHarness dap, string scopeName, string variableName)
    {
        var scopes = await dap.SendRequestAsync("scopes", new JsonObject { ["frameId"] = await TopFrameIdAsync(dap) });
        var scopeRef = scopes["body"]!["scopes"]!.AsArray()
            .First(s => s!["name"]!.GetValue<string>() == scopeName)!["variablesReference"]!.GetValue<int>();
        var vars = await dap.SendRequestAsync("variables", new JsonObject { ["variablesReference"] = scopeRef });
        return vars["body"]!["variables"]!.AsArray()
            .First(v => v!["name"]!.GetValue<string>() == variableName)!;
    }

    [SkippableFact]
    public async Task CopyValue_ClipboardContext_ReturnsRealValues_NotThePlaceholder()
    {
        var raw = Environment.GetEnvironmentVariable(ConnEnvVar);
        Skip.If(string.IsNullOrWhiteSpace(raw), $"{ConnEnvVar} is not set; skipping live probe (never fake a pass — CLAUDE.md).");

        var csb = new SqlConnectionStringBuilder(raw);
        var tracePath = Path.Combine(Path.GetTempPath(), "a71-clipboard-evaluate.jsonl");
        if (File.Exists(tracePath))
        {
            File.Delete(tracePath);
        }

        var scriptPath = Path.Combine(Path.GetTempPath(), "a71-clipboard-evaluate.sql");
        await File.WriteAllTextAsync(scriptPath,
            "DECLARE @x int = 41;\n" +
            "DECLARE @tv TABLE (v int);\n" +
            "INSERT INTO @tv (v) VALUES (1), (2);\n" +
            "SELECT @x;\n");
        var targetsFile = Path.Combine(AppContext.BaseDirectory, "targets.json");

        await using var dap = DapStdioHarness.Launch(tracePath);
        var init = await dap.SendRequestAsync("initialize", new JsonObject { ["clientID"] = "a71", ["adapterID"] = "tsqldbg" });

        // A71: the capability is what makes VS Code send clipboard requests at all.
        Assert.True(init["body"]!["supportsClipboardContext"]!.GetValue<bool>());

        await dap.SendRequestAsync("launch", new JsonObject
        {
            ["server"] = csb.DataSource,
            ["database"] = csb.InitialCatalog,
            ["mode"] = "script",
            ["script"] = scriptPath,
            ["targetsFile"] = targetsFile,
            ["stopOnEntry"] = true,
            ["watchBudgetMs"] = 0,
        });
        await dap.WaitForEventAsync("initialized");
        await dap.SendRequestAsync("configurationDone", new JsonObject());
        await dap.WaitForEventAsync("stopped", e => e["body"]!["reason"]!.GetValue<string>() == "entry");

        await StepAsync(dap);   // DECLARE @x int = 41
        await StepAsync(dap);   // DECLARE @tv TABLE (v int)
        await StepAsync(dap);   // INSERT @tv (1),(2); now stopped at SELECT

        // Baseline symptom: with the budget exhausted (0 ms), a watch renders the
        // placeholder — the exact string the pre-A71 Copy Value put on the clipboard.
        Assert.Equal("⏱ (click to evaluate)", await EvaluateAsync(dap, "watch", "@x + 1"));

        // A71 payoff: the SAME expression through context:"clipboard" evaluates for
        // real, outside the budget.
        Assert.Equal("42", await EvaluateAsync(dap, "clipboard", "@x + 1"));

        // Variable token → snapshot value (the hover-shared resolution).
        Assert.Equal("41", await EvaluateAsync(dap, "clipboard", "@x"));

        // Locals rows carry evaluateName (what VS Code sends back for Copy Value).
        var local = await ScopeVariableAsync(dap, "Locals", "@x");
        Assert.Equal("@x", local["evaluateName"]!.GetValue<string>());

        // Temp Tables rows carry the PHYSICAL name as evaluateName; clipboard must
        // resolve it back to the rendered rowcount, not shove it through the watch
        // pipeline as a scalar.
        var tempRow = await ScopeVariableAsync(dap, "Temp Tables", "@tv");
        var physicalName = tempRow["evaluateName"]!.GetValue<string>();
        Assert.Contains("2 rows", await EvaluateAsync(dap, "clipboard", physicalName));

        await dap.DisposeAsync();
    }
}
