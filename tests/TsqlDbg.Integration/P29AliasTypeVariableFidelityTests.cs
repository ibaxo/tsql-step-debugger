using Microsoft.Data.SqlClient;
using TsqlDbg.Core.Sessions;
using TsqlDbg.Core.Targets;
using Xunit;

namespace TsqlDbg.Integration;

// docs/DESIGN.md §20.4 corpus fixture p29_alias_type_variable (A59). See the fixture's own
// header for what it exercises. ZERO exemptions — an alias-typed variable is a base-typed
// value wearing a name (fact 34b), so the debugger has no licence to diverge on it.
//
// This is the test that would have caught the reported bug: before A59, every pass below
// failed at LAUNCH (msg 2715, "Cannot find data type dbo.p29_Name") — the session never
// reached a step, because §8.1's state table put the DECLARED type into a tempdb column.
public sealed class P29AliasTypeVariableFidelityTests
{
    private const string ConnEnvVar = "TSQLDBG_TEST_CONN";

    private sealed record AliasResult(
        string Greeting, decimal Total, int NameCount, int LongestName, string TotalBaseType);

    [SkippableTheory]
    [InlineData(StepKind.Over, false)]
    [InlineData(StepKind.Into, false)]
    [InlineData(StepKind.Over, true)]      // pass 3: continue with boost on
    public async Task DebuggerRun_MatchesNativeRun_ForAliasTypedVariables(StepKind stepKind, bool boost)
    {
        var rawConnectionString = Environment.GetEnvironmentVariable(ConnEnvVar);
        Skip.If(
            string.IsNullOrWhiteSpace(rawConnectionString),
            $"{ConnEnvVar} is not set; skipping fidelity test (never fake a pass — CLAUDE.md).");

        var csb = new SqlConnectionStringBuilder(rawConnectionString);
        await CorpusDeployer.DeployAsync(rawConnectionString!, "p29_alias_type_variable.sql");

        var native = await RunNativeAsync(rawConnectionString!);
        var debugged = await RunThroughDebuggerAsync(csb.DataSource, csb.InitialCatalog, stepKind, boost);

        Assert.Equal(native.Greeting, debugged.Greeting);
        Assert.Equal(native.Total, debugged.Total);
        Assert.Equal(native.NameCount, debugged.NameCount);
        Assert.Equal(native.LongestName, debugged.LongestName);
        Assert.Equal(native.TotalBaseType, debugged.TotalBaseType);

        // Pin the values themselves, so a mutually-wrong pair cannot pass: the alias
        // decimal(9,2) must accumulate 3 x 2.50 exactly, and must still BE a decimal —
        // the storage type carries the scale, and a lossy one would show here.
        Assert.Equal("Hello, Ada", debugged.Greeting);
        Assert.Equal(7.50m, debugged.Total);
        Assert.Equal("decimal", debugged.TotalBaseType);
        Assert.Equal(2, debugged.NameCount);
    }

    private static async Task<AliasResult> RunNativeAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var tran = (SqlTransaction)await connection.BeginTransactionAsync();

        await using var command = connection.CreateCommand();
        command.Transaction = tran;
        command.CommandText = "EXEC dbo.p29_alias_type_variable @Customer = N'Ada';";
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        var result = new AliasResult(
            reader.GetString(0), reader.GetDecimal(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetString(4));
        await reader.DisposeAsync();

        await tran.RollbackAsync();
        return result;
    }

    private static async Task<AliasResult> RunThroughDebuggerAsync(
        string server, string database, StepKind stepKind, bool boost)
    {
        var target = new TargetEntry(server, "test", AllowWrites: false, Options: null);
        var options = new SessionOptions(
            server,
            database,
            LaunchMode.Procedure,
            "dbo.p29_alias_type_variable",
            new Dictionary<string, string> { ["@Customer"] = "N'Ada'" },
            ScriptText: null,
            Boost: boost);

        var result = await SessionHost.RunAsync(options, target, stepKind: stepKind);

        var resultSet = Assert.Single(result.Execution.ResultSets);
        var row = Assert.Single(resultSet.Rows);
        return new AliasResult(
            (string)row[0]!, (decimal)row[1]!, (int)row[2]!, (int)row[3]!, (string)row[4]!);
    }
}
