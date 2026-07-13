using Microsoft.Data.SqlClient;
using TsqlDbg.Core.Sessions;
using TsqlDbg.Core.Targets;
using TsqlDbg.Core.Tracing;
using Xunit;

namespace TsqlDbg.Integration;

// DESIGN §20.4 corpus fixture p11_dynamic_sql_blackbox + M7 D3 §3.1 (docs/archive/reviews/
// m7-hardening-design-notes-fable.md): C10 (dynamic SQL is an atomic black box). See
// the fixture's own header comment for the four black-box shapes it exercises
// (plain EXEC(@sql) DML, sp_executesql typed OUTPUT param, child-#temp lifetime,
// scope-isolated dynamic identity insert vs. the caller's own SCOPE_IDENTITY()).
// step-into REFUSES all dynamic SQL (C10), so all three passes are trajectory-
// identical to pass 1; boost refuses every EXEC member (§14/A21) -- NO exemptions
// anywhere in this fixture.
public sealed class P11DynamicSqlBlackboxFidelityTests
{
    private const string ConnEnvVar = "TSQLDBG_TEST_CONN";

    private sealed record BlackboxResult(int? Out, int? TmpGone, int? Si);

    [SkippableFact]
    public async Task DebuggerRun_MatchesNativeRun_ForDynamicSqlBlackbox()
    {
        var rawConnectionString = Environment.GetEnvironmentVariable(ConnEnvVar);
        Skip.If(
            string.IsNullOrWhiteSpace(rawConnectionString),
            $"{ConnEnvVar} is not set; skipping fidelity test (never fake a pass — CLAUDE.md).");

        var csb = new SqlConnectionStringBuilder(rawConnectionString);
        var server = csb.DataSource;
        var database = csb.InitialCatalog;

        await DeployFixtureAsync(rawConnectionString!);

        var native = await RunNativeAsync(rawConnectionString!);
        var debugged = await RunThroughDebuggerAsync(server, database, StepKind.Over);

        Assert.Equal(native, debugged);
        Assert.Equal(new BlackboxResult(11, 1, null), native);   // ground truth: SUM(5,6)=11; temp gone; caller si untouched
    }

    // Matrix cell (design note §6.1/§6.2, "p11" — no D2 dependency): step-into refuses
    // ALL dynamic SQL (C10) and falls back to step-over, so this pass is trajectory-
    // identical to pass 1 — asserted, not assumed.
    [SkippableFact]
    public async Task DebuggerRun_MatchesNativeRun_SteppedInto()
    {
        var rawConnectionString = Environment.GetEnvironmentVariable(ConnEnvVar);
        Skip.If(
            string.IsNullOrWhiteSpace(rawConnectionString),
            $"{ConnEnvVar} is not set; skipping fidelity test (never fake a pass — CLAUDE.md).");

        var csb = new SqlConnectionStringBuilder(rawConnectionString);
        var server = csb.DataSource;
        var database = csb.InitialCatalog;

        await DeployFixtureAsync(rawConnectionString!);

        var native = await RunNativeAsync(rawConnectionString!);
        var debugged = await RunThroughDebuggerAsync(server, database, StepKind.Into);

        Assert.Equal(native, debugged);
    }

    // M6 item 6 (B9) / M7 §6.2: fidelity pass 3 — continue with boost on. Assertions
    // identical to the pass-1 method above. Non-hollow boost declaration (§6.2,
    // MANDATORY): the fixture's ONLY IF node is boost-ELIGIBLE by node kind, so a
    // recording trace must show it was genuinely attempted and refused — the
    // single-line `IF ... SET ...;` shares its predicate's line with its own THEN
    // statement, so BoostPlanner refuses it with "line-ambiguity" (every OTHER member
    // in this fixture is an EXEC, refused outright by the §14/A21 whitelist, but the
    // interpreter never even reaches a plan attempt for those — only If/While nodes
    // trigger TryStepBoostedAsync's planner call at all).
    private sealed class RecordingSink : ITraceSink
    {
        public List<(string Category, string Message)> Events { get; } = new();
        public void Event(string category, string message) => Events.Add((category, message));
    }

    [SkippableFact]
    public async Task DebuggerRun_MatchesNativeRun_ContinueBoost()
    {
        var rawConnectionString = Environment.GetEnvironmentVariable(ConnEnvVar);
        Skip.If(
            string.IsNullOrWhiteSpace(rawConnectionString),
            $"{ConnEnvVar} is not set; skipping fidelity test (never fake a pass — CLAUDE.md).");

        var csb = new SqlConnectionStringBuilder(rawConnectionString);
        var server = csb.DataSource;
        var database = csb.InitialCatalog;

        await DeployFixtureAsync(rawConnectionString!);

        var native = await RunNativeAsync(rawConnectionString!);
        var trace = new RecordingSink();
        var debugged = await RunThroughDebuggerAsync(server, database, StepKind.Over, boost: true, trace: trace);

        Assert.Equal(native, debugged);
        Assert.Contains(trace.Events, e => e.Category == "boost.refuse" && e.Message.Contains("line-ambiguity"));
        Assert.DoesNotContain(trace.Events, e => e.Category == "boost.fire");
    }

    private static async Task DeployFixtureAsync(string connectionString)
    {
        var sqlPath = Path.Combine(AppContext.BaseDirectory, "corpus", "p11_dynamic_sql_blackbox.sql");
        var script = await File.ReadAllTextAsync(sqlPath);
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

    // @Out is a mandatory (no-default) OUTPUT parameter of the proc itself; a plain
    // ad-hoc batch supplies a throwaway local to bind it (native T-SQL requires SOME
    // variable for an OUTPUT arg even though the callee fully overwrites it before the
    // fixture's own SELECT reads it back) — the actual comparison reads the proc's own
    // result set, exactly like every other corpus fixture.
    private static async Task<BlackboxResult> RunNativeAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var tran = (SqlTransaction)await connection.BeginTransactionAsync();

        await using var command = connection.CreateCommand();
        command.Transaction = tran;
        command.CommandText =
            "DECLARE @OutArg int = 0; EXEC dbo.p11_dynamic_sql_blackbox @Seed = 5, @Out = @OutArg OUTPUT;";
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        var result = ReadRow(reader);
        await reader.DisposeAsync();

        await tran.RollbackAsync();
        return result;
    }

    private static async Task<BlackboxResult> RunThroughDebuggerAsync(
        string server, string database, StepKind stepKind, bool boost = false, ITraceSink? trace = null)
    {
        var target = new TargetEntry(server, "test", AllowWrites: false, Options: null);
        var options = new SessionOptions(
            server,
            database,
            LaunchMode.Procedure,
            "dbo.p11_dynamic_sql_blackbox",
            new Dictionary<string, string> { ["@Seed"] = "5", ["@Out"] = "0" },
            ScriptText: null,
            Boost: boost);

        var result = await SessionHost.RunAsync(options, target, trace, stepKind: stepKind);

        var resultSet = Assert.Single(result.Execution.ResultSets);
        var row = Assert.Single(resultSet.Rows);
        return new BlackboxResult((int?)row[0], (int?)row[1], (int?)row[2]);
    }

    private static BlackboxResult ReadRow(SqlDataReader reader) => new(
        reader.IsDBNull(0) ? null : reader.GetInt32(0),
        reader.IsDBNull(1) ? null : reader.GetInt32(1),
        reader.IsDBNull(2) ? null : reader.GetInt32(2));
}
