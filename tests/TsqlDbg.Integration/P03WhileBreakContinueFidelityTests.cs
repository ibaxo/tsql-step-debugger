using Microsoft.Data.SqlClient;
using TsqlDbg.Core.Sessions;
using TsqlDbg.Core.Targets;
using TsqlDbg.Core.Tracing;
using Xunit;

namespace TsqlDbg.Integration;

// DESIGN §20.4 corpus fixture p03_while_break_continue + §22 M2 accept criterion
// (Accept: p02-p03, p14, p17, p18). See the fixture's own header comment for the
// WHILE-predicate fact-12/D3 pattern, fact 14 C loop-body DECLARE reinitialization,
// and nested BREAK/CONTINUE it exercises.
public sealed class P03WhileBreakContinueFidelityTests
{
    private const string ConnEnvVar = "TSQLDBG_TEST_CONN";

    private sealed record LoopResult(int? RowcountInLoop, string DoubledLog, string NestedLog);

    [SkippableFact]
    public async Task DebuggerRun_MatchesNativeRun_ForWhileBreakContinue()
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

    // M6 item 6 (B9): fidelity pass 3 — continue with boost on, assertions identical
    // to the pass-1 method above. This is one of the two loop fixtures (the other is
    // p08) pinned to prove boost actually FIRED here, not just that pass 3 stayed
    // green by every subtree refusing (the p04 lesson applied to boost, per the note
    // §10 item 6): the first WHILE refuses (its predicate reads @@ROWCOUNT — R4's
    // intrinsic), the fact-14-C loop refuses (a DECLARE-with-initializer in its body),
    // but the nested BREAK/CONTINUE loop (@j/@k) has no intrinsic reference, no
    // DECLARE, and no TRY/CATCH anywhere in its subtree, so the outer WHILE @j < @Outer
    // is eligible as ONE boosted node (the inner WHILE @k < @Outer nests inside it).
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
        var debugged = await RunThroughDebuggerAsync(server, database, boost: true, trace: trace);

        Assert.Equal(native, debugged);
        Assert.Contains(trace.Events, e => e.Category == "boost.fire");
    }

    private static async Task DeployFixtureAsync(string connectionString)
    {
        var sqlPath = Path.Combine(AppContext.BaseDirectory, "corpus", "p03_while_break_continue.sql");
        var script = await File.ReadAllTextAsync(sqlPath);

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = script;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<LoopResult> RunNativeAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var tran = (SqlTransaction)await connection.BeginTransactionAsync();

        await using var command = connection.CreateCommand();
        command.Transaction = tran;
        command.CommandText = "EXEC dbo.p03_while_break_continue @Outer = 4;";
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        var result = new LoopResult(
            reader.IsDBNull(0) ? null : reader.GetInt32(0), reader.GetString(1), reader.GetString(2));
        await reader.DisposeAsync();

        await tran.RollbackAsync();
        return result;
    }

    private static async Task<LoopResult> RunThroughDebuggerAsync(
        string server, string database, StepKind stepKind = StepKind.Over, bool boost = false, ITraceSink? trace = null)
    {
        var target = new TargetEntry(server, "test", AllowWrites: false, Options: null);
        var options = new SessionOptions(
            server,
            database,
            LaunchMode.Procedure,
            "dbo.p03_while_break_continue",
            new Dictionary<string, string> { ["@Outer"] = "4" },
            ScriptText: null,
            Boost: boost);

        var result = await SessionHost.RunAsync(options, target, trace, stepKind: stepKind);

        var resultSet = Assert.Single(result.Execution.ResultSets);
        var row = Assert.Single(resultSet.Rows);
        return new LoopResult((int?)row[0], (string)row[1]!, (string)row[2]!);
    }
}
