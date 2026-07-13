using Microsoft.Data.SqlClient;
using TsqlDbg.Core.Sessions;
using TsqlDbg.Core.Targets;
using Xunit;

namespace TsqlDbg.Integration;

// DESIGN §20.4 corpus fixture p15_scope_identity_rowcount_chains + §22 M1 accept
// criterion ("Accept: p01, p15"). Exercises R4/R6 shadow substitution across
// composed-batch boundaries (see the fixture's own header comment for why this
// specific chain shape matters).
[Collection("P15SharedFixture")]
public sealed class P15ScopeIdentityRowcountChainsFidelityTests
{
    private const string ConnEnvVar = "TSQLDBG_TEST_CONN";

    private sealed record ChainResult(int FirstId, int FirstRowcount, int SecondId, int SecondRowcount, int UpdateRowcount);

    [SkippableFact]
    public async Task DebuggerRun_MatchesNativeRun_ForScopeIdentityRowcountChain()
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

    // M7 matrix cell (design note §6.1/§6.2): mechanical pass-2 addition -- the
    // ORIGINAL p15 proc is strictly linear and EXEC-free (the p15_ext_* procs in the
    // same file are covered separately by P15ExtScopeIdentityFramesFidelityTests), so
    // Into is trajectory-identical to Over here; asserted (runs the full pipeline and
    // can genuinely fail), not assumed.
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
    // to the pass-1 method above.
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

    // GO-split loader: the file gained M7 extension procs (p15_ext_*) after the existing
    // proc (kept byte-identical) — CREATE PROCEDURE must be alone in its batch.
    private static async Task DeployFixtureAsync(string connectionString)
    {
        var sqlPath = Path.Combine(AppContext.BaseDirectory, "corpus", "p15_scope_identity_rowcount_chains.sql");
        var script = await File.ReadAllTextAsync(sqlPath);
        var batches = System.Text.RegularExpressions.Regex.Split(script, @"^\s*GO\s*$",
            System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        foreach (var batch in batches)
        {
            if (string.IsNullOrWhiteSpace(batch))
            {
                continue;
            }

            await using var command = connection.CreateCommand();
            command.CommandText = batch;
            await command.ExecuteNonQueryAsync();
        }
    }

    private static async Task<ChainResult> RunNativeAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var tran = (SqlTransaction)await connection.BeginTransactionAsync();

        await using var command = connection.CreateCommand();
        command.Transaction = tran;
        command.CommandText = "EXEC dbo.p15_scope_identity_rowcount_chains @Seed = 100;";
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        var result = new ChainResult(
            reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4));
        await reader.DisposeAsync();

        await tran.RollbackAsync();
        return result;
    }

    private static async Task<ChainResult> RunThroughDebuggerAsync(
        string server, string database, StepKind stepKind = StepKind.Over, bool boost = false)
    {
        var target = new TargetEntry(server, "test", AllowWrites: false, Options: null);
        var options = new SessionOptions(
            server,
            database,
            LaunchMode.Procedure,
            "dbo.p15_scope_identity_rowcount_chains",
            new Dictionary<string, string> { ["@Seed"] = "100" },
            ScriptText: null,
            Boost: boost);

        var result = await SessionHost.RunAsync(options, target, stepKind: stepKind);

        var resultSet = Assert.Single(result.Execution.ResultSets);
        var row = Assert.Single(resultSet.Rows);
        return new ChainResult(
            (int)row[0]!, (int)row[1]!, (int)row[2]!, (int)row[3]!, (int)row[4]!);
    }
}
