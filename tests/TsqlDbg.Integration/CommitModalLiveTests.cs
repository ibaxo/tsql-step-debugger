using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;

namespace TsqlDbg.Integration;

// M7 hardening + M9 trust-model re-spec (docs/archive/reviews/connection-trust-model-opus.md,
// A38-A40): a live DapStdioHarness smoke for the commit path over the REAL DAP wire,
// playing the "extension" role for the tsqldbg_commitConfirm/tsqldbg_commitDecision
// round trip. Exercises the ONE ratified path CLAUDE.md safety rule 7 carves out of the
// otherwise-unconditional rollback teardown: an EXPLICIT `terminate` (never a natural
// completion, never `disconnect`) with commitMode="commit". On the interactive surface
// the terminate modal is the SOLE authorization (A39) — the third probe proves commit
// succeeds even against an allowWrites:false target, which the pre-A39 adapter refused.
public sealed class CommitModalLiveTests
{
    private const string ConnEnvVar = "TSQLDBG_TEST_CONN";

    // Two statements deliberately: `next` past the INSERT lands on the SELECT with
    // the session very much still alive (not naturally completed) — `terminate` is
    // then a genuine ABORT of an in-progress session, not the natural-completion
    // path (which always rolls back regardless of commitMode; EndSessionLockedAsync
    // supplies no decision callback).
    private const string Script = """
        INSERT dbo.m7_commit_probe (v) VALUES (42);
        SELECT 1;
        """;

    [SkippableFact]
    public async Task ExplicitTerminate_CommitModeArmed_ConfirmedYes_CommitsTheRow()
    {
        var raw = Environment.GetEnvironmentVariable(ConnEnvVar);
        Skip.If(string.IsNullOrWhiteSpace(raw), $"{ConnEnvVar} is not set; skipping live probe (never fake a pass — CLAUDE.md).");

        var csb = new SqlConnectionStringBuilder(raw);
        var server = csb.DataSource;
        var database = csb.InitialCatalog;

        await DeployFixtureAsync(raw!);
        try
        {
            var tracePath = Path.Combine(Path.GetTempPath(), "m7-commit-modal.jsonl");
            if (File.Exists(tracePath))
            {
                File.Delete(tracePath);
            }

            var scriptPath = Path.Combine(Path.GetTempPath(), "m7-commit-modal.sql");
            await File.WriteAllTextAsync(scriptPath, Script);

            var targetsFile = Path.Combine(AppContext.BaseDirectory, "targets.json");

            await using var dap = DapStdioHarness.Launch(tracePath);
            await dap.SendRequestAsync("initialize", new JsonObject { ["clientID"] = "m7-commit-modal-live-test", ["adapterID"] = "tsqldbg" });
            await dap.SendRequestAsync("launch", new JsonObject
            {
                ["server"] = server,
                ["database"] = database,
                ["mode"] = "script",
                ["script"] = scriptPath,
                ["targetsFile"] = targetsFile,
                ["stopOnEntry"] = true,
                ["commitMode"] = "commit",
            });
            await dap.WaitForEventAsync("initialized");
            await dap.SendRequestAsync("configurationDone", new JsonObject());
            await dap.WaitForEventAsync("stopped", e => e["body"]!["reason"]!.GetValue<string>() == "entry");

            await dap.SendRequestAsync("next", new JsonObject { ["threadId"] = 1 });   // runs the INSERT
            await dap.WaitForEventAsync("stopped", e => e["body"]!["reason"]!.GetValue<string>() == "step");

            // Play the extension's role: `terminate` is fire-and-forget from the
            // client's point of view, so send it and race the confirm event.
            var terminateTask = dap.SendRequestAsync("terminate", new JsonObject());
            var confirm = await dap.WaitForEventAsync("tsqldbg_commitConfirm", timeout: TimeSpan.FromSeconds(15));
            Assert.Equal(server, confirm["body"]!["server"]!.GetValue<string>());
            Assert.Equal(database, confirm["body"]!["database"]!.GetValue<string>());
            Assert.Equal("dev", confirm["body"]!["env"]!.GetValue<string>());

            await dap.SendRequestAsync("tsqldbg_commitDecision", new JsonObject { ["commit"] = true });
            await terminateTask;
            await dap.WaitForEventAsync("terminated", timeout: TimeSpan.FromSeconds(15));
            await dap.DisposeAsync();

            var committed = await ReadRowsAsync(raw!);
            Assert.Equal(new[] { 42 }, committed);

            var traceLines = await File.ReadAllLinesAsync(tracePath);
            Assert.Contains(traceLines, l => l.Contains("\"session.commit\""));
            Assert.DoesNotContain(traceLines, l => l.Contains("\"session.rollback\""));
        }
        finally
        {
            await DropFixtureAsync(raw!);
        }
    }

