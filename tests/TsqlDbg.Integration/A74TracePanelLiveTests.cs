using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Xunit;

namespace TsqlDbg.Integration;

// A74 (§17): traceRun view:"panel" over the real DAP wire — the per-step console lines are
// replaced by the tsqldbg_traceStart/Step/Summary custom-event stream (step bodies = the
// §24.8 file lines verbatim + the adapter-added `source`), while the intro line, the
// end-of-run console block, and the JSONL file stay exactly as in console view.
public sealed class A74TracePanelLiveTests
{
    private const string ConnEnvVar = "TSQLDBG_TEST_CONN";

    [SkippableFact]
    public async Task TraceRun_PanelView_StreamsEvents_KeepsConsoleQuiet_AndWritesFile()
    {
        var raw = Environment.GetEnvironmentVariable(ConnEnvVar);
        Skip.If(string.IsNullOrWhiteSpace(raw), $"{ConnEnvVar} is not set; skipping live probe (never fake a pass — CLAUDE.md).");

        var csb = new SqlConnectionStringBuilder(raw);
        var tracePath = Path.Combine(Path.GetTempPath(), "a74-tracepanel-adapter.jsonl");
        var runTracePath = Path.Combine(Path.GetTempPath(), "a74-tracepanel-file.jsonl");
        foreach (var p in new[] { tracePath, runTracePath })
        {
            if (File.Exists(p))
            {
                File.Delete(p);
            }
        }

        var scriptPath = Path.Combine(Path.GetTempPath(), "a74-tracepanel.sql");
        await File.WriteAllTextAsync(scriptPath,
            "DECLARE @a int = 1;\n" +           // L1: baseline {@a=1}
            "SET @a = @a + 1;\n" +              // L2: delta {@a=2}
            "EXEC('SELECT 3 AS x;');\n" +       // L3: stepMode:into pushes the dynamic frame (A58)
            "PRINT 'done';\n");                 // L4
        var targetsFile = Path.Combine(AppContext.BaseDirectory, "targets.json");

        await using var dap = DapStdioHarness.Launch(tracePath);
        await dap.SendRequestAsync("initialize", new JsonObject { ["clientID"] = "a74", ["adapterID"] = "tsqldbg" });
        await dap.SendRequestAsync("launch", new JsonObject
        {
            ["server"] = csb.DataSource,
            ["database"] = csb.InitialCatalog,
            ["mode"] = "script",
            ["script"] = scriptPath,
            ["targetsFile"] = targetsFile,
            ["traceRun"] = new JsonObject
            {
                ["stepMode"] = "into",
                ["file"] = runTracePath,
                ["view"] = "panel",
            },
        });
        await dap.WaitForEventAsync("initialized");
        await dap.SendRequestAsync("configurationDone", new JsonObject());

        var events = await dap.CollectEventsUntilAsync("terminated", timeout: TimeSpan.FromSeconds(60));

        Assert.DoesNotContain(events, e => e["event"]?.GetValue<string>() == "stopped");

        // The tsqldbg_traceStart envelope: run metadata + where the machine record lands.
        var start = Assert.Single(events, e => e["event"]?.GetValue<string>() == "tsqldbg_traceStart");
        Assert.Equal("into", start["body"]!["stepMode"]!.GetValue<string>());
        Assert.Equal("changed", start["body"]!["variableCapture"]!.GetValue<string>());
        Assert.Equal("script", start["body"]!["mode"]!.GetValue<string>());
        Assert.Equal(runTracePath, start["body"]!["filePath"]!.GetValue<string>());

        // The step stream: §24.8 step-line bodies. The outer statements carry a navigable
        // `source` (real file); the stepped-into dynamic statement must NOT (its only source
        // is a tsqldbg: virtual doc, dead once the session tears down — A73 review MED-1).
        var steps = events.Where(e => e["event"]?.GetValue<string>() == "tsqldbg_traceStep").ToList();
        Assert.True(steps.Count >= 4, $"expected at least 4 step events, got {steps.Count}");
        Assert.Equal("step", steps[0]["body"]!["kind"]!.GetValue<string>());
        Assert.Equal("1", steps[0]["body"]!["variablesChanged"]!["@a"]!.GetValue<string>());
        Assert.Equal("2", steps[1]["body"]!["variablesChanged"]!["@a"]!.GetValue<string>());
        Assert.Equal(scriptPath, steps[0]["body"]!["source"]!["path"]!.GetValue<string>());
        Assert.Equal(1, steps[0]["body"]!["source"]!["line"]!.GetValue<int>());
        // The outer EXEC('…') statement text also contains the inner SELECT — the dynamic
        // frame's own step is the one whose statement ISN'T the EXEC wrapper.
        var dynamicStep = steps.Single(s =>
        {
            var statement = s["body"]!["statement"]?.GetValue<string>() ?? string.Empty;
            return statement.Contains("SELECT 3 AS x") && !statement.Contains("EXEC", StringComparison.OrdinalIgnoreCase);
        });
        Assert.Null(dynamicStep["body"]!["source"]);

        // A74 rider: the panel event carries the pre-step call stack (bottom → top, callers
        // at their call-site line) and the file carries frame.depth. The outer statements
        // are a single root entry; the dynamic frame's step chains root:3 → dynamic:1.
        var rootStack = steps[0]["body"]!["stack"]!.AsArray();
        Assert.Single(rootStack);
        Assert.Equal(0, steps[0]["body"]!["frame"]!["depth"]!.GetValue<int>());
        var dynamicStack = dynamicStep["body"]!["stack"]!.AsArray();
        Assert.Equal(2, dynamicStack.Count);
        Assert.Equal(3, dynamicStack[0]!["line"]!.GetValue<int>());              // the EXEC call site
        Assert.Equal(rootStack[0]!["module"]!.GetValue<string>(), dynamicStack[0]!["module"]!.GetValue<string>());
        Assert.Equal(dynamicStep["body"]!["frame"]!["id"]!.GetValue<int>(), dynamicStack[1]!["id"]!.GetValue<int>());
        Assert.Equal(1, dynamicStep["body"]!["frame"]!["depth"]!.GetValue<int>());
        var printStep = steps.Single(s =>
            (s["body"]!["statement"]?.GetValue<string>() ?? string.Empty).Contains("PRINT 'done'"));
        Assert.Contains("done", printStep["body"]!["output"]!.AsArray().Select(n => n!.GetValue<string>()));

        // Nested keys stay PascalCase on the WIRE too (§24.8 contract the panel JS reads):
        // the dynamic SELECT's result set rides its step event as Columns/Rows/Truncated.
        var dynamicResultSet = dynamicStep["body"]!["resultSets"]!.AsArray().Single()!;
        Assert.Equal("x", dynamicResultSet["Columns"]!.AsArray().Single()!.GetValue<string>());
        Assert.Equal("3", dynamicResultSet["Rows"]!.AsArray().Single()!.AsArray().Single()!.GetValue<string>());
        Assert.False(dynamicResultSet["Truncated"]!.GetValue<bool>());

        // The tsqldbg_traceSummary envelope: §24.8 summary body + filePath.
        var summaryEvt = Assert.Single(events, e => e["event"]?.GetValue<string>() == "tsqldbg_traceSummary");
        Assert.Equal("completed", summaryEvt["body"]!["finalState"]!.GetValue<string>());
        Assert.False(summaryEvt["body"]!["committed"]!.GetValue<bool>());
        Assert.Equal(steps.Count, summaryEvt["body"]!["steps"]!.GetValue<int>());
        Assert.Equal(runTracePath, summaryEvt["body"]!["filePath"]!.GetValue<string>());

        // Console hygiene: the panel replaces the per-step lines — no step line, no per-step
        // variable delta on the console; the intro line and the end-of-run block remain.
        var allOutput = string.Concat(events
            .Where(e => e["event"]?.GetValue<string>() == "output")
            .Select(e => e["body"]!["output"]!.GetValue<string>()));
        Assert.Contains("Trace run (stepMode: into", allOutput);
        Assert.Contains("── trace completed:", allOutput);
        Assert.Contains("rolled back", allOutput);
        Assert.Contains(runTracePath, allOutput);
        Assert.DoesNotContain("{@a=2}", allOutput);
        Assert.DoesNotContain("SELECT 3 AS x", allOutput);

        // The §24.8 machine record is view-independent — same file as console view writes.
        var rawLines = File.ReadAllLines(runTracePath);
        var lines = rawLines.Select(l => JsonDocument.Parse(l).RootElement).ToArray();
        Assert.Equal("header", lines[0].GetProperty("kind").GetString());
        Assert.Equal("launch", lines[0].GetProperty("startedFrom").GetString());
        var fileStepLines = rawLines.Where(l => JsonDocument.Parse(l).RootElement.GetProperty("kind").GetString() == "step").ToArray();
        Assert.Equal(steps.Count, fileStepLines.Length);
        Assert.Equal("summary", lines[^1].GetProperty("kind").GetString());
        Assert.Equal("completed", lines[^1].GetProperty("finalState").GetString());

        // The reparse-verbatim contract (§17 A74 + review MED-1): each step EVENT body,
        // minus the adapter-added `source` and `stack`, is node-equal to the corresponding
        // FILE line — a datetime-coercing reparse or any second projection would break this.
        for (var i = 0; i < steps.Count; i++)
        {
            var eventBody = steps[i]["body"]!.DeepClone().AsObject();
            eventBody.Remove("source");
            eventBody.Remove("stack");
            Assert.True(
                JsonNode.DeepEquals(eventBody, JsonNode.Parse(fileStepLines[i])),
                $"step event {i} body diverges from file line: {eventBody.ToJsonString()} vs {fileStepLines[i]}");
        }

        await dap.DisposeAsync();
    }
}
