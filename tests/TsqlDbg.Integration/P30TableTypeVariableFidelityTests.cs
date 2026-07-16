using Microsoft.Data.SqlClient;
using TsqlDbg.Core.Sessions;
using TsqlDbg.Core.Targets;
using Xunit;

namespace TsqlDbg.Integration;

// docs/DESIGN.md §20.4 corpus fixture p30_table_type_variable (A59). See the fixture's own
// header. ZERO exemptions: the realization DDL is rebuilt from the catalog (fact 34f) and
// must reproduce IDENTITY, DEFAULT, computed columns, PK/UNIQUE and CHECK exactly, and the
// §9 preamble materialization must deliver the rows to the TVP formal unchanged.
//
// C28 (regenerated identities) is deliberately NOT reachable here — the fixture inserts
// contiguously, so native and debugger agree on 10/15/20. C28's gap shape is pinned
// separately, as a live probe, in C28TvpIdentityGapLiveTests.
public sealed class P30TableTypeVariableFidelityTests
{
    private const string ConnEnvVar = "TSQLDBG_TEST_CONN";

    private sealed record TableTypeResult(
        int Cnt, decimal Total, decimal Doubled, int MaxId, int CalleeCode);

    [SkippableTheory]
    [InlineData(StepKind.Over, false)]
    [InlineData(StepKind.Into, false)]     // A62: the EXEC now steps INTO the TVP callee (was C9 step-over pre-A62); result identical
    [InlineData(StepKind.Over, true)]      // pass 3: continue with boost on
    public async Task DebuggerRun_MatchesNativeRun_ForTableTypeVariable(StepKind stepKind, bool boost)
    {
        var rawConnectionString = Environment.GetEnvironmentVariable(ConnEnvVar);
        Skip.If(
            string.IsNullOrWhiteSpace(rawConnectionString),
            $"{ConnEnvVar} is not set; skipping fidelity test (never fake a pass — CLAUDE.md).");

        var csb = new SqlConnectionStringBuilder(rawConnectionString);
        await CorpusDeployer.DeployAsync(rawConnectionString!, "p30_table_type_variable.sql");

        var native = await RunNativeAsync(rawConnectionString!);
        var debugged = await RunThroughDebuggerAsync(csb.DataSource, csb.InitialCatalog, stepKind, boost);

        Assert.Equal(native.Cnt, debugged.Cnt);
        Assert.Equal(native.Total, debugged.Total);
        Assert.Equal(native.Doubled, debugged.Doubled);
        Assert.Equal(native.MaxId, debugged.MaxId);
        Assert.Equal(native.CalleeCode, debugged.CalleeCode);

        // Pinned absolutely, so a mutually-wrong pair cannot pass:
        Assert.Equal(3, debugged.Cnt);                 // 2 explicit + 1 defaulted row
        Assert.Equal(7.000m, debugged.Total);          // 2.0 + 3.5 + DEFAULT 1.5
        Assert.Equal(14.000m, debugged.Doubled);       // the computed column survived the rebuild
        Assert.Equal(20, debugged.MaxId);              // IDENTITY(10,5) -> 10, 15, 20
        Assert.Equal(310, debugged.CalleeCode);        // callee saw 3 rows, min id 10
    }

    private static async Task<TableTypeResult> RunNativeAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var tran = (SqlTransaction)await connection.BeginTransactionAsync();

        await using var command = connection.CreateCommand();
        command.Transaction = tran;
        command.CommandText = "EXEC dbo.p30_table_type_variable;";
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        var result = new TableTypeResult(
            reader.GetInt32(0), reader.GetDecimal(1), reader.GetDecimal(2),
            reader.GetInt32(3), reader.GetInt32(4));
        await reader.DisposeAsync();

        await tran.RollbackAsync();
        return result;
    }

    private static async Task<TableTypeResult> RunThroughDebuggerAsync(
        string server, string database, StepKind stepKind, bool boost)
    {
        var target = new TargetEntry(server, "test", AllowWrites: false, Options: null);
        var options = new SessionOptions(
            server,
            database,
            LaunchMode.Procedure,
            "dbo.p30_table_type_variable",
            Args: null,
            ScriptText: null,
            Boost: boost);

        var result = await SessionHost.RunAsync(options, target, stepKind: stepKind);

        var resultSet = Assert.Single(result.Execution.ResultSets);
        var row = Assert.Single(resultSet.Rows);
        return new TableTypeResult(
            (int)row[0]!, (decimal)row[1]!, (decimal)row[2]!, (int)row[3]!, (int)row[4]!);
    }
}