    [SkippableFact]
    public async Task ExplicitTerminate_CommitModeArmed_Declined_RollsBackInstead()
    {
        var raw = Environment.GetEnvironmentVariable(ConnEnvVar);
        Skip.If(string.IsNullOrWhiteSpace(raw), $"{ConnEnvVar} is not set; skipping live probe (never fake a pass — CLAUDE.md).");

        var csb = new SqlConnectionStringBuilder(raw);
        var server = csb.DataSource;
        var database = csb.InitialCatalog;

        await DeployFixtureAsync(raw!);
        try
        {
            var tracePath = Path.Combine(Path.GetTempPath(), "m7-commit-modal-declined.jsonl");
            if (File.Exists(tracePath))
            {
                File.Delete(tracePath);
            }

            var scriptPath = Path.Combine(Path.GetTempPath(), "m7-commit-modal-declined.sql");
            await File.WriteAllTextAsync(scriptPath, Script);

            var targetsFile = Path.Combine(AppContext.BaseDirectory, "targets.json");

            await using var dap = DapStdioHarness.Launch(tracePath);
            await dap.SendRequestAsync("initialize", new JsonObject { ["clientID"] = "m7-commit-modal-live-test", ["adapterID"] = "tsqldbg" });
            await dap.SendRequestAsync("launch", new JsonObject
            {
                ["server"] = server,
                ["database"] = database,
                ["mode"] = "script",
                ["script"] = scriptPath,
                ["targetsFile"] = targetsFile,
                ["stopOnEntry"] = true,
                ["commitMode"] = "commit",
            });
            await dap.WaitForEventAsync("initialized");
            await dap.SendRequestAsync("configurationDone", new JsonObject());
            await dap.WaitForEventAsync("stopped", e => e["body"]!["reason"]!.GetValue<string>() == "entry");

            await dap.SendRequestAsync("next", new JsonObject { ["threadId"] = 1 });
            await dap.WaitForEventAsync("stopped", e => e["body"]!["reason"]!.GetValue<string>() == "step");

            var terminateTask = dap.SendRequestAsync("terminate", new JsonObject());
            await dap.WaitForEventAsync("tsqldbg_commitConfirm", timeout: TimeSpan.FromSeconds(15));
            await dap.SendRequestAsync("tsqldbg_commitDecision", new JsonObject { ["commit"] = false });
            await terminateTask;
            await dap.WaitForEventAsync("terminated", timeout: TimeSpan.FromSeconds(15));
            await dap.DisposeAsync();

            var rows = await ReadRowsAsync(raw!);
            Assert.Empty(rows);

            var traceLines = await File.ReadAllLinesAsync(tracePath);
            Assert.Contains(traceLines, l => l.Contains("\"session.commit.declined\""));
            Assert.Contains(traceLines, l => l.Contains("\"session.rollback\""));
        }
        finally
        {
            await DropFixtureAsync(raw!);
        }
    }

