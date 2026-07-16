using Microsoft.Data.SqlClient;
using TsqlDbg.Core.Sessions;
using TsqlDbg.Core.Targets;
using Xunit;

namespace TsqlDbg.Integration;

// docs/DESIGN.md §20.4 corpus fixture p31_tvp_step_into (A62). See the fixture's own header.
// Step-INTO a callee that takes a table-valued (READONLY) parameter — the half of C9 that
// A59 left refused. The Into pass is the point of the fixture: it drives the §11.3-step-2
// #temp -> #temp seed for BOTH a stored-procedure callee and an sp_executesql dynamic frame,
// including a TVP mixed with two scalar OUTPUT parameters. ZERO exemptions — identity values
// are contiguous, so C28 is not reachable and the callee's aggregates must match natively.
public sealed class P31TvpStepIntoFidelityTests
{
    private const string ConnEnvVar = "TSQLDBG_TEST_CONN";

    private sealed record StepIntoResult(int ProcSum, int DynCount, int DynSum);

    [SkippableTheory]
    [InlineData(StepKind.Over, false)]     // pass 1: step over — the callees run natively, A59 materialization
    [InlineData(StepKind.Into, false)]     // pass 2: step INTO both TVP callees — the A62 seed path
    [InlineData(StepKind.Over, true)]      // pass 3: continue with boost on (a TVP-passing subtree is boost-refused)
    public async Task DebuggerRun_MatchesNativeRun_SteppingIntoTvpCallees(StepKind stepKind, bool boost)
    {
        var rawConnectionString = Environment.GetEnvironmentVariable(ConnEnvVar);
        Skip.If(
            string.IsNullOrWhiteSpace(rawConnectionString),
            $"{ConnEnvVar} is not set; skipping fidelity test (never fake a pass — CLAUDE.md).");

        var csb = new SqlConnectionStringBuilder(rawConnectionString);
        await CorpusDeployer.DeployAsync(rawConnectionString!, "p31_tvp_step_into.sql");

        var native = await RunNativeAsync(rawConnectionString!);
        var debugged = await RunThroughDebuggerAsync(csb.DataSource, csb.InitialCatalog, stepKind, boost);

        Assert.Equal(native.ProcSum, debugged.ProcSum);
        Assert.Equal(native.DynCount, debugged.DynCount);
        Assert.Equal(native.DynSum, debugged.DynSum);

        // Pinned absolutely, so a mutually-wrong pair cannot pass:
        Assert.Equal(60, debugged.ProcSum);            // proc callee summed the 3 copied rows (10+20+30)
        Assert.Equal(3, debugged.DynCount);            // dynamic-frame callee counted them
        Assert.Equal(60, debugged.DynSum);             // dynamic-frame callee summed them
    }

    private static async Task<StepIntoResult> RunNativeAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var tran = (SqlTransaction)await connection.BeginTransactionAsync();

        await using var command = connection.CreateCommand();
        command.Transaction = tran;
        command.CommandText = "EXEC dbo.p31_tvp_step_into;";
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        var result = new StepIntoResult(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2));
        await reader.DisposeAsync();

        await tran.RollbackAsync();
        return result;
    }

    private static async Task<StepIntoResult> RunThroughDebuggerAsync(
        string server, string database, StepKind stepKind, bool boost)
    {
        var target = new TargetEntry(server, "test", AllowWrites: false, Options: null);
        var options = new SessionOptions(
            server,
            database,
            LaunchMode.Procedure,
            "dbo.p31_tvp_step_into",
            Args: null,
            ScriptText: null,
            Boost: boost);

        var result = await SessionHost.RunAsync(options, target, stepKind: stepKind);

        var resultSet = Assert.Single(result.Execution.ResultSets);
        var row = Assert.Single(resultSet.Rows);
        return new StepIntoResult((int)row[0]!, (int)row[1]!, (int)row[2]!);
    }
}
