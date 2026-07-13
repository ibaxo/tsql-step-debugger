using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;

namespace TsqlDbg.Integration;

// M7 packaging (design note §7/D7 items 2-3; DESIGN.md §7, §17, §19). The work
// order's own REALITY CHECK: an interactive `code --profile m7-smoke` F5 session on a
// clean VS Code profile needs an actual VS Code UI to drive and is not achievable from
// a headless subagent. This test is the sanctioned headless substitute: a
// DapStdioHarness smoke driving the PACKAGED, self-contained win-x64 adapter
// executable DIRECTLY -- not `dotnet <dll>`, the dev-path every other Integration test
// in this project uses -- with the .NET SDK/runtime masked from PATH and DOTNET_ROOT
// unset for the CHILD PROCESS ONLY, proving the self-contained publish genuinely needs
// no system-wide .NET install, end to end, over the real DAP wire:
//
//   launch -> entry stop -> a breakpoint set in a stepped-into callee's virtual
//   document (tsqldbg:) and hit on that callee's SECOND call via `continue` ->
//   Locals + Temp Tables expand -> one REPL SELECT -> `continue` into a 5s WAITFOR ->
//   `pause` mid-statement -> `terminate` -> a FRESH connection verifying server-side
//   that the whole session's durable writes rolled back.
//
// The trace is committed as docs/archive/reviews/m7-vsix-smoketest-trace.jsonl (must parse
// 0-unparseable, DESIGN.md §19 -- asserted below, not just eyeballed).
//
// Skips cleanly (never fakes a pass -- CLAUDE.md) when the packaged exe isn't present:
// it is scripts/package-vsix.ps1's build artifact, not `dotnet build`/`dotnet test`'s
// -- exactly the same discipline as the TSQLDBG_TEST_CONN skip used everywhere else.
public sealed class PackagedSelfContainedSmokeTests
{
    private const string ConnEnvVar = "TSQLDBG_TEST_CONN";