    // DESIGN §16 (A39, option A / A38): the interactive surface authorizes commit by the
    // terminate modal ALONE — no targets.json allowWrites:true pre-gate. This target is
    // allowWrites:FALSE (under the pre-A39 adapter this launch refused outright with
    // "requires ... allowWrites:true"), yet a confirmed modal still commits. `options` is
    // retained so TLS behaves like the other probes; only allowWrites is under test.
    [SkippableFact]
    public async Task ExplicitTerminate_CommitModeArmed_AllowWritesFalseTarget_StillCommits_ViaModal()
    {
        var raw = Environment.GetEnvironmentVariable(ConnEnvVar);
        Skip.If(string.IsNullOrWhiteSpace(raw), $"{ConnEnvVar} is not set; skipping live probe (never fake a pass — CLAUDE.md).");

        var csb = new SqlConnectionStringBuilder(raw);
        var server = csb.DataSource;
        var database = csb.InitialCatalog;

        await DeployFixtureAsync(raw!);
        try
        {
            var tracePath = Path.Combine(Path.GetTempPath(), "a39-commit-no-allowwrites.jsonl");
            if (File.Exists(tracePath))
            {
                File.Delete(tracePath);
            }

            var scriptPath = Path.Combine(Path.GetTempPath(), "a39-commit-no-allowwrites.sql");
            await File.WriteAllTextAsync(scriptPath, Script);

            // A targets.json whose entry is allowWrites:FALSE — the pre-A39 gate refused
            // commit against this. Backslash in the server (named instance) escaped so the
            // file stays valid JSON and the key matches the launch server exactly.
            var serverJson = server.Replace("\\", "\\\\");
            var targetsPath = Path.Combine(Path.GetTempPath(), "a39-allowwrites-false-targets.json");
            await File.WriteAllTextAsync(targetsPath, $$"""
                {
                  "targets": {
                    "{{serverJson}}": { "env": "dev", "allowWrites": false, "options": "TrustServerCertificate=True" }
                  }
                }
                """);

            await using var dap = DapStdioHarness.Launch(tracePath);
            await dap.SendRequestAsync("initialize", new JsonObject { ["clientID"] = "a39-commit-live-test", ["adapterID"] = "tsqldbg" });
            await dap.SendRequestAsync("launch", new JsonObject
            {
                ["server"] = server,
                ["database"] = database,
                ["mode"] = "script",
                ["script"] = scriptPath,
                ["targetsFile"] = targetsPath,
                ["stopOnEntry"] = true,
                ["commitMode"] = "commit",
            });
            await dap.WaitForEventAsync("initialized");
            await dap.SendRequestAsync("configurationDone", new JsonObject());
            await dap.WaitForEventAsync("stopped", e => e["body"]!["reason"]!.GetValue<string>() == "entry");

            await dap.SendRequestAsync("next", new JsonObject { ["threadId"] = 1 });
            await dap.WaitForEventAsync("stopped", e => e["body"]!["reason"]!.GetValue<string>() == "step");

            var terminateTask = dap.SendRequestAsync("terminate", new JsonObject());
            await dap.WaitForEventAsync("tsqldbg_commitConfirm", timeout: TimeSpan.FromSeconds(15));
            await dap.SendRequestAsync("tsqldbg_commitDecision", new JsonObject { ["commit"] = true });
            await terminateTask;
            await dap.WaitForEventAsync("terminated", timeout: TimeSpan.FromSeconds(15));
            await dap.DisposeAsync();

            var committed = await ReadRowsAsync(raw!);
            Assert.Equal(new[] { 42 }, committed);

            var traceLines = await File.ReadAllLinesAsync(tracePath);
            Assert.Contains(traceLines, l => l.Contains("\"session.commit\""));
            Assert.DoesNotContain(traceLines, l => l.Contains("\"session.rollback\""));
        }
        finally
        {
            await DropFixtureAsync(raw!);
        }
    }

    private static async Task DeployFixtureAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            IF OBJECT_ID('dbo.m7_commit_probe') IS NOT NULL DROP TABLE dbo.m7_commit_probe;
            CREATE TABLE dbo.m7_commit_probe (id int IDENTITY PRIMARY KEY, v int NOT NULL);
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task DropFixtureAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "IF OBJECT_ID('dbo.m7_commit_probe') IS NOT NULL DROP TABLE dbo.m7_commit_probe;";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int[]> ReadRowsAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT v FROM dbo.m7_commit_probe ORDER BY id;";
        await using var reader = await command.ExecuteReaderAsync();
        var values = new List<int>();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetInt32(0));
        }

        return values.ToArray();
    }
}
