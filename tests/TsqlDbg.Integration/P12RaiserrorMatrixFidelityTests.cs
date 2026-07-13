using Microsoft.Data.SqlClient;
using TsqlDbg.Core.Sessions;
using TsqlDbg.Core.Targets;
using Xunit;

namespace TsqlDbg.Integration;

// DESIGN §20.4 corpus fixture p12_raiserror_matrix + §22 M3 accept criterion. See the
// fixture's own header comment for the severity matrix it exercises.
public sealed class P12RaiserrorMatrixFidelityTests
{
    private const string ConnEnvVar = "TSQLDBG_TEST_CONN";

    private sealed record RaiserrorResult(
        int? CaughtNumber, string? CaughtMessage, int? UncaughtErrorNumber, int? UncaughtRowcount);

    [SkippableFact]
    public async Task DebuggerRun_MatchesNativeRun_ForRaiserrorMatrix()
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
        var debugged = await RunThroughDebuggerAsync(server, database);

        Assert.Equal(native, debugged);
    }

    // M7 matrix cell (design note §6.1/§6.2): mechanical pass-2 addition -- this
    // fixture is EXEC-free, so Into is trajectory-identical to Over; asserted (runs
    // the full pipeline and can genuinely fail), not assumed.
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

    // M6 item 6 (B9): fidelity pass 3 — continue with boost on. Assertions identical
    // to the pass-1 method above.
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
        var debugged = await RunThroughDebuggerAsync(server, database, boost: true);

        Assert.Equal(native, debugged);
    }

    private static async Task DeployFixtureAsync(string connectionString)
    {
        var sqlPath = Path.Combine(AppContext.BaseDirectory, "corpus", "p12_raiserror_matrix.sql");
        var script = await File.ReadAllTextAsync(sqlPath);

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = script;
        await command.ExecuteNonQueryAsync();
    }

    // §10 error-model design notes §3.3 (Sonnet checklist note): the native runner
    // needs FireInfoMessageEventOnUserErrors = true, or the ADO.NET client throws a
    // SqlException client-side on the uncaught severity-16 RAISERROR — exactly where
    // the SERVER itself continues the batch (fact 18/20/21). Without this flag the
    // native and debugger runs would not even be comparable: the native reader would
    // never reach the final SELECT at all.
    private static async Task<RaiserrorResult> RunNativeAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        connection.FireInfoMessageEventOnUserErrors = true;
        connection.InfoMessage += (_, _) => { };
        await connection.OpenAsync();
        await using var tran = (SqlTransaction)await connection.BeginTransactionAsync();

        await using var command = connection.CreateCommand();
        command.Transaction = tran;
        command.CommandText = "EXEC dbo.p12_raiserror_matrix @Mode = 0;";
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        var result = ReadRow(reader);
        await reader.DisposeAsync();

        await tran.RollbackAsync();
        return result;
    }

    private static async Task<RaiserrorResult> RunThroughDebuggerAsync(
        string server, string database, StepKind stepKind = StepKind.Over, bool boost = false)
    {
        var target = new TargetEntry(server, "test", AllowWrites: false, Options: null);
        var options = new SessionOptions(
            server,
            database,
            LaunchMode.Procedure,
            "dbo.p12_raiserror_matrix",
            new Dictionary<string, string> { ["@Mode"] = "0" },
            ScriptText: null,
            Boost: boost);

        var result = await SessionHost.RunAsync(options, target, stepKind: stepKind);

        var resultSet = Assert.Single(result.Execution.ResultSets);
        var row = Assert.Single(resultSet.Rows);
        return new RaiserrorResult((int?)row[0], (string?)row[1], (int?)row[2], (int?)row[3]);
    }

    private static RaiserrorResult ReadRow(SqlDataReader reader) => new(
        reader.IsDBNull(0) ? null : reader.GetInt32(0),
        reader.IsDBNull(1) ? null : reader.GetString(1),
        reader.IsDBNull(2) ? null : reader.GetInt32(2),
        reader.IsDBNull(3) ? null : reader.GetInt32(3));
}
