using Microsoft.Data.SqlClient;
using TsqlDbg.Core.Sessions;
using TsqlDbg.Core.Targets;
using Xunit;

namespace TsqlDbg.Integration;

// DESIGN §20.4 corpus fixture p04_fk_violation_trycatch + §22 M3 accept criterion.
// See the fixture's own header comment for the R7-exactness and fact-21/F2 shapes it
// exercises.
public sealed class P04FkViolationTryCatchFidelityTests
{
    private const string ConnEnvVar = "TSQLDBG_TEST_CONN";

    private sealed record FkResult(
        int? CaughtNumber, string? CaughtMessage, int? UncaughtErrorNumber, int? UncaughtRowcount);

    [SkippableTheory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task DebuggerRun_MatchesNativeRun_ForFkViolationTryCatch(int mode)
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
    [InlineData(0)]
    [InlineData(1)]
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
    [InlineData(0)]
    [InlineData(1)]
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

    // CREATE/ALTER PROCEDURE must be the only statement in its batch, and this fixture
    // needs a preceding batch to (re)create dbo.p04_parent/p04_child — GO is a
    // client-tool batch separator, not real T-SQL, so it has to be split here rather
    // than sent as one ExecuteNonQueryAsync call.
    private static async Task DeployFixtureAsync(string connectionString)
    {
        var sqlPath = Path.Combine(AppContext.BaseDirectory, "corpus", "p04_fk_violation_trycatch.sql");
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

    // fact 18/20/21 continuation: an uncaught statement-level error continues the batch
    // natively (Mode=1's second, uncaught FK violation) — but the ADO.NET client throws
    // a SqlException on ANY severity>=11 message by default regardless of what the
    // server does next, unless FireInfoMessageEventOnUserErrors routes it through
    // InfoMessage instead (same fix as the p12 RAISERROR-matrix harness).
    private static async Task<FkResult> RunNativeAsync(string connectionString, int mode)
    {
        await using var connection = new SqlConnection(connectionString);
        connection.FireInfoMessageEventOnUserErrors = true;
        connection.InfoMessage += (_, _) => { };
        await connection.OpenAsync();
        await using var tran = (SqlTransaction)await connection.BeginTransactionAsync();

        await using var command = connection.CreateCommand();
        command.Transaction = tran;
        command.CommandText = "EXEC dbo.p04_fk_violation_trycatch @Mode = @Mode;";
        command.Parameters.AddWithValue("@Mode", mode);
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        var result = ReadRow(reader);
        await reader.DisposeAsync();

        await tran.RollbackAsync();
        return result;
    }

    private static async Task<FkResult> RunThroughDebuggerAsync(
        string server, string database, int mode, StepKind stepKind = StepKind.Over, bool boost = false)
    {
        var target = new TargetEntry(server, "test", AllowWrites: false, Options: null);
        var options = new SessionOptions(
            server,
            database,
            LaunchMode.Procedure,
            "dbo.p04_fk_violation_trycatch",
            new Dictionary<string, string> { ["@Mode"] = mode.ToString() },
            ScriptText: null,
            Boost: boost);

        var result = await SessionHost.RunAsync(options, target, stepKind: stepKind);

        var resultSet = Assert.Single(result.Execution.ResultSets);
        var row = Assert.Single(resultSet.Rows);
        return new FkResult((int?)row[0], (string?)row[1], (int?)row[2], (int?)row[3]);
    }

    private static FkResult ReadRow(SqlDataReader reader) => new(
        reader.IsDBNull(0) ? null : reader.GetInt32(0),
        reader.IsDBNull(1) ? null : reader.GetString(1),
        reader.IsDBNull(2) ? null : reader.GetInt32(2),
        reader.IsDBNull(3) ? null : reader.GetInt32(3));
}
