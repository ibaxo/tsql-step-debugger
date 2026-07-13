using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;

namespace TsqlDbg.Integration;

// M7 hardening (design note §5.1/§5.2, sourceMap hash-compare): live DapStdioHarness
// pins over the real DAP wire. dbo.m7_sourcemap_outer calls dbo.m7_sourcemap_inner;
// the inner proc's server definition is written verbatim to a real workspace file the
// launch `sourceMap` glob points at, so stepping into it should resolve the match at
// its first blueprint fetch (TryStepIntoAsync -> FetchModuleBlueprintAsync) and bind
// breakpoints / stack-frame Source to that real file instead of the tsqldbg: document.
public sealed class SourceMapLiveTests
{
    private const string ConnEnvVar = "TSQLDBG_TEST_CONN";

    // Lines: 1 CREATE PROCEDURE, 2 BEGIN, 3 DECLARE, 4 SET (the breakpoint target),
    // 5 SELECT, 6 END.
    private const string InnerProcDefinition = """
        CREATE PROCEDURE dbo.m7_sourcemap_inner @x int AS
        BEGIN
            DECLARE @y int;
            SET @y = @x + 1;
            SELECT @y AS result;
        END
        """;

    private const string OuterProcDefinition = """
        CREATE PROCEDURE dbo.m7_sourcemap_outer @x int AS
        BEGIN
            EXEC dbo.m7_sourcemap_inner @x;
        END
        """;

    [SkippableFact]
    public async Task BreakpointInAMatchedCalleeFile_BindsAndFires_AtDepthTwo_WithTheRealFileAsSource()
    {
        var raw = Environment.GetEnvironmentVariable(ConnEnvVar);
        Skip.If(string.IsNullOrWhiteSpace(raw), $"{ConnEnvVar} is not set; skipping live probe (never fake a pass — CLAUDE.md).");

        var csb = new SqlConnectionStringBuilder(raw);
        await DeployProceduresAsync(raw!);
        try
        {
            var sourceMapDir = Path.Combine(Path.GetTempPath(), "tsqldbg-sourcemap-live");
            if (Directory.Exists(sourceMapDir))
            {
                Directory.Delete(sourceMapDir, recursive: true);
            }

            Directory.CreateDirectory(sourceMapDir);
            var innerFilePath = Path.Combine(sourceMapDir, "m7_sourcemap_inner.sql");
            await File.WriteAllTextAsync(innerFilePath, InnerProcDefinition);

            var tracePath = Path.Combine(Path.GetTempPath(), "m7-sourcemap-match.jsonl");
            if (File.Exists(tracePath))
            {
                File.Delete(tracePath);
            }

            var targetsFile = Path.Combine(AppContext.BaseDirectory, "targets.json");

            await using var dap = DapStdioHarness.Launch(tracePath);
            await dap.SendRequestAsync("initialize", new JsonObject { ["clientID"] = "m7-sourcemap-live-test", ["adapterID"] = "tsqldbg" });
            await dap.SendRequestAsync("launch", new JsonObject
            {
                ["server"] = csb.DataSource,
                ["database"] = csb.InitialCatalog,
                ["mode"] = "procedure",
                ["procedure"] = "dbo.m7_sourcemap_outer",
                ["args"] = new JsonObject { ["@x"] = "5" },
                ["targetsFile"] = targetsFile,
                ["stopOnEntry"] = true,
                ["sourceMap"] = new JsonArray(Path.Combine(sourceMapDir, "*.sql")),
            });
            await dap.WaitForEventAsync("initialized");
            await dap.SendRequestAsync("configurationDone", new JsonObject());
            await dap.WaitForEventAsync("stopped", e => e["body"]!["reason"]!.GetValue<string>() == "entry");

            // Step into the callee -- its first blueprint fetch resolves the
            // sourceMap match (FetchModuleBlueprintAsync -> ResolveSourceMapMatch).
            await dap.SendRequestAsync("stepIn", new JsonObject { ["threadId"] = 1 });
            await dap.WaitForEventAsync("stopped", e => e["body"]!["reason"]!.GetValue<string>() == "step");

            var framesAfterStepIn = await dap.SendRequestAsync("stackTrace", new JsonObject { ["threadId"] = 1 });
            Assert.Equal(2, framesAfterStepIn["body"]!["totalFrames"]!.GetValue<int>());
            var topSourceAfterStepIn = framesAfterStepIn["body"]!["stackFrames"]!.AsArray()[0]!["source"];
            Assert.Equal(innerFilePath, topSourceAfterStepIn!["path"]!.GetValue<string>());

            // Now the real file is a resolved module -- setBreakpoints against it
            // must bind (not fall to the pending-by-file bucket).
            var setBp = await dap.SendRequestAsync("setBreakpoints", new JsonObject
            {
                ["source"] = new JsonObject { ["path"] = innerFilePath },
                ["breakpoints"] = new JsonArray(new JsonObject { ["line"] = 4 }),
            });
            var bp = setBp["body"]!["breakpoints"]!.AsArray()[0]!;
            Assert.True(bp["verified"]!.GetValue<bool>(), bp["message"]?.GetValue<string>());
            Assert.Equal(4, bp["line"]!.GetValue<int>());

            await dap.SendRequestAsync("continue", new JsonObject { ["threadId"] = 1 });
            var stop = await dap.WaitForEventAsync("stopped", e => e["body"]!["reason"]!.GetValue<string>() == "breakpoint");
            Assert.Equal(1, stop["body"]!["threadId"]!.GetValue<int>());

            var framesAtBreakpoint = await dap.SendRequestAsync("stackTrace", new JsonObject { ["threadId"] = 1 });
            Assert.Equal(2, framesAtBreakpoint["body"]!["totalFrames"]!.GetValue<int>());
            var topFrame = framesAtBreakpoint["body"]!["stackFrames"]!.AsArray()[0]!;
            Assert.Equal(4, topFrame["line"]!.GetValue<int>());
            Assert.Equal(innerFilePath, topFrame["source"]!["path"]!.GetValue<string>());

            await dap.DisposeAsync();

            var traceLines = await File.ReadAllLinesAsync(tracePath);
            Assert.Contains(traceLines, l => l.Contains("\"sourcemap.match\""));
        }
        finally
        {
            await DropProceduresAsync(raw!);
        }
    }

