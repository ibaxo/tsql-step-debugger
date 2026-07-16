using Microsoft.Data.SqlClient;
using TsqlDbg.Core.Sessions;
using TsqlDbg.Core.Targets;
using Xunit;

namespace TsqlDbg.Integration;

// docs/DESIGN.md §20.4 corpus fixture p33_set_rowcount (C13). See the fixture's own header. The
// debuggee's SET ROWCOUNT must limit the debuggee's own statements exactly as native, revert at a
// callee's exit (§11.2), and — the point of the fixture — must NOT truncate the debugger's own
// multi-row TVP copy (the step-OVER materialization and the A62 step-INTO formal seed are both
// INSERT … SELECT over the realization). ZERO exemptions: native == debugged AND pinned.
public sealed class P33SetRowCountFidelityTests
{
    private const string ConnEnvVar = "TSQLDBG_TEST_CONN";

    private sealed record RowCountResult(int LimitedSelect, int TvpCount, int AfterCalleeRevert);

    [SkippableTheory]
    [InlineData(StepKind.Over, false)]     // step over — the TVP call materializes @t natively
    [InlineData(StepKind.Into, false)]     // step INTO — the A62 #temp→#temp formal seed copies @t
    [InlineData(StepKind.Over, true)]      // continue with boost on
    public async Task DebuggerRun_MatchesNativeRun_ForSetRowCount(StepKind stepKind, bool boost)
    {
        var rawConnectionString = Environment.GetEnvironmentVariable(ConnEnvVar);
        Skip.If(
            string.IsNullOrWhiteSpace(rawConnectionString),
            $"{ConnEnvVar} is not set; skipping fidelity test (never fake a pass — CLAUDE.md).");

        var csb = new SqlConnectionStringBuilder(rawConnectionString);
        await CorpusDeployer.DeployAsync(rawConnectionString!, "p33_set_rowcount.sql");

        var native = await RunNativeAsync(rawConnectionString!);
        var debugged = await RunThroughDebuggerAsync(csb.DataSource, csb.InitialCatalog, stepKind, boost);

        Assert.Equal(native, debugged);

        // Pinned absolutely to the live-probed native truth:
        Assert.Equal(2, debugged.LimitedSelect);       // debuggee SELECT limited to 2
        Assert.Equal(5, debugged.TvpCount);            // the 5-row TVP is NOT truncated by ROWCOUNT
        Assert.Equal(2, debugged.AfterCalleeRevert);   // callee's SET ROWCOUNT 1 reverted; caller's 2 restored
    }

    private static async Task<RowCountResult> RunNativeAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var tran = (SqlTransaction)await connection.BeginTransactionAsync();

        await using var command = connection.CreateCommand();
        command.Transaction = tran;
        command.CommandText = "EXEC dbo.p33_set_rowcount;";
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        var result = new RowCountResult(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2));
        await reader.DisposeAsync();

        await tran.RollbackAsync();
        return result;
    }

    private static async Task<RowCountResult> RunThroughDebuggerAsync(
        string server, string database, StepKind stepKind, bool boost)
    {
        var target = new TargetEntry(server, "test", AllowWrites: false, Options: null);
        var options = new SessionOptions(
            server,
            database,
            LaunchMode.Procedure,
            "dbo.p33_set_rowcount",
            Args: null,
            ScriptText: null,
            Boost: boost);

        var result = await SessionHost.RunAsync(options, target, stepKind: stepKind);

        var resultSet = Assert.Single(result.Execution.ResultSets);
        var row = Assert.Single(resultSet.Rows);
        return new RowCountResult((int)row[0]!, (int)row[1]!, (int)row[2]!);
    }
}
