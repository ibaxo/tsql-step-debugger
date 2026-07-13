using Microsoft.Data.SqlClient;
using TsqlDbg.Core.Sessions;
using TsqlDbg.Core.Targets;
using Xunit;

namespace TsqlDbg.Integration;

// DESIGN §20.4 corpus fixture p02_if_else_branches + §22 M2 accept criterion
// (Accept: p02-p03, p14, p17, p18). See the fixture's own header comment for the
// fact-12/D3 rowcount pattern and fact 14 B hoisting behavior it exercises.
public sealed class P02IfElseBranchesFidelityTests
{
    private const string ConnEnvVar = "TSQLDBG_TEST_CONN";

    private sealed record BranchResult(
        int Mode, int? RowcountInBranch, int? ThenOnly, int? ElseOnly, int? ThenDeclared, int? ElseDeclared);

    [SkippableTheory]
    [InlineData(5)]
    [InlineData(-5)]
    public async Task DebuggerRun_MatchesNativeRun_ForIfElseBranches(int mode)
    {
        var rawConnectionString = Environment.GetEnvironmentVariable(ConnEnvVar);
        Skip.If(
            string.IsNullOrWhiteSpace(rawConnectionString),
            $"{ConnEnvVar} is not set; skipping fidelity test (never fake a pass — CLAUDE.md).");

        var csb = new SqlConnectionStringBuilder(rawConnectionString);
        var server = csb.DataSource;
        var database = csb.InitialCatalog;

        await DeployFixtureAsync(rawConnectionString!);

        var native = await RunNativeAsync(rawConnectionString!, mode);
        var debugged = await RunThroughDebuggerAsync(server, database, mode);

        Assert.Equal(native, debugged);
    }

    // M7 matrix cell (design note §6.1/§6.2): mechanical pass-2 addition -- this
    // fixture is EXEC-free, so Into is trajectory-identical to Over; asserted (runs
    // the full pipeline and can genuinely fail), not assumed.
    [SkippableTheory]
    [InlineData(5)]
    [InlineData(-5)]
    public async Task DebuggerRun_MatchesNativeRun_SteppedInto(int mode)
    {
        var rawConnectionString = Environment.GetEnvironmentVariable(ConnEnvVar);
        Skip.If(
            string.IsNullOrWhiteSpace(rawConnectionString),
            $"{ConnEnvVar} is not set; skipping fidelity test (never fake a pass — CLAUDE.md).");

        var csb = new SqlConnectionStringBuilder(rawConnectionString);
        var server = csb.DataSource;
        var database = csb.InitialCatalog;

        await DeployFixtureAsync(rawConnectionString!);

        var native = await RunNativeAsync(rawConnectionString!, mode);
        var debugged = await RunThroughDebuggerAsync(server, database, mode, StepKind.Into);

        Assert.Equal(native, debugged);
    }

    // M6 item 6 (B9): fidelity pass 3 — continue with boost on. Assertions identical
    // to the pass-1 method above. Both IFs refuse boost per §14/A21 (the first's
    // predicate references @@ROWCOUNT; the second's branches each DECLARE a scalar) —
    // this fixture is a refusal-equivalence baseline, not a boost.fire exercise.
    [SkippableTheory]
    [InlineData(5)]
    [InlineData(-5)]
    public async Task DebuggerRun_MatchesNativeRun_ContinueBoost(int mode)
    {
        var rawConnectionString = Environment.GetEnvironmentVariable(ConnEnvVar);
        Skip.If(
            string.IsNullOrWhiteSpace(rawConnectionString),
            $"{ConnEnvVar} is not set; skipping fidelity test (never fake a pass — CLAUDE.md).");

        var csb = new SqlConnectionStringBuilder(rawConnectionString);
        var server = csb.DataSource;
        var database = csb.InitialCatalog;

        await DeployFixtureAsync(rawConnectionString!);

        var native = await RunNativeAsync(rawConnectionString!, mode);
        var debugged = await RunThroughDebuggerAsync(server, database, mode, boost: true);

        Assert.Equal(native, debugged);
    }

    private static async Task DeployFixtureAsync(string connectionString)
    {
        var sqlPath = Path.Combine(AppContext.BaseDirectory, "corpus", "p02_if_else_branches.sql");
        var script = await File.ReadAllTextAsync(sqlPath);

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = script;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<BranchResult> RunNativeAsync(string connectionString, int mode)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var tran = (SqlTransaction)await connection.BeginTransactionAsync();

        await using var command = connection.CreateCommand();
        command.Transaction = tran;
        command.CommandText = "EXEC dbo.p02_if_else_branches @Mode = @Mode;";
        command.Parameters.AddWithValue("@Mode", mode);
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        var result = ReadRow(reader);
        await reader.DisposeAsync();

        await tran.RollbackAsync();
        return result;
    }

    private static async Task<BranchResult> RunThroughDebuggerAsync(
        string server, string database, int mode, StepKind stepKind = StepKind.Over, bool boost = false)
    {
        var target = new TargetEntry(server, "test", AllowWrites: false, Options: null);
        var options = new SessionOptions(
            server,
            database,
            LaunchMode.Procedure,
            "dbo.p02_if_else_branches",
            new Dictionary<string, string> { ["@Mode"] = mode.ToString() },
            ScriptText: null,
            Boost: boost);

        var result = await SessionHost.RunAsync(options, target, stepKind: stepKind);

        var resultSet = Assert.Single(result.Execution.ResultSets);
        var row = Assert.Single(resultSet.Rows);
        return new BranchResult(
            (int)row[0]!,
            (int?)row[1],
            (int?)row[2],
            (int?)row[3],
            (int?)row[4],
            (int?)row[5]);
    }

    private static BranchResult ReadRow(SqlDataReader reader) => new(
        reader.GetInt32(0),
        reader.IsDBNull(1) ? null : reader.GetInt32(1),
        reader.IsDBNull(2) ? null : reader.GetInt32(2),
        reader.IsDBNull(3) ? null : reader.GetInt32(3),
        reader.IsDBNull(4) ? null : reader.GetInt32(4),
        reader.IsDBNull(5) ? null : reader.GetInt32(5));
}