    [SkippableFact]
    public async Task OneByteOffSourceMapFile_NeverMatches_FallsBackToTheVirtualDocument()
    {
        var raw = Environment.GetEnvironmentVariable(ConnEnvVar);
        Skip.If(string.IsNullOrWhiteSpace(raw), $"{ConnEnvVar} is not set; skipping live probe (never fake a pass — CLAUDE.md).");

        var csb = new SqlConnectionStringBuilder(raw);
        await DeployProceduresAsync(raw!);
        try
        {
            var sourceMapDir = Path.Combine(Path.GetTempPath(), "tsqldbg-sourcemap-live-mismatch");
            if (Directory.Exists(sourceMapDir))
            {
                Directory.Delete(sourceMapDir, recursive: true);
            }

            Directory.CreateDirectory(sourceMapDir);
            // One character off (@y -> @z on the DECLARE line only) -- everything
            // else byte-identical to the real server definition.
            var almostRight = InnerProcDefinition.Replace("DECLARE @y int;", "DECLARE @z int;");
            await File.WriteAllTextAsync(Path.Combine(sourceMapDir, "m7_sourcemap_inner.sql"), almostRight);

            var tracePath = Path.Combine(Path.GetTempPath(), "m7-sourcemap-mismatch.jsonl");
            if (File.Exists(tracePath))
            {
                File.Delete(tracePath);
            }

            var targetsFile = Path.Combine(AppContext.BaseDirectory, "targets.json");

            await using var dap = DapStdioHarness.Launch(tracePath);
            await dap.SendRequestAsync("initialize", new JsonObject { ["clientID"] = "m7-sourcemap-mismatch-live-test", ["adapterID"] = "tsqldbg" });
            await dap.SendRequestAsync("launch", new JsonObject
            {
                ["server"] = csb.DataSource,
                ["database"] = csb.InitialCatalog,
                ["mode"] = "procedure",
                ["procedure"] = "dbo.m7_sourcemap_outer",
                ["args"] = new JsonObject { ["@x"] = "5" },
                ["targetsFile"] = targetsFile,
                ["stopOnEntry"] = true,
                ["sourceMap"] = new JsonArray(Path.Combine(sourceMapDir, "*.sql")),
            });
            await dap.WaitForEventAsync("initialized");
            await dap.SendRequestAsync("configurationDone", new JsonObject());
            await dap.WaitForEventAsync("stopped", e => e["body"]!["reason"]!.GetValue<string>() == "entry");

            await dap.SendRequestAsync("stepIn", new JsonObject { ["threadId"] = 1 });
            await dap.WaitForEventAsync("stopped", e => e["body"]!["reason"]!.GetValue<string>() == "step");

            var frames = await dap.SendRequestAsync("stackTrace", new JsonObject { ["threadId"] = 1 });
            var topSource = frames["body"]!["stackFrames"]!.AsArray()[0]!["source"];

            // No match: falls back to the read-only tsqldbg: virtual document, same
            // as if no sourceMap had ever been configured.
            Assert.StartsWith("tsqldbg:", topSource!["path"]!.GetValue<string>());

            await dap.DisposeAsync();

            var traceLines = await File.ReadAllLinesAsync(tracePath);
            Assert.DoesNotContain(traceLines, l => l.Contains("\"sourcemap.match\""));
        }
        finally
        {
            await DropProceduresAsync(raw!);
        }
    }

    private static async Task DeployProceduresAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        await using (var drop = connection.CreateCommand())
        {
            drop.CommandText = """
                IF OBJECT_ID('dbo.m7_sourcemap_outer') IS NOT NULL DROP PROCEDURE dbo.m7_sourcemap_outer;
                IF OBJECT_ID('dbo.m7_sourcemap_inner') IS NOT NULL DROP PROCEDURE dbo.m7_sourcemap_inner;
                """;
            await drop.ExecuteNonQueryAsync();
        }

        await using (var createInner = connection.CreateCommand())
        {
            createInner.CommandText = InnerProcDefinition;
            await createInner.ExecuteNonQueryAsync();
        }

        await using var createOuter = connection.CreateCommand();
        createOuter.CommandText = OuterProcDefinition;
        await createOuter.ExecuteNonQueryAsync();
    }

    private static async Task DropProceduresAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            IF OBJECT_ID('dbo.m7_sourcemap_outer') IS NOT NULL DROP PROCEDURE dbo.m7_sourcemap_outer;
            IF OBJECT_ID('dbo.m7_sourcemap_inner') IS NOT NULL DROP PROCEDURE dbo.m7_sourcemap_inner;
            """;
        await command.ExecuteNonQueryAsync();
    }
}
