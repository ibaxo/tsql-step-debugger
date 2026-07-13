using Microsoft.Data.SqlClient;
using TsqlDbg.Core.Sessions;
using TsqlDbg.Core.Targets;
using Xunit;

namespace TsqlDbg.Integration;

// DESIGN §20.4 corpus fixture p20_deferred_resolution + §22 M3 accept criterion.
// §20.3.4 expected-failure comparison mode: unlike every other fidelity fixture, BOTH
// sides are expected to FAIL (native: SqlException 208 at batch-compile time; debugger:
// SessionFaultException wrapping FrameFaulted, same number) — the assertion is that
// they fail the SAME way, not that they produce equal ResultSets.
public sealed class P20DeferredResolutionFidelityTests
{
    private const string ConnEnvVar = "TSQLDBG_TEST_CONN";

    [SkippableFact]
    public async Task DebuggerRun_MatchesNativeRun_BothFailWithError208()
    {
        var rawConnectionString = Environment.GetEnvironmentVariable(ConnEnvVar);
        Skip.If(
            string.IsNullOrWhiteSpace(rawConnectionString),
            $"{ConnEnvVar} is not set; skipping fidelity test (never fake a pass — CLAUDE.md).");

        var csb = new SqlConnectionStringBuilder(rawConnectionString);
        var server = csb.DataSource;
        var database = csb.InitialCatalog;

        await DeployFixtureAsync(rawConnectionString!);

        var nativeEx = await Assert.ThrowsAsync<SqlException>(() => RunNativeAsync(rawConnectionString!));
        Assert.Equal(208, nativeEx.Number);

        var debuggerEx = await Assert.ThrowsAsync<SessionFaultException>(() => RunThroughDebuggerAsync(server, database));
        Assert.Contains("208", debuggerEx.Message);
    }

    // M7 matrix cell (design note §6.1/§6.2): mechanical pass-2 addition -- this
    // fixture is EXEC-free, so Into is trajectory-identical to Over; asserted (both
    // sides fail the same way, the §20.3.4 expected-failure comparison), not assumed.
    // The 208 is a compile-time deferred-resolution fault on frame 0's very first
    // statement, so step granularity never matters here — but the cell runs the full
    // pipeline and can genuinely fail.
    [SkippableFact]
    public async Task DebuggerRun_MatchesNativeRun_SteppedInto_BothFailWithError208()
    {
        var rawConnectionString = Environment.GetEnvironmentVariable(ConnEnvVar);
        Skip.If(
            string.IsNullOrWhiteSpace(rawConnectionString),
            $"{ConnEnvVar} is not set; skipping fidelity test (never fake a pass — CLAUDE.md).");

        var csb = new SqlConnectionStringBuilder(rawConnectionString);
        var server = csb.DataSource;
        var database = csb.InitialCatalog;

        await DeployFixtureAsync(rawConnectionString!);

        var nativeEx = await Assert.ThrowsAsync<SqlException>(() => RunNativeAsync(rawConnectionString!));
        Assert.Equal(208, nativeEx.Number);

        var debuggerEx = await Assert.ThrowsAsync<SessionFaultException>(
            () => RunThroughDebuggerAsync(server, database, StepKind.Into));
        Assert.Contains("208", debuggerEx.Message);
    }

    // M6 item 6 (B9): fidelity pass 3 — continue with boost on. Assertions identical
    // to the pass-1 method above (both sides fail the same way; boost never reaches
    // the fault — it is a compile-time 208 on frame 0's very first statement).
    [SkippableFact]
    public async Task DebuggerRun_MatchesNativeRun_ContinueBoost_BothFailWithError208()
    {
        var rawConnectionString = Environment.GetEnvironmentVariable(ConnEnvVar);
        Skip.If(
            string.IsNullOrWhiteSpace(rawConnectionString),
            $"{ConnEnvVar} is not set; skipping fidelity test (never fake a pass — CLAUDE.md).");

        var csb = new SqlConnectionStringBuilder(rawConnectionString);
        var server = csb.DataSource;
        var database = csb.InitialCatalog;

        await DeployFixtureAsync(rawConnectionString!);

        var nativeEx = await Assert.ThrowsAsync<SqlException>(() => RunNativeAsync(rawConnectionString!));
        Assert.Equal(208, nativeEx.Number);

        var debuggerEx = await Assert.ThrowsAsync<SessionFaultException>(
            () => RunThroughDebuggerAsync(server, database, boost: true));
        Assert.Contains("208", debuggerEx.Message);
    }

    private static async Task DeployFixtureAsync(string connectionString)
    {
        var sqlPath = Path.Combine(AppContext.BaseDirectory, "corpus", "p20_deferred_resolution.sql");
        var script = await File.ReadAllTextAsync(sqlPath);

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = script;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task RunNativeAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var tran = (SqlTransaction)await connection.BeginTransactionAsync();

        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = tran;
            command.CommandText = "EXEC dbo.p20_deferred_resolution @Mode = 0;";
            await using var reader = await command.ExecuteReaderAsync();
            await reader.ReadAsync();
        }
        finally
        {
            await tran.RollbackAsync();
        }
    }

    private static async Task RunThroughDebuggerAsync(
        string server, string database, StepKind stepKind = StepKind.Over, bool boost = false)
    {
        var target = new TargetEntry(server, "test", AllowWrites: false, Options: null);
        var options = new SessionOptions(
            server,
            database,
            LaunchMode.Procedure,
            "dbo.p20_deferred_resolution",
            new Dictionary<string, string> { ["@Mode"] = "0" },
            ScriptText: null,
            Boost: boost);

        await SessionHost.RunAsync(options, target, stepKind: stepKind);
    }
}
