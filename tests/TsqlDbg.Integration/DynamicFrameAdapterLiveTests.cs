using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;

namespace TsqlDbg.Integration;

// A58 (§11.6) adapter lane: a dynamic frame's SURFACE is only observable over the real DAP wire —
// Core's RunToEndAsync never steps in, so it cannot see the call-stack entry, the read-only
// virtual document, or a breakpoint bound inside dynamic SQL (the same adapter-only lesson as
// A44/A54/A56). Drives the REAL compiled adapter over stdio via DapStdioHarness.
//
//   1. Stepping into `EXEC sp_executesql` puts a `dynamic SQL #<hash>` frame on the stack whose
//      Source is a `tsqldbg:` virtual document, and the custom `tsqldbg_source` request serves
//      the runtime text back byte-exactly.
//   2. The identity is a CONTENT hash, so a breakpoint set inside the dynamic text re-binds on a
//      LATER execution of the same string — the whole reason for hashing the content rather than
//      minting a per-activation id. Proven by hitting it on a loop's second iteration.
public sealed class DynamicFrameAdapterLiveTests
{
    private const string ConnEnvVar = "TSQLDBG_TEST_CONN";

    // The dynamic text is 2 statements so a breakpoint can land on its SECOND line — a line that
    // exists only inside the string, nowhere on disk.
    private const string Script = """
        DECLARE @i int = 1;
        DECLARE @sql nvarchar(200) = N'PRINT N''in-dynamic'';
        SELECT @i AS iter;';
        WHILE @i <= 2
        BEGIN
            EXEC sp_executesql @sql, N'@i int', @i = @i;
            SET @i = @i + 1;
        END
        SELECT 'done' AS marker;
        """;

    private const int DynamicSelectLine = 2;      // `SELECT @i AS iter;` — line 2 OF THE DYNAMIC TEXT

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

    private static async Task<DapStdioHarness> LaunchScriptAsync(
        string connectionString, string tracePath, string scriptPath, string clientId)
    {
        var csb = new SqlConnectionStringBuilder(connectionString);
        var targetsFile = Path.Combine(AppContext.BaseDirectory, "targets.json");

        var dap = DapStdioHarness.Launch(tracePath);
        await dap.SendRequestAsync("initialize", new JsonObject { ["clientID"] = clientId, ["adapterID"] = "tsqldbg" });
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

    private static async Task<JsonNode> TopFrameAsync(DapStdioHarness dap)
    {
        var response = await dap.SendRequestAsync("stackTrace", new JsonObject { ["threadId"] = 1 });
        return response["body"]!["stackFrames"]!.AsArray()[0]!;
    }

    private static async Task<int> DepthAsync(DapStdioHarness dap)
    {
        var response = await dap.SendRequestAsync("stackTrace", new JsonObject { ["threadId"] = 1 });
        return response["body"]!["stackFrames"]!.AsArray().Count;
    }

    [SkippableFact]
    public async Task StepIntoSpExecuteSql_ShowsDynamicFrame_AndServesItsTextAsAVirtualDocument()
    {
        var (connString, tracePath, scriptPath) = SkipUnlessLive("a58-dynamic-frame.jsonl");
        await using var dap = await LaunchScriptAsync(connString!, tracePath, scriptPath, "a58-dynamic-frame-live-test");
        await dap.SendRequestAsync("configurationDone", new JsonObject());
        await dap.WaitForEventAsync("stopped", e => e["body"]!["reason"]!.GetValue<string>() == "entry");

        // Walk to the EXEC (the DECLAREs and the WHILE precede it), then step INTO it.
        for (var i = 0; i < 8 && await DepthAsync(dap) == 1; i++)
        {
            await dap.SendRequestAsync("stepIn", new JsonObject { ["threadId"] = 1 });
            await dap.WaitForEventAsync("stopped");
        }

        Assert.Equal(2, await DepthAsync(dap));                     // a frame was pushed — into dynamic SQL
        var frame = await TopFrameAsync(dap);

        // The call stack names it as dynamic SQL, not as a module (it is not one) and not as the
        // .sql file (the text is not in it).
        var frameName = frame["name"]!.GetValue<string>();
        Assert.Contains("dynamic SQL #", frameName);

        // Its Source is the read-only tsqldbg: virtual document, carrying the reserved __dyn token.
        var path = frame["source"]!["path"]!.GetValue<string>();
        Assert.StartsWith("tsqldbg:", path);
        Assert.Contains("__dyn.", path);

        // …and the custom tsqldbg_source request serves the RUNTIME TEXT back — the string the
        // server is actually executing, which exists nowhere on disk or in the catalog.
        var source = await dap.SendRequestAsync("tsqldbg_source", new JsonObject { ["path"] = path });
        var content = source["body"]!["content"]!.GetValue<string>();
        Assert.Equal("PRINT N'in-dynamic';\nSELECT @i AS iter;", content.Replace("\r\n", "\n"));

        await dap.SendRequestAsync("continue", new JsonObject { ["threadId"] = 1 });
        await dap.WaitForEventAsync("terminated", timeout: TimeSpan.FromSeconds(20));
        await dap.DisposeAsync();

        // §19 (definition of done): the trace stays parseable. A dynamic frame's module display
        // carries spaces and a '#', and it rides a frame.push trace event — so this is exactly the
        // kind of change that can quietly corrupt the debugger's own debugger.
        var traceLines = await File.ReadAllLinesAsync(tracePath);
        Assert.NotEmpty(traceLines);
        Assert.DoesNotContain(traceLines, line => !TryParseJsonLine(line));
        Assert.Contains(traceLines, line => line.Contains("frame.push") && line.Contains("dynamic SQL"));
    }

    private static bool TryParseJsonLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return true;
        }

