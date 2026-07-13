using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;

namespace TsqlDbg.Integration;

// A54 (§6/§11.5) adapter lane: the implicit-return inspection stop is ONLY observable over
// the DAP wire — Core's RunToEndAsync (fidelity) runs THROUGH the park and never sees the
// extra stop (the same "adapter-only" lesson as A44's MultibatchAdapterLiveTests). Drives
// the REAL compiled adapter over stdio via DapStdioHarness against a deployed proc.
//   1. Stepping (next) off the last statement of a stepped-into MODULE parks at the proc's
//      end — the callee frame is STILL on the stack (depth unchanged), its final locals are
//      readable in its own scope — and the NEXT step performs the §11.5 pop to the caller.
//   2. stepOut runs straight THROUGH the implicit return to the caller (only next/stepIn
//      land on it), matching Ivan's "Step Out returns straight to the caller" ruling.
public sealed class ImplicitReturnStopAdapterLiveTests
{
    private const string ConnEnvVar = "TSQLDBG_TEST_CONN";

    // A body-end callee (NO explicit RETURN) with a local, so its final value proves the
    // state table is intact at the park. Lines: 1 CREATE, 2 BEGIN, 3 DECLARE @y,
    // 4 SET @y, 5 SELECT @y (the LAST body statement), 6 END.
    private const string InnerProcDefinition = """
        CREATE PROCEDURE dbo.a54_return_inner @x int AS
        BEGIN
            DECLARE @y int;
            SET @y = @x + 1;
            SELECT @y AS result;
        END
        """;

    private const int InnerLastStatementLine = 5;

    // The launch script (script mode) steps INTO the callee, then has a following statement
    // so the post-pop landing is unambiguous. Lines: 1 EXEC, 2 SELECT 'after'.
    private const string Script = "EXEC dbo.a54_return_inner 10;\nSELECT 'after' AS marker;\n";
    private const int CallerFollowingLine = 2;

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

    [SkippableFact]
    public async Task StepInto_RunOffBodyEnd_ParksAtImplicitReturn_ThenReturnsToCaller()
    {
        var (connString, tracePath, scriptPath) = SkipUnlessLive("a54-implicit-return-stop.jsonl");
        await DeployProcedureAsync(connString!);
        try
        {
            await using var dap = await LaunchScriptAsync(connString!, tracePath, scriptPath, "a54-implicit-return-live-test");
            await dap.SendRequestAsync("configurationDone", new JsonObject());
            await dap.WaitForEventAsync("stopped", e => e["body"]!["reason"]!.GetValue<string>() == "entry");
            Assert.Equal(1, await DepthAsync(dap));                     // the script frame, at the EXEC (line 1)

            // Step INTO the callee, then walk to its LAST statement (line 5) — robust to
            // however many stoppable SUs precede it.
            await dap.SendRequestAsync("stepIn", new JsonObject { ["threadId"] = 1 });
            await dap.WaitForEventAsync("stopped");
            Assert.Equal(2, await DepthAsync(dap));                     // pushed into the callee
            for (var i = 0; i < 6 && await LineOfTopFrameAsync(dap) != InnerLastStatementLine; i++)
            {
                await dap.SendRequestAsync("next", new JsonObject { ["threadId"] = 1 });
                await dap.WaitForEventAsync("stopped");
            }
            Assert.Equal(InnerLastStatementLine, await LineOfTopFrameAsync(dap));  // stopped ON the last statement, before it runs
            Assert.Equal(2, await DepthAsync(dap));

            // Execute the last statement. A54: instead of popping straight to the caller,
            // the session PARKS at the implicit return — the callee frame is STILL on the
            // stack (depth stays 2). Pre-A54 this single step would have landed at depth 1.
            await dap.SendRequestAsync("next", new JsonObject { ["threadId"] = 1 });
            await dap.WaitForEventAsync("stopped");
            Assert.Equal(2, await DepthAsync(dap));                     // PARKED — not yet popped (the A54 proof)
            Assert.Contains("a54_return_inner", await NameOfTopFrameAsync(dap));  // still in the callee's own scope

            // The callee's FINAL locals are readable at the park (state table intact): @y = @x + 1.
            var locals = await CollectAllVariablesAsync(dap, await TopFrameIdAsync(dap));
            Assert.Equal("11", locals.GetValueOrDefault("@y"));
            Assert.Equal("10", locals.GetValueOrDefault("@x"));

            // The next step performs the deferred §11.5 pop — back in the caller.
            await dap.SendRequestAsync("next", new JsonObject { ["threadId"] = 1 });
            await dap.WaitForEventAsync("stopped");
            Assert.Equal(1, await DepthAsync(dap));                     // popped to the caller
            Assert.Equal(CallerFollowingLine, await LineOfTopFrameAsync(dap));  // the SELECT after the EXEC

            await dap.SendRequestAsync("continue", new JsonObject { ["threadId"] = 1 });
            await dap.WaitForEventAsync("terminated", timeout: TimeSpan.FromSeconds(15));
            await dap.DisposeAsync();

            // §19: --trace output must parse 0-unparseable.
            var traceLines = await File.ReadAllLinesAsync(tracePath);
            Assert.NotEmpty(traceLines);
            Assert.DoesNotContain(traceLines, l => !TryParseJsonLine(l));
        }
        finally
        {
            await DropProcedureAsync(connString!);
        }
    }

