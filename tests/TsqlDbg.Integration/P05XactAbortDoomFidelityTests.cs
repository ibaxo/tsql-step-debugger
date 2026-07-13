using Microsoft.Data.SqlClient;
using TsqlDbg.Core.Sessions;
using TsqlDbg.Core.Targets;
using Xunit;

namespace TsqlDbg.Integration;

// DESIGN §20.4 corpus fixture p05_xact_abort_doom + §22 M3 accept criterion. See the
// fixture's own header comment for the §10.4 watchdog / D2 sandwich / resurrection
// shapes it exercises end-to-end against a live SQL Server 2022 instance.
public sealed class P05XactAbortDoomFidelityTests
{
    private const string ConnEnvVar = "TSQLDBG_TEST_CONN";

    private sealed record DoomResult(
        int Before, int? CaughtNumber, string? CaughtMessage, int AfterRollback, int FinalXactState);

    [SkippableFact]
    public async Task DebuggerRun_MatchesNativeRun_ForXactAbortDoom()
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
    // the full pipeline and can genuinely fail), not assumed. (The doom/watchdog
    // machinery is entirely frame-0-local here, unaffected by step granularity.)
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
    // to the pass-1 method above — the refusal-equivalence story (design note §10 item
    // 6 / A21): B2 refuses boost outright once the session is doomed, so a doomed
    // fixture's pass-3 run is byte-for-byte the same interpreted walk as pass 1.
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
        var sqlPath = Path.Combine(AppContext.BaseDirectory, "corpus", "p05_xact_abort_doom.sql");
        var script = await File.ReadAllTextAsync(sqlPath);

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = script;
        await command.ExecuteNonQueryAsync();
    }

    // Native run per §20.3, adapted: the debugger always has an EXPLICIT transaction
    // open when the debuggee runs (§4 step 5's own `BEGIN TRANSACTION;`) — that is what
    // lets XACT_ABORT ON reach genuinely DOOMED (XACT_STATE() = -1) rather than just
    // auto-rolling-back an implicit per-statement transaction the way autocommit mode
    // would. So the native comparison needs its own explicit BEGIN TRANSACTION too, sent
    // as plain T-SQL text (not an ADO.NET SqlTransaction object) — the debuggee's own
    // mid-batch ROLLBACK would otherwise desync SqlTransaction's client-side bookkeeping
    // from the server's real trancount, exactly the confusion Session.cs's own remarks
    // call out for why it never sets SqlCommand.Transaction either.
    private static async Task<DoomResult> RunNativeAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        await using (var beginCommand = connection.CreateCommand())
        {
            beginCommand.CommandText = "BEGIN TRANSACTION;";
            await beginCommand.ExecuteNonQueryAsync();
        }

        DoomResult result;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "EXEC dbo.p05_xact_abort_doom @Seed = 7;";
            await using var reader = await command.ExecuteReaderAsync();
            await reader.ReadAsync();
            result = ReadRow(reader);
        }

        await using (var cleanupCommand = connection.CreateCommand())
        {
            cleanupCommand.CommandText = "IF @@TRANCOUNT > 0 ROLLBACK;";
            await cleanupCommand.ExecuteNonQueryAsync();
        }

        return result;
    }

    private static async Task<DoomResult> RunThroughDebuggerAsync(
        string server, string database, StepKind stepKind = StepKind.Over, bool boost = false)
    {
        var target = new TargetEntry(server, "test", AllowWrites: false, Options: null);
        var options = new SessionOptions(
            server,
            database,
            LaunchMode.Procedure,
            "dbo.p05_xact_abort_doom",
            new Dictionary<string, string> { ["@Seed"] = "7" },
            ScriptText: null,
            Boost: boost);

        var result = await SessionHost.RunAsync(options, target, stepKind: stepKind);

        var resultSet = Assert.Single(result.Execution.ResultSets);
        var row = Assert.Single(resultSet.Rows);
        return new DoomResult(
            (int)row[0]!, (int?)row[1], (string?)row[2], (int)row[3]!, Convert.ToInt32(row[4]));
    }

    // XACT_STATE() returns smallint (Int16), not int — GetInt32 throws InvalidCastException
    // on it directly; same reason the debugger-side row read above goes through Convert.
    private static DoomResult ReadRow(SqlDataReader reader) => new(
        reader.GetInt32(0),
        reader.IsDBNull(1) ? null : reader.GetInt32(1),
        reader.IsDBNull(2) ? null : reader.GetString(2),
        reader.GetInt32(3),
        Convert.ToInt32(reader.GetInt16(4)));
}