        try
        {
            _ = JsonNode.Parse(line);
            return true;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    [SkippableFact]
    public async Task BreakpointInsideDynamicSql_RebindsOnALaterExecutionOfTheSameText()
    {
        // The payoff of hashing the CONTENT: the same dynamic string executed again is the same
        // document, so a breakpoint set in it during iteration 1 is still bound in iteration 2.
        // A per-activation identity would silently drop it.
        var (connString, tracePath, scriptPath) = SkipUnlessLive("a58-dynamic-breakpoint.jsonl");
        await using var dap = await LaunchScriptAsync(connString!, tracePath, scriptPath, "a58-dynamic-bp-live-test");
        await dap.SendRequestAsync("configurationDone", new JsonObject());
        await dap.WaitForEventAsync("stopped", e => e["body"]!["reason"]!.GetValue<string>() == "entry");

        for (var i = 0; i < 8 && await DepthAsync(dap) == 1; i++)
        {
            await dap.SendRequestAsync("stepIn", new JsonObject { ["threadId"] = 1 });
            await dap.WaitForEventAsync("stopped");
        }

        Assert.Equal(2, await DepthAsync(dap));                     // inside the dynamic frame, iteration 1
        var path = (await TopFrameAsync(dap))["source"]!["path"]!.GetValue<string>();

        // Set a breakpoint on line 2 of the dynamic TEXT — a line that exists only in the string.
        var setResponse = await dap.SendRequestAsync("setBreakpoints", new JsonObject
        {
            ["source"] = new JsonObject { ["path"] = path },
            ["breakpoints"] = new JsonArray { new JsonObject { ["line"] = DynamicSelectLine } },
        });
        Assert.True(setResponse["body"]!["breakpoints"]!.AsArray()[0]!["verified"]!.GetValue<bool>());

        // Run on. The frame we are in pops at the end of iteration 1; iteration 2 pushes a FRESH
        // dynamic frame over the same text — and the breakpoint must bind to it.
        await dap.SendRequestAsync("continue", new JsonObject { ["threadId"] = 1 });
        var stopped = await dap.WaitForEventAsync("stopped",
            e => e["body"]!["reason"]!.GetValue<string>() == "breakpoint", TimeSpan.FromSeconds(20));
        Assert.NotNull(stopped);

        var frame = await TopFrameAsync(dap);
        Assert.Equal(2, await DepthAsync(dap));                     // stopped INSIDE dynamic SQL again
        Assert.Contains("dynamic SQL #", frame["name"]!.GetValue<string>());
        Assert.Equal(DynamicSelectLine, frame["line"]!.GetValue<int>());
        Assert.Equal(path, frame["source"]!["path"]!.GetValue<string>());   // the SAME document — content-keyed

        await dap.SendRequestAsync("continue", new JsonObject { ["threadId"] = 1 });
        await dap.WaitForEventAsync("terminated", timeout: TimeSpan.FromSeconds(20));
    }
}
