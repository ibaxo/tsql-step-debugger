using Microsoft.Data.SqlClient;
using TsqlDbg.Core.Sessions;
using TsqlDbg.Core.Targets;
using Xunit;

namespace TsqlDbg.Integration;

// docs/DESIGN.md §20.4 corpus fixture p34_collation (C14). See the fixture's own header. A table
// variable's char columns inherit the DATABASE collation; the debugger realizes the variable as a
// #temp, whose columns would default to tempdb's collation. This test runs against a dedicated
// CASE-SENSITIVE database (Latin1_General_CS_AS) — created on demand — so the divergence is
// observable (tempdb is case-insensitive here). ZERO exemptions: native == debugged AND pinned.
public sealed class P34CollationFidelityTests
{
    private const string ConnEnvVar = "TSQLDBG_TEST_CONN";
    private const string CollationDb = "TsqlDbgCollTest";
    private const string DbCollation = "Latin1_General_CS_AS";

    private sealed record CollationResult(int DefaultColMatch, int ExplicitColMatch);

    [SkippableTheory]
    [InlineData(StepKind.Over, false)]
    [InlineData(StepKind.Into, false)]
    [InlineData(StepKind.Over, true)]      // continue with boost on
    public async Task DebuggerRun_MatchesNativeRun_ForCollation(StepKind stepKind, bool boost)
    {
        var rawConnectionString = Environment.GetEnvironmentVariable(ConnEnvVar);
        Skip.If(
            string.IsNullOrWhiteSpace(rawConnectionString),
            $"{ConnEnvVar} is not set; skipping fidelity test (never fake a pass — CLAUDE.md).");

        await EnsureCollationDatabaseAsync(rawConnectionString!);

        var csb = new SqlConnectionStringBuilder(rawConnectionString!) { InitialCatalog = CollationDb };
        await CorpusDeployer.DeployAsync(csb.ConnectionString, "p34_collation.sql");

        var native = await RunNativeAsync(csb.ConnectionString);
        var debugged = await RunThroughDebuggerAsync(csb.DataSource, CollationDb, stepKind, boost);

        Assert.Equal(native, debugged);

        // Pinned to the case-sensitive database's native truth:
        Assert.Equal(0, debugged.DefaultColMatch);     // un-COLLATE'd column keeps the DB's CS collation ('a' <> 'A')
        Assert.Equal(1, debugged.ExplicitColMatch);     // explicit CI COLLATE preserved, NOT overwritten by the DB's CS default ('x' = 'X')
    }

    // The case-sensitive database differs in collation from tempdb — that difference is the whole
    // point of the fixture. Created once; harmless if it already exists.
    private static async Task EnsureCollationDatabaseAsync(string rawConnectionString)
    {
        var master = new SqlConnectionStringBuilder(rawConnectionString) { InitialCatalog = "master" }.ConnectionString;
        await using var connection = new SqlConnection(master);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"IF DB_ID('{CollationDb}') IS NULL CREATE DATABASE [{CollationDb}] COLLATE {DbCollation};";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<CollationResult> RunNativeAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var tran = (SqlTransaction)await connection.BeginTransactionAsync();

        await using var command = connection.CreateCommand();
        command.Transaction = tran;
        command.CommandText = "EXEC dbo.p34_collation;";
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        var result = new CollationResult(reader.GetInt32(0), reader.GetInt32(1));
        await reader.DisposeAsync();

        await tran.RollbackAsync();
        return result;
    }

    private static async Task<CollationResult> RunThroughDebuggerAsync(
        string server, string database, StepKind stepKind, bool boost)
    {
        var target = new TargetEntry(server, "test", AllowWrites: false, Options: null);
        var options = new SessionOptions(
            server,
            database,
            LaunchMode.Procedure,
            "dbo.p34_collation",
            Args: null,
            ScriptText: null,
            Boost: boost);

        var result = await SessionHost.RunAsync(options, target, stepKind: stepKind);

        var resultSet = Assert.Single(result.Execution.ResultSets);
        var row = Assert.Single(resultSet.Rows);
        return new CollationResult((int)row[0]!, (int)row[1]!);
    }
}
