using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Xunit;

namespace TsqlDbg.Integration;

// A73 (§17/§24.3): the traceRun launch mode over the real DAP wire — a launch that never
// stops (stopOnEntry set true is deliberately ignored), streams one console line per
// executed statement (step-into included: a dynamic frame's inner statements appear),
// writes the §24.8 JSONL file (startedFrom:"launch"), and terminates on its own with the
// default rollback.
public sealed class A73TraceRunLiveTests
{
    private const string ConnEnvVar = "TSQLDBG_TEST_CONN";

    [SkippableFact]
    public async Task TraceRun_Into_StreamsTrace_WritesFile_NeverStops_AndTerminates()
    {
        var raw = Environment.GetEnvironmentVariable(ConnEnvVar);
        Skip.If(string.IsNullOrWhiteSpace(raw), $"{ConnEnvVar} is not set; skipping live probe (never fake a pass — CLAUDE.md).");

        var csb = new SqlConnectionStringBuilder(raw);
        var tracePath = Path.Combine(Path.GetTempPath(), "a73-tracerun-adapter.jsonl");
        var runTracePath = Path.Combine(Path.GetTempPath(), "a73-tracerun-file.jsonl");
        foreach (var p in new[] { tracePath, runTracePath })
        {
            if (File.Exists(p))
            {
                File.Delete(p);
            }
        }

        var scriptPath = Path.Combine(Path.GetTempPath(), "a73-tracerun.sql");
        await File.WriteAllTextAsync(scriptPath,
            "DECLARE @a int = 1;\n" +           // L1: baseline {@a=1}
            "SET @a = @a + 1;\n" +              // L2: delta {@a=2}
            "EXEC('SELECT 3 AS x;');\n" +       // L3: stepMode:into pushes the dynamic frame (A58)
            "PRINT 'done';\n");                 // L4
        var targetsFile = Path.Combine(AppContext.BaseDirectory, "targets.json");

        await using var dap = DapStdioHarness.Launch(tracePath);
        await dap.SendRequestAsync("initialize", new JsonObject { ["clientID"] = "a73", ["adapterID"] = "tsqldbg" });
        await dap.SendRequestAsync("launch", new JsonObject
        {
            ["server"] = csb.DataSource,
            ["database"] = csb.InitialCatalog,
            ["mode"] = "script",
            ["script"] = scriptPath,
            ["targetsFile"] = targetsFile,
            ["stopOnEntry"] = true,             // deliberately set: trace mode must ignore it
            ["traceRun"] = new JsonObject { ["stepMode"] = "into", ["file"] = runTracePath },
        });
        await dap.WaitForEventAsync("initialized");
        await dap.SendRequestAsync("configurationDone", new JsonObject());

        // Everything up to termination, in order — lets us assert what did NOT happen (no stops).
        var events = await dap.CollectEventsUntilAsync("terminated", timeout: TimeSpan.FromSeconds(60));

        Assert.DoesNotContain(events, e => e["event"]?.GetValue<string>() == "stopped");

        var outputTexts = events
            .Where(e => e["event"]?.GetValue<string>() == "output")
            .Select(e => e["body"]!["output"]!.GetValue<string>())
            .ToList();
        var allOutput = string.Concat(outputTexts);

        // The per-statement console projection: outer statements, the inner dynamic statement
        // (stepMode:into), the variable delta, the debuggee's PRINT, and the summary + file path.
        Assert.Contains("DECLARE @a int = 1;", allOutput);
        Assert.Contains("SELECT 3 AS x;", allOutput);
        Assert.Contains("{@a=2}", allOutput);
        Assert.Contains("done", allOutput);
        Assert.Contains("── trace completed:", allOutput);
        Assert.Contains("rolled back", allOutput);
        Assert.Contains(runTracePath, allOutput);

        // The source-linked step line: at least one console output event carries Source+Line
        // (the click-to-navigate contract).
        Assert.Contains(events, e => e["event"]?.GetValue<string>() == "output"
            && e["body"]!["source"]?["path"]?.GetValue<string>() == scriptPath
            && e["body"]!["line"] is not null);

        // The §24.8 machine record: header (startedFrom:"launch", no sessionId), one step line
        // per statement with the changed-mode deltas, a completed/not-committed summary.
        var lines = File.ReadLines(runTracePath).Select(l => JsonDocument.Parse(l).RootElement).ToArray();
        var header = lines[0];
        Assert.Equal("header", header.GetProperty("kind").GetString());
        Assert.Equal("launch", header.GetProperty("startedFrom").GetString());
        Assert.Equal("into", header.GetProperty("stepMode").GetString());
        Assert.False(header.GetProperty("session").TryGetProperty("sessionId", out _));

        var steps = lines.Where(e => e.GetProperty("kind").GetString() == "step").ToArray();
        Assert.True(steps.Length >= 4, $"expected at least 4 step lines, got {steps.Length}");
        Assert.Equal("1", steps[0].GetProperty("variablesChanged").GetProperty("@a").GetString());
        Assert.Equal("2", steps[1].GetProperty("variablesChanged").GetProperty("@a").GetString());
        Assert.Contains(steps, s => (s.GetProperty("statement").GetString() ?? string.Empty).Contains("SELECT 3 AS x"));

        var summary = lines[^1];
        Assert.Equal("summary", summary.GetProperty("kind").GetString());
        Assert.Equal("completed", summary.GetProperty("finalState").GetString());
        Assert.False(summary.GetProperty("committed").GetBoolean());
        Assert.Equal(steps.Length, summary.GetProperty("steps").GetInt32());

        await dap.DisposeAsync();
    }
}
