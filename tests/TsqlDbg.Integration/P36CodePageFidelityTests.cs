using Microsoft.Data.SqlClient;
using TsqlDbg.Core.Sessions;
using TsqlDbg.Core.Targets;
using Xunit;

namespace TsqlDbg.Integration;

// docs/DESIGN.md §20.4 corpus fixture p36_codepage (C14 Fable follow-up F1). Runs against a
// dedicated CYRILLIC (CP1251) database — a DIFFERENT code page from tempdb (CP1252) — so a
// non-ASCII varchar transcodes unless the tempdb column carries the database collation. Covers
// BOTH the §8.1 scalar-variable state table (F1) and the §9 table-variable realization (C14).
// ZERO exemptions: native == debugged AND pinned to the codepoint 1103 (U+044F).
public sealed class P36CodePageFidelityTests
{
    private const string ConnEnvVar = "TSQLDBG_TEST_CONN";
    private const string CodePageDb = "TsqlDbgCp1251";
    private const string DbCollation = "Cyrillic_General_CI_AS";

    private sealed record CodePageResult(int ScalarCode, int TableVarCode);

    [SkippableTheory]
    [InlineData(StepKind.Over, false)]
    [InlineData(StepKind.Into, false)]
    [InlineData(StepKind.Over, true)]
    public async Task DebuggerRun_MatchesNativeRun_AcrossCodePages(StepKind stepKind, bool boost)
    {
        var rawConnectionString = Environment.GetEnvironmentVariable(ConnEnvVar);
        Skip.If(
            string.IsNullOrWhiteSpace(rawConnectionString),
            $"{ConnEnvVar} is not set; skipping fidelity test (never fake a pass — CLAUDE.md).");

        await EnsureCodePageDatabaseAsync(rawConnectionString!);

        var csb = new SqlConnectionStringBuilder(rawConnectionString!) { InitialCatalog = CodePageDb };
        await CorpusDeployer.DeployAsync(csb.ConnectionString, "p36_codepage.sql");

        var native = await RunNativeAsync(csb.ConnectionString);
        var debugged = await RunThroughDebuggerAsync(csb.DataSource, CodePageDb, stepKind, boost);

        Assert.Equal(native, debugged);

        // Pinned to the Cyrillic character's codepoint — a CP1252 tempdb column would return 63 ('?'):
        Assert.Equal(1103, debugged.ScalarCode);      // plain char SCALAR survived the state table (F1)
        Assert.Equal(1103, debugged.TableVarCode);    // table-variable char column survived its realization (C14)
    }

    private static async Task EnsureCodePageDatabaseAsync(string rawConnectionString)
    {
        var master = new SqlConnectionStringBuilder(rawConnectionString) { InitialCatalog = "master" }.ConnectionString;
        await using var connection = new SqlConnection(master);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"IF DB_ID('{CodePageDb}') IS NULL CREATE DATABASE [{CodePageDb}] COLLATE {DbCollation};";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<CodePageResult> RunNativeAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var tran = (SqlTransaction)await connection.BeginTransactionAsync();

        await using var command = connection.CreateCommand();
        command.Transaction = tran;
        command.CommandText = "EXEC dbo.p36_codepage;";
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        var result = new CodePageResult(reader.GetInt32(0), reader.GetInt32(1));
        await reader.DisposeAsync();

        await tran.RollbackAsync();
        return result;
    }

    private static async Task<CodePageResult> RunThroughDebuggerAsync(
        string server, string database, StepKind stepKind, bool boost)
    {
        var target = new TargetEntry(server, "test", AllowWrites: false, Options: null);
        var options = new SessionOptions(
            server, database, LaunchMode.Procedure,
            "dbo.p36_codepage", Args: null, ScriptText: null, Boost: boost);

        var result = await SessionHost.RunAsync(options, target, stepKind: stepKind);

        var resultSet = Assert.Single(result.Execution.ResultSets);
        var row = Assert.Single(resultSet.Rows);
        return new CodePageResult((int)row[0]!, (int)row[1]!);
    }
}