    [SkippableFact]
    public async Task StepOut_RunsStraightThroughImplicitReturn_ToTheCaller()
    {
        var (connString, tracePath, scriptPath) = SkipUnlessLive("a54-implicit-return-stepout.jsonl");
        await DeployProcedureAsync(connString!);
        try
        {
            await using var dap = await LaunchScriptAsync(connString!, tracePath, scriptPath, "a54-implicit-return-stepout-live-test");
            await dap.SendRequestAsync("configurationDone", new JsonObject());
            await dap.WaitForEventAsync("stopped", e => e["body"]!["reason"]!.GetValue<string>() == "entry");

            await dap.SendRequestAsync("stepIn", new JsonObject { ["threadId"] = 1 });
            await dap.WaitForEventAsync("stopped");
            Assert.Equal(2, await DepthAsync(dap));                     // inside the callee

            // stepOut runs THROUGH the implicit return (only next/stepIn stop at it): the VERY
            // NEXT stop is back in the caller, never an intermediate implicit-return stop.
            await dap.SendRequestAsync("stepOut", new JsonObject { ["threadId"] = 1 });
            var stop = await dap.WaitForEventAsync("stopped");
            Assert.Equal(1, await DepthAsync(dap));                     // landed directly in the caller
            Assert.Equal(CallerFollowingLine, await LineOfTopFrameAsync(dap));
            Assert.Equal("step", stop["body"]!["reason"]!.GetValue<string>());

            await dap.SendRequestAsync("continue", new JsonObject { ["threadId"] = 1 });
            await dap.WaitForEventAsync("terminated", timeout: TimeSpan.FromSeconds(15));
            await dap.DisposeAsync();
        }
        finally
        {
            await DropProcedureAsync(connString!);
        }
    }

    private static async Task DeployProcedureAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using (var drop = connection.CreateCommand())
        {
            drop.CommandText = "IF OBJECT_ID('dbo.a54_return_inner') IS NOT NULL DROP PROCEDURE dbo.a54_return_inner;";
            await drop.ExecuteNonQueryAsync();
        }

        await using var create = connection.CreateCommand();
        create.CommandText = InnerProcDefinition;
        await create.ExecuteNonQueryAsync();
    }

    private static async Task DropProcedureAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "IF OBJECT_ID('dbo.a54_return_inner') IS NOT NULL DROP PROCEDURE dbo.a54_return_inner;";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> DepthAsync(DapStdioHarness dap)
    {
        var trace = await dap.SendRequestAsync("stackTrace", new JsonObject { ["threadId"] = 1 });
        return trace["body"]!["stackFrames"]!.AsArray().Count;
    }

    private static async Task<int> LineOfTopFrameAsync(DapStdioHarness dap)
    {
        var trace = await dap.SendRequestAsync("stackTrace", new JsonObject { ["threadId"] = 1 });
        return trace["body"]!["stackFrames"]!.AsArray()[0]!["line"]!.GetValue<int>();
    }

    private static async Task<int> TopFrameIdAsync(DapStdioHarness dap)
    {
        var trace = await dap.SendRequestAsync("stackTrace", new JsonObject { ["threadId"] = 1 });
        return trace["body"]!["stackFrames"]!.AsArray()[0]!["id"]!.GetValue<int>();
    }

    private static async Task<string> NameOfTopFrameAsync(DapStdioHarness dap)
    {
        var trace = await dap.SendRequestAsync("stackTrace", new JsonObject { ["threadId"] = 1 });
        return trace["body"]!["stackFrames"]!.AsArray()[0]!["name"]!.GetValue<string>();
    }

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

    private static bool TryParseJsonLine(string line)
    {
        try
        {
            JsonNode.Parse(line);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
