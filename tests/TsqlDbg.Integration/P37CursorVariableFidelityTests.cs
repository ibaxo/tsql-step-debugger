using Microsoft.Data.SqlClient;
using TsqlDbg.Core.Sessions;
using TsqlDbg.Core.Targets;
using TsqlDbg.Core.Tracing;
using Xunit;

namespace TsqlDbg.Integration;

// docs/DESIGN.md §20.4 corpus fixture p37_cursor_variable + §21 C12 (A63). Proves cursor
// VARIABLES (DECLARE @c CURSOR; SET @c = CURSOR FOR …) are byte-faithful to native across
// Over/Into/Boost — including a re-SET of the same variable (the CURSOR_STATUS guard) and
// an explicit DEALLOCATE @c. See the fixture header for the shape it exercises.
public sealed class P37CursorVariableFidelityTests
{
    private const string ConnEnvVar = "TSQLDBG_TEST_CONN";

    private sealed record CursorResult(int Total, int Count);

    [SkippableFact]
    public async Task DebuggerRun_MatchesNativeRun_ForCursorVariableLoop()
    {
        var rawConnectionString = Environment.GetEnvironmentVariable(ConnEnvVar);
        Skip.If(
            string.IsNullOrWhiteSpace(rawConnectionString),
            $"{ConnEnvVar} is not set; skipping fidelity test (never fake a pass — CLAUDE.md).");

        var csb = new SqlConnectionStringBuilder(rawConnectionString);
        await DeployFixtureAsync(rawConnectionString!);

        var native = await RunNativeAsync(rawConnectionString!);
        var debugged = await RunThroughDebuggerAsync(csb.DataSource, csb.InitialCatalog);

        Assert.Equal(native, debugged);
    }

    // EXEC-free fixture, so Into is trajectory-identical to Over — asserted, not assumed.
    [SkippableFact]
    public async Task DebuggerRun_MatchesNativeRun_SteppedInto()
    {
        var rawConnectionString = Environment.GetEnvironmentVariable(ConnEnvVar);
        Skip.If(
            string.IsNullOrWhiteSpace(rawConnectionString),
            $"{ConnEnvVar} is not set; skipping fidelity test (never fake a pass — CLAUDE.md).");

        var csb = new SqlConnectionStringBuilder(rawConnectionString);
        await DeployFixtureAsync(rawConnectionString!);

        var native = await RunNativeAsync(rawConnectionString!);
        var debugged = await RunThroughDebuggerAsync(csb.DataSource, csb.InitialCatalog, StepKind.Into);

        Assert.Equal(native, debugged);
    }

    private sealed class RecordingSink : ITraceSink
    {
        public List<(string Category, string Message)> Events { get; } = new();
        public void Event(string category, string message) => Events.Add((category, message));
    }

    // A63 + §14/A21: the two WHILE @@FETCH_STATUS loops (body SET/CursorOp FETCH only) are
    // boost-eligible; the reifying `SET @c = CURSOR …` (CursorDeclare) between them is NOT in
    // the A21 whitelist, so it stays interpreted while the loops boost. Asserts boost fires and
    // the result is still native-identical.
    [SkippableFact]
    public async Task DebuggerRun_MatchesNativeRun_ContinueBoost()
    {
        var rawConnectionString = Environment.GetEnvironmentVariable(ConnEnvVar);
        Skip.If(
            string.IsNullOrWhiteSpace(rawConnectionString),
            $"{ConnEnvVar} is not set; skipping fidelity test (never fake a pass — CLAUDE.md).");

        var csb = new SqlConnectionStringBuilder(rawConnectionString);
        await DeployFixtureAsync(rawConnectionString!);

        var native = await RunNativeAsync(rawConnectionString!);
        var trace = new RecordingSink();
        var debugged = await RunThroughDebuggerAsync(csb.DataSource, csb.InitialCatalog, boost: true, trace: trace);

        Assert.Equal(native, debugged);
        Assert.Contains(trace.Events, e => e.Category == "boost.fire");
    }

    private static async Task DeployFixtureAsync(string connectionString)
    {
        var sqlPath = Path.Combine(AppContext.BaseDirectory, "corpus", "p37_cursor_variable.sql");
        var script = await File.ReadAllTextAsync(sqlPath);

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = script;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<CursorResult> RunNativeAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var tran = (SqlTransaction)await connection.BeginTransactionAsync();

        await using var command = connection.CreateCommand();
        command.Transaction = tran;
        command.CommandText = "EXEC dbo.p37_cursor_variable @Seed = 10;";
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        var result = new CursorResult(reader.GetInt32(0), reader.GetInt32(1));
        await reader.DisposeAsync();

        await tran.RollbackAsync();
        return result;
    }

    private static async Task<CursorResult> RunThroughDebuggerAsync(
        string server, string database, StepKind stepKind = StepKind.Over, bool boost = false, ITraceSink? trace = null)
    {
        var target = new TargetEntry(server, "test", AllowWrites: false, Options: null);
        var options = new SessionOptions(
            server,
            database,
            LaunchMode.Procedure,
            "dbo.p37_cursor_variable",
            new Dictionary<string, string> { ["@Seed"] = "10" },
            ScriptText: null,
            Boost: boost);

        var result = await SessionHost.RunAsync(options, target, trace, stepKind: stepKind);

        var resultSet = Assert.Single(result.Execution.ResultSets);
        var row = Assert.Single(resultSet.Rows);
        return new CursorResult((int)row[0]!, (int)row[1]!);
    }
}