    [SkippableFact]
    public async Task PackagedAdapter_DotNetMaskedFromPathAndDotnetRoot_RunsAFullSessionOverRealDap_AndRollsBack()
    {
        var raw = Environment.GetEnvironmentVariable(ConnEnvVar);
        Skip.If(string.IsNullOrWhiteSpace(raw),
            $"{ConnEnvVar} is not set; skipping live probe (never fake a pass -- CLAUDE.md).");

        var repoRoot = FindRepoRoot();
        var packagedExe = Path.Combine(repoRoot, "extension", "bin", "win-x64", "TsqlDbg.Adapter.exe");
        Skip.If(!File.Exists(packagedExe),
            $"Packaged self-contained adapter not found at {packagedExe}. Run " +
            "`pwsh -File scripts/package-vsix.ps1` first -- this smoke drives the PACKAGED " +
            "artifact, not the dev dotnet-run path every other Integration test uses.");

        var csb = new SqlConnectionStringBuilder(raw);
        var server = csb.DataSource;
        var database = csb.InitialCatalog;

        await DeployFixtureAsync(raw!);
        try
        {
            // M8 hygiene fix (docs/archive/reviews/m8-multibatch-adapter-sonnet.md): this used to
            // write directly to the COMMITTED docs/archive/reviews/m7-vsix-smoketest-trace.jsonl,
            // so every live suite run re-dirtied the tree even though the test's own
            // assertions only need SOME trace file to read back and parse -- the committed
            // path is a static, one-time-captured artifact (the M1-M6 smoke-trace
            // precedent), not something a passing test run should keep rewriting. Point
            // this run's own trace at a scratch path instead; the committed file is left
            // untouched here.
            var tracePath = Path.Combine(Path.GetTempPath(), "m7-vsix-smoketest-trace.jsonl");
            if (File.Exists(tracePath))
            {
                File.Delete(tracePath);
            }

            var targetsFile = Path.Combine(repoRoot, "targets.json");

            var (maskedPath, strippedDirs) = StripDotnetFromPath();
            Assert.NotEmpty(strippedDirs); // non-hollow: this run's own PATH really did carry a dotnet
            var envOverrides = new Dictionary<string, string?>
            {
                ["PATH"] = maskedPath,
                ["DOTNET_ROOT"] = null,
                ["DOTNET_ROOT(x86)"] = null,
                ["DOTNET_MULTILEVEL_LOOKUP"] = "0",
            };

            await using var dap = DapStdioHarness.Launch(tracePath, packagedExe, envOverrides);

            await dap.SendRequestAsync("initialize", new JsonObject { ["clientID"] = "m7-vsix-smoke", ["adapterID"] = "tsqldbg" });
            await dap.SendRequestAsync("launch", new JsonObject
            {
                ["server"] = server,
                ["database"] = database,
                ["mode"] = "procedure",
                ["procedure"] = "dbo.m7_smoke_outer",
                ["args"] = new JsonObject { ["@Seed"] = "10" },
                ["targetsFile"] = targetsFile,
                ["stopOnEntry"] = true,
                ["waitfor"] = "honor",
                ["trace"] = true,
            });
            await dap.WaitForEventAsync("initialized");
            await dap.SendRequestAsync("configurationDone", new JsonObject());
            await dap.WaitForEventAsync("stopped", e => e["body"]!["reason"]!.GetValue<string>() == "entry", TimeSpan.FromSeconds(60));

            // stepIn degrades to `next` on any non-EXEC line (§6), so looping it is
            // robust to exactly how many SUs precede the first EXEC.
            JsonNode frames = new JsonObject();
            var depth = 1;
            var guard = 0;
            do
            {
                await dap.SendRequestAsync("stepIn", new JsonObject { ["threadId"] = 1 });
                await dap.WaitForEventAsync("stopped", e => e["body"]!["reason"]!.GetValue<string>() == "step");
                frames = await dap.SendRequestAsync("stackTrace", new JsonObject { ["threadId"] = 1 });
                depth = frames["body"]!["totalFrames"]!.GetValue<int>();
                guard++;
            }
            while (depth < 2 && guard < 10);
            Assert.Equal(2, depth); // pushed into dbo.m7_smoke_callee's FIRST activation

            var calleeFrame = frames["body"]!["stackFrames"]!.AsArray()[0]!;
            var calleeSourcePath = calleeFrame["source"]!["path"]!.GetValue<string>();
            var calleeEntryLine = calleeFrame["line"]!.GetValue<int>();
            Assert.StartsWith("tsqldbg:", calleeSourcePath); // read-only virtual doc: no sourceMap configured for this smoke

            // Breakpoint on the callee's SECOND statement -- one line past the one
            // stepIn landed us on (`SET NOCOUNT ON;` then `SET @Result = @Seed * 2;`,
            // adjacent lines in the fixture below) -- deliberately NOT the line we're
            // currently standing on: stepOut is itself continue-like (§6: "run (as
            // continue) until current frame pops"), so it checks-before-executing the
            // very next SU, and a breakpoint on the CURRENT line would refire
            // immediately without ever really stepping (verified live while building
            // this test).
            var calleeBreakpointLine = calleeEntryLine + 1;
            var setBp = await dap.SendRequestAsync("setBreakpoints", new JsonObject
            {
                ["source"] = new JsonObject { ["path"] = calleeSourcePath },
                ["breakpoints"] = new JsonArray(new JsonObject { ["line"] = calleeBreakpointLine }),
            });
            Assert.True(setBp["body"]!["breakpoints"]!.AsArray()[0]!["verified"]!.GetValue<bool>());

            // stepOut runs forward (continue-like) from the entry line and must stop
            // AT the breakpointed second line, still inside the callee (frame depth 2)
            // -- a real breakpoint hit, not a `next`/`stepIn` single-step.
            await dap.SendRequestAsync("stepOut", new JsonObject { ["threadId"] = 1 });
            var bpStop = await dap.WaitForEventAsync("stopped", e => e["body"]!["reason"]!.GetValue<string>() == "breakpoint");
            Assert.Equal(1, bpStop["body"]!["threadId"]!.GetValue<int>());
            var atBreakpoint = await dap.SendRequestAsync("stackTrace", new JsonObject { ["threadId"] = 1 });
            Assert.Equal(2, atBreakpoint["body"]!["totalFrames"]!.GetValue<int>());
            var bpFrame = atBreakpoint["body"]!["stackFrames"]!.AsArray()[0]!;
            Assert.Equal(calleeSourcePath, bpFrame["source"]!["path"]!.GetValue<string>());
            Assert.Equal(calleeBreakpointLine, bpFrame["line"]!.GetValue<int>());

            // Clear it (whole-set replacement, §13) so the callee's SECOND activation
            // (the outer frame's next EXEC) doesn't stop again -- this smoke only needs
            // to prove the breakpoint fires once, cleanly.
            await dap.SendRequestAsync("setBreakpoints", new JsonObject
            {
                ["source"] = new JsonObject { ["path"] = calleeSourcePath },
                ["breakpoints"] = new JsonArray(),
            });

            // Finish this activation and pop back to the outer frame for
            // Locals + Temp Tables + REPL (frame 0 owns #m7_smoke directly -- no
            // cross-frame registry-visibility assumptions needed).
            await dap.SendRequestAsync("stepOut", new JsonObject { ["threadId"] = 1 });
            await dap.WaitForEventAsync("stopped", e => e["body"]!["reason"]!.GetValue<string>() == "step");

            // Step over the SECOND call to the callee entirely (already proved
            // step-into + breakpoint above; no need to re-enter it).
            await dap.SendRequestAsync("next", new JsonObject { ["threadId"] = 1 });
            await dap.WaitForEventAsync("stopped", e => e["body"]!["reason"]!.GetValue<string>() == "step");

            var outerFrames = await dap.SendRequestAsync("stackTrace", new JsonObject { ["threadId"] = 1 });
            var outerFrameId = outerFrames["body"]!["stackFrames"]!.AsArray()[0]!["id"]!.GetValue<int>();

            var scopesResp = await dap.SendRequestAsync("scopes", new JsonObject { ["frameId"] = outerFrameId });
            var scopesArray = scopesResp["body"]!["scopes"]!.AsArray();

            var localsRef = scopesArray.First(s => s!["name"]!.GetValue<string>() == "Locals")!["variablesReference"]!.GetValue<int>();
            var localsVars = await dap.SendRequestAsync("variables", new JsonObject { ["variablesReference"] = localsRef });
            var localsArray = localsVars["body"]!["variables"]!.AsArray();
            Assert.Contains(localsArray, v => v!["name"]!.GetValue<string>() == "@Result1");
            var result1 = localsArray.First(v => v!["name"]!.GetValue<string>() == "@Result1")!["value"]!.GetValue<string>();
            Assert.Equal("20", result1); // @Seed(10) * 2, copied back from the first callee activation (§11.5)

            var tempRef = scopesArray.First(s => s!["name"]!.GetValue<string>() == "Temp Tables")!["variablesReference"]!.GetValue<int>();
            var tempVars = await dap.SendRequestAsync("variables", new JsonObject { ["variablesReference"] = tempRef });
            var tempArray = tempVars["body"]!["variables"]!.AsArray();
            Assert.Contains(tempArray, v => v!["name"]!.GetValue<string>().Contains("m7_smoke", StringComparison.OrdinalIgnoreCase)
                && v["value"]!.GetValue<string>().Contains("rows"));

            var repl = await dap.SendRequestAsync("evaluate", new JsonObject
            {
                ["expression"] = "SELECT COUNT(*) AS Cnt FROM #m7_smoke",
                ["context"] = "repl",
                ["frameId"] = outerFrameId,
            });
            Assert.NotNull(repl["body"]!["result"]);

            // `next` runs the durable pre-WAITFOR INSERT, landing the cursor ON the
            // WAITFOR line itself.
            await dap.SendRequestAsync("next", new JsonObject { ["threadId"] = 1 });
            await dap.WaitForEventAsync("stopped", e => e["body"]!["reason"]!.GetValue<string>() == "step");

            // This `next` EXECUTES the WAITFOR (waitfor:"honor" -- a real 5s server
            // wait, §6/§14 D8); its own request/ack returns promptly (the same
            // request-ack-now/stop-later split BoostedAttentionPauseLiveTests already
            // relies on for `continue`) while the statement itself keeps running.
            await dap.SendRequestAsync("next", new JsonObject { ["threadId"] = 1 });
            await Task.Delay(TimeSpan.FromSeconds(1));
            await dap.SendRequestAsync("pause", new JsonObject { ["threadId"] = 1 }, TimeSpan.FromSeconds(10));
            var pauseStop = await dap.WaitForEventAsync("stopped", e => e["body"]!["reason"]!.GetValue<string>() == "pause", TimeSpan.FromSeconds(30));
            Assert.Equal(1, pauseStop["body"]!["threadId"]!.GetValue<int>());

            await dap.SendRequestAsync("terminate", new JsonObject());
            await dap.WaitForEventAsync("terminated", timeout: TimeSpan.FromSeconds(15));
            await dap.DisposeAsync();

            // Server-side, over a FRESH connection: the durable INSERT that ran BEFORE
            // the pause point (dbo.m7_smoke_writes) must be gone -- a real rollback
            // proof, not a vacuous one (that statement genuinely executed and
            // committed-within-the-open-transaction before terminate tore it down).
            var writeCount = await CountWritesAsync(raw!);
            Assert.Equal(0, writeCount);

            var traceLines = await File.ReadAllLinesAsync(tracePath);
            Assert.NotEmpty(traceLines);
            var unparseable = traceLines.Where(l => !TryParseJsonLine(l)).ToList();
            Assert.True(unparseable.Count == 0,
                $"{unparseable.Count} unparseable trace line(s) (DESIGN.md §19 requires 0): " +
                string.Join(" | ", unparseable.Take(3)));
        }
        finally
        {
            await DropFixtureAsync(raw!);
        }
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

    // Best-effort corporate-machine simulation (D7 item 3): strip every PATH entry
    // that itself carries a dotnet executable. Returns the masked PATH plus which
    // directories were removed, so the caller can assert this run's own PATH really
    // did carry one (a no-op mask would prove nothing).
    private static (string MaskedPath, IReadOnlyList<string> Removed) StripDotnetFromPath()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        var entries = path.Split(Path.PathSeparator);
        var removed = new List<string>();
        var kept = new List<string>();
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry))
            {
                continue;
            }

            var hasDotnet = File.Exists(Path.Combine(entry, "dotnet.exe")) || File.Exists(Path.Combine(entry, "dotnet"));
            if (hasDotnet)
            {
                removed.Add(entry);
            }
            else
            {
                kept.Add(entry);
            }
        }

        return (string.Join(Path.PathSeparator, kept), removed);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "TsqlDbg.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Could not locate the repo root (TsqlDbg.sln) walking up from " + AppContext.BaseDirectory);
    }

    // GO isn't a real batch separator for SqlCommand -- split it like every other
    // corpus/fixture loader in this project does. CREATE PROCEDURE must be the only
    // statement in its own batch.
    private static async Task DeployFixtureAsync(string connectionString)
    {
        const string script = """
            IF OBJECT_ID('dbo.m7_smoke_writes') IS NULL
                CREATE TABLE dbo.m7_smoke_writes (id int IDENTITY(1,1) PRIMARY KEY, val int NOT NULL);
            GO
            TRUNCATE TABLE dbo.m7_smoke_writes;
            GO
            CREATE OR ALTER PROCEDURE dbo.m7_smoke_callee
                @Seed int,
                @Result int OUTPUT
            AS
            BEGIN
                SET NOCOUNT ON;
                SET @Result = @Seed * 2;
            END
            GO
            CREATE OR ALTER PROCEDURE dbo.m7_smoke_outer
                @Seed int
            AS
            BEGIN
                SET NOCOUNT ON;

                CREATE TABLE #m7_smoke (id int IDENTITY(1,1) PRIMARY KEY, val int);
                INSERT INTO #m7_smoke (val) VALUES (@Seed);

                DECLARE @Result1 int, @Result2 int;
                DECLARE @SeedPlusOne int = @Seed + 1;   -- EXEC args must be literals/variables, not expressions
                EXEC dbo.m7_smoke_callee @Seed = @Seed, @Result = @Result1 OUTPUT;
                EXEC dbo.m7_smoke_callee @Seed = @SeedPlusOne, @Result = @Result2 OUTPUT;

                -- Durable write BEFORE the pause point: must NOT survive terminate's rollback.
                INSERT INTO dbo.m7_smoke_writes (val) VALUES (@Result1);

                WAITFOR DELAY '00:00:05';

                -- Never reached (terminate fires during the WAITFOR above).
                INSERT INTO dbo.m7_smoke_writes (val) VALUES (@Result2);

                SELECT @Result1 AS Result1, @Result2 AS Result2;
            END
            """;
        var batches = System.Text.RegularExpressions.Regex.Split(script, @"^\s*GO\s*$",
            System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        foreach (var batch in batches)
        {
            if (string.IsNullOrWhiteSpace(batch))
            {
                continue;
            }

            await using var command = connection.CreateCommand();
            command.CommandText = batch;
            await command.ExecuteNonQueryAsync();
        }
    }

    private static async Task DropFixtureAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            IF OBJECT_ID('dbo.m7_smoke_outer') IS NOT NULL DROP PROCEDURE dbo.m7_smoke_outer;
            IF OBJECT_ID('dbo.m7_smoke_callee') IS NOT NULL DROP PROCEDURE dbo.m7_smoke_callee;
            IF OBJECT_ID('dbo.m7_smoke_writes') IS NOT NULL DROP TABLE dbo.m7_smoke_writes;
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> CountWritesAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM dbo.m7_smoke_writes;";
        return (int)(await command.ExecuteScalarAsync())!;
    }
}
