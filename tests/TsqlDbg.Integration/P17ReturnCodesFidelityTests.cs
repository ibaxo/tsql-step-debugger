using System.Data;
using Microsoft.Data.SqlClient;
using TsqlDbg.Core.Sessions;
using TsqlDbg.Core.Targets;
using Xunit;

namespace TsqlDbg.Integration;

// DESIGN §20.4 corpus fixture p17_return_codes + §22 M2 accept criterion
// (Accept: p02-p03, p14, p17, p18). Exercises D9 (ReturnFromFrame / __ret scalar-eval
// / bare RETURN defaults to 0) across three paths: negative literal, bare RETURN, and
// a computed positive code alongside a SELECT that ran before it.
public sealed class P17ReturnCodesFidelityTests
{
    private const string ConnEnvVar = "TSQLDBG_TEST_CONN";

    private sealed record ReturnResult(int ReturnCode, int? Computed);

    [SkippableTheory]
    [InlineData(-5)]
    [InlineData(0)]
    [InlineData(3)]
    public async Task DebuggerRun_MatchesNativeRun_ForReturnCodes(int mode)
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
    [InlineData(-5)]
    [InlineData(0)]
    [InlineData(3)]
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
    // to the pass-1 method above.
    [SkippableTheory]
    [InlineData(-5)]
    [InlineData(0)]
    [InlineData(3)]
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
        var sqlPath = Path.Combine(AppContext.BaseDirectory, "corpus", "p17_return_codes.sql");
        var script = await File.ReadAllTextAsync(sqlPath);

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = script;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<ReturnResult> RunNativeAsync(string connectionString, int mode)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var tran = (SqlTransaction)await connection.BeginTransactionAsync();

        await using var command = connection.CreateCommand();
        command.Transaction = tran;
        command.CommandText = "dbo.p17_return_codes";
        command.CommandType = CommandType.StoredProcedure;
        command.Parameters.AddWithValue("@Mode", mode);
        var returnParam = command.Parameters.Add("@ReturnValue", SqlDbType.Int);
        returnParam.Direction = ParameterDirection.ReturnValue;

        int? computed = null;
        await using (var reader = await command.ExecuteReaderAsync())
        {
            if (await reader.ReadAsync())
            {
                computed = reader.GetInt32(0);
            }
        }

        await tran.RollbackAsync();
        return new ReturnResult((int)returnParam.Value, computed);
    }

    private static async Task<ReturnResult> RunThroughDebuggerAsync(
        string server, string database, int mode, StepKind stepKind = StepKind.Over, bool boost = false)
    {
        var target = new TargetEntry(server, "test", AllowWrites: false, Options: null);
        var options = new SessionOptions(
            server,
            database,
            LaunchMode.Procedure,
            "dbo.p17_return_codes",
            new Dictionary<string, string> { ["@Mode"] = mode.ToString() },
            ScriptText: null,
            Boost: boost);

        var result = await SessionHost.RunAsync(options, target, stepKind: stepKind);

        int? computed = null;
        if (result.Execution.ResultSets.Count > 0)
        {
            var resultSet = Assert.Single(result.Execution.ResultSets);
            var row = Assert.Single(resultSet.Rows);
            computed = (int)row[0]!;
        }

        return new ReturnResult(result.ReturnCode, computed);
    }
}
