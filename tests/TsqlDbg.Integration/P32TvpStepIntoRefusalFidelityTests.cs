using Microsoft.Data.SqlClient;
using TsqlDbg.Core.Sessions;
using TsqlDbg.Core.Targets;
using Xunit;

namespace TsqlDbg.Integration;

// docs/DESIGN.md §20.4 corpus fixture p32_tvp_step_into_refusals (A62 F1–F4). See the fixture's own
// header. Every TVP shape the ENGINE rejects at the call must fall through to a faithful step-OVER,
// so the server raises its own error and the proc's TRY/CATCH captures it exactly as native does —
// the debugger must neither crash the session (F1/F2, pre-fix unhandled 137/invalid-column) nor run
// a callee the engine compile-refuses (F3/F4). ZERO exemptions: native and debugged rows are equal
// AND pinned to the live-probed truth, so a mutually-wrong pair cannot pass.
public sealed class P32TvpStepIntoRefusalFidelityTests
{
    private const string ConnEnvVar = "TSQLDBG_TEST_CONN";

    private sealed record RefusalResult(int OkCount, int F4, int F1, int F2a, int F2b, int F3a, int F3b);

    [SkippableTheory]
    [InlineData(StepKind.Over, false)]     // step over — every callee runs natively
    [InlineData(StepKind.Into, false)]     // step INTO — the guards must fire (the point of the fixture)
    [InlineData(StepKind.Over, true)]      // continue with boost on
    public async Task DebuggerRun_MatchesNativeRun_ForEveryRefusedTvpShape(StepKind stepKind, bool boost)
    {
        var rawConnectionString = Environment.GetEnvironmentVariable(ConnEnvVar);
        Skip.If(
            string.IsNullOrWhiteSpace(rawConnectionString),
            $"{ConnEnvVar} is not set; skipping fidelity test (never fake a pass — CLAUDE.md).");

        var csb = new SqlConnectionStringBuilder(rawConnectionString);
        await CorpusDeployer.DeployAsync(rawConnectionString!, "p32_tvp_step_into_refusals.sql");

        var native = await RunNativeAsync(rawConnectionString!);
        var debugged = await RunThroughDebuggerAsync(csb.DataSource, csb.InitialCatalog, stepKind, boost);

        Assert.Equal(native, debugged);

        // Pinned absolutely to the live-probed native truth:
        Assert.Equal(3, debugged.OkCount);     // happy path stepped IN, callee saw all 3 rows
        Assert.Equal(206, debugged.F4);        // same columns, different table type → operand type clash
        Assert.Equal(206, debugged.F1);        // different table type, different columns
        Assert.Equal(352, debugged.F2a);       // table-type formal missing READONLY
        Assert.Equal(206, debugged.F2b);       // table-type variable to a scalar formal
        Assert.Equal(10700, debugged.F3a);     // aliased write of a READONLY TVP (compile error)
        Assert.Equal(10700, debugged.F3b);     // OUTPUT … INTO write of a READONLY TVP (compile error)
    }

    private static async Task<RefusalResult> RunNativeAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var tran = (SqlTransaction)await connection.BeginTransactionAsync();

        await using var command = connection.CreateCommand();
        command.Transaction = tran;
        command.CommandText = "EXEC dbo.p32_tvp_step_into_refusals;";
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        var result = new RefusalResult(
            reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3),
            reader.GetInt32(4), reader.GetInt32(5), reader.GetInt32(6));
        await reader.DisposeAsync();

        await tran.RollbackAsync();
        return result;
    }

    private static async Task<RefusalResult> RunThroughDebuggerAsync(
        string server, string database, StepKind stepKind, bool boost)
    {
        var target = new TargetEntry(server, "test", AllowWrites: false, Options: null);
        var options = new SessionOptions(
            server,
            database,
            LaunchMode.Procedure,
            "dbo.p32_tvp_step_into_refusals",
            Args: null,
            ScriptText: null,
            Boost: boost);

        var result = await SessionHost.RunAsync(options, target, stepKind: stepKind);

        var resultSet = Assert.Single(result.Execution.ResultSets);
        var row = Assert.Single(resultSet.Rows);
        return new RefusalResult(
            (int)row[0]!, (int)row[1]!, (int)row[2]!, (int)row[3]!, (int)row[4]!, (int)row[5]!, (int)row[6]!);
    }
}
