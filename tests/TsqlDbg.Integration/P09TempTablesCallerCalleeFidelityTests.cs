using Microsoft.Data.SqlClient;
using TsqlDbg.Core.Sessions;
using TsqlDbg.Core.Targets;
using Xunit;

namespace TsqlDbg.Integration;

// DESIGN §20.4 corpus fixture p09_temp_tables_caller_callee + §22 M4 accept
// criterion. See the fixture's own header comment for the R2 cross-frame temp-table
// visibility shape it exercises.
public sealed class P09TempTablesCallerCalleeFidelityTests
{
    private const string ConnEnvVar = "TSQLDBG_TEST_CONN";

    private sealed record TwoSets(int CalleeSum, int FinalSum);

    [SkippableFact]
    public async Task DebuggerRun_MatchesNativeRun_ForTempTablesCallerCallee()
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

    // A20's acceptance test (ratified 2026-07-06 — docs/archive/reviews/
    // m5-a20-r2-collision-rename-fable.md): with R2 renaming only on collision, the
    // caller's #p09_shared keeps its ORIGINAL physical name, so the stepped-over
    // callee's compiled body reads and writes it natively — the divergence this test
    // pinned while A20 was pending (208 on #p09_shared__f0, missing CalleeSum set,
    // FinalSum 6 vs 36) is gone, and the plain fidelity assertion holds. This flip
    // FROM the pinning probe TO native==debugged is the ratified change itself, per
    // the probe's own header note — not a weakening.
    [SkippableFact]
    public async Task DebuggerRun_MatchesNativeRun_SteppedOver_D5()
    {
        var rawConnectionString = Environment.GetEnvironmentVariable(ConnEnvVar);
        Skip.If(
            string.IsNullOrWhiteSpace(rawConnectionString),
            $"{ConnEnvVar} is not set; skipping fidelity test (never fake a pass — CLAUDE.md).");

        var csb = new SqlConnectionStringBuilder(rawConnectionString);
        await DeployFixtureAsync(rawConnectionString!);

        var native = await RunNativeAsync(rawConnectionString!);
        Assert.Equal(new TwoSets(36, 36), native);         // callee's +10 write lands natively

        var debugged = await RunThroughDebuggerAsync(csb.DataSource, csb.InitialCatalog, StepKind.Over);

        Assert.Equal(native, debugged);
    }

    // M6 item 6 (B9): fidelity pass 3 — continue with boost on. Assertions identical
    // to the pass-1 method (SteppedOver_D5) above.
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
        Assert.Equal(new TwoSets(36, 36), native);

        var debugged = await RunThroughDebuggerAsync(csb.DataSource, csb.InitialCatalog, StepKind.Over, boost: true);

        Assert.Equal(native, debugged);
    }

    private static async Task DeployFixtureAsync(string connectionString)
    {
        var sqlPath = Path.Combine(AppContext.BaseDirectory, "corpus", "p09_temp_tables_caller_callee.sql");
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

    // Two result sets stream: the callee's own "CalleeSum" SELECT, then the caller's
    // "FinalSum" SELECT — EXEC forwards a callee's result sets to the client exactly
    // like any nested call, native or debugged.
    private static async Task<TwoSets> RunNativeAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var tran = (SqlTransaction)await connection.BeginTransactionAsync();

        await using var command = connection.CreateCommand();
        command.Transaction = tran;
        command.CommandText = "EXEC dbo.p09_temp_tables_caller_callee @Seed = 1;";
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        var calleeSum = reader.GetInt32(0);
        await reader.NextResultAsync();
        await reader.ReadAsync();
        var finalSum = reader.GetInt32(0);
        await reader.DisposeAsync();

        await tran.RollbackAsync();
        return new TwoSets(calleeSum, finalSum);
    }

    private static async Task<TwoSets> RunThroughDebuggerAsync(
        string server, string database, StepKind stepKind, bool boost = false)
    {
        var target = new TargetEntry(server, "test", AllowWrites: false, Options: null);
        var options = new SessionOptions(
            server,
            database,
            LaunchMode.Procedure,
            "dbo.p09_temp_tables_caller_callee",
            new Dictionary<string, string> { ["@Seed"] = "1" },
            ScriptText: null,
            Boost: boost);

        var result = await SessionHost.RunAsync(options, target, stepKind: stepKind);

        Assert.Equal(2, result.Execution.ResultSets.Count);
        var calleeSum = (int)result.Execution.ResultSets[0].Rows[0][0]!;
        var finalSum = (int)result.Execution.ResultSets[1].Rows[0][0]!;
        return new TwoSets(calleeSum, finalSum);
    }
}
