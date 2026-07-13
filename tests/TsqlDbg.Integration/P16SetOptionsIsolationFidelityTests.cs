using Microsoft.Data.SqlClient;
using TsqlDbg.Core.Sessions;
using TsqlDbg.Core.Targets;
using TsqlDbg.Core.Tracing;
using Xunit;

namespace TsqlDbg.Integration;

// DESIGN §20.4 corpus fixture p16_set_options_isolation + §22 M4 accept criterion.
// See the fixture's own header comment for the D6/fact-9 SET-option pop-restore shape
// it exercises.
public sealed class P16SetOptionsIsolationFidelityTests
{
    private const string ConnEnvVar = "TSQLDBG_TEST_CONN";

    private sealed record IsolationResult(int CalleeIsolation, int BeforeIsolation, int AfterIsolation);

    private sealed class RecordingSink : ITraceSink
    {
        public List<(string Category, string Message)> Events { get; } = new();
        public void Event(string category, string message) => Events.Add((category, message));
    }

    // M7 matrix cell (design note §6.1): the MEANINGFUL pass-1 (Over) addition. This
    // fixture's ONLY non-boost pass was Into; a stepped-OVER call runs the callee
    // (which does SET TRANSACTION ISOLATION LEVEL SERIALIZABLE) as one native
    // statement, so the ENGINE'S OWN proc-scoped SET-option revert at module exit
    // (fact 9) is what restores the caller's isolation -- NOT the debugger's push/pop
    // emit-restoring-SETs machinery (§11.2, exercised only on the Into pass). ANY
    // first-run diff here is a fidelity finding (design note §6.1), not mechanical.
    [SkippableFact]
    public async Task DebuggerRun_MatchesNativeRun_SteppedOver()
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
        var debugged = await RunThroughDebuggerAsync(server, database, StepKind.Over);

        Assert.Equal(native, debugged);
        Assert.Equal(4, native.CalleeIsolation);              // 4 = SERIALIZABLE, while the callee runs
        Assert.Equal(native.BeforeIsolation, native.AfterIsolation);   // fact 9: reverts at module exit
    }

    [SkippableFact]
    public async Task DebuggerRun_MatchesNativeRun_ForSetOptionsIsolation()
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
        Assert.Equal(4, native.CalleeIsolation);              // 4 = SERIALIZABLE, while the callee runs
        Assert.Equal(native.BeforeIsolation, native.AfterIsolation);   // fact 9: reverts at module exit
    }

    // M6 item 6 (B9) + M7 §6.1 pass-3 normalization: fidelity pass 3 — continue with
    // boost on, now with the §20.3 pass-3 shape (continue = Over fallback, NOT the old
    // Into-hardcoded helper). §6.2 non-hollow boost declaration (MANDATORY): under
    // Over fallback the cursor never leaves frame 0 (the outer proc, whose body is
    // DECLARE/EXEC/SELECT with NO IF/WHILE), so boost is never even ATTEMPTED -- an
    // all-refusal-by-absence fixture. Asserted via a recording trace (no boost.* at
    // all), not just declared in a comment.
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
        var trace = new RecordingSink();
        var debugged = await RunThroughDebuggerAsync(server, database, StepKind.Over, boost: true, trace: trace);

        Assert.Equal(native, debugged);
        Assert.DoesNotContain(trace.Events, e => e.Category.StartsWith("boost."));
    }

    private static async Task DeployFixtureAsync(string connectionString)
    {
        var sqlPath = Path.Combine(AppContext.BaseDirectory, "corpus", "p16_set_options_isolation.sql");
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

    private static async Task<IsolationResult> RunNativeAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var tran = (SqlTransaction)await connection.BeginTransactionAsync();

        await using var command = connection.CreateCommand();
        command.Transaction = tran;
        command.CommandText = "EXEC dbo.p16_set_options_isolation;";
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        var calleeIsolation = reader.GetInt16(0);
        await reader.NextResultAsync();
        await reader.ReadAsync();
        var result = new IsolationResult(calleeIsolation, reader.GetInt32(0), reader.GetInt32(1));
        await reader.DisposeAsync();

        await tran.RollbackAsync();
        return result;
    }

    private static async Task<IsolationResult> RunThroughDebuggerAsync(
        string server, string database, StepKind stepKind = StepKind.Over, bool boost = false, ITraceSink? trace = null)
    {
        var target = new TargetEntry(server, "test", AllowWrites: false, Options: null);
        var options = new SessionOptions(
            server,
            database,
            LaunchMode.Procedure,
            "dbo.p16_set_options_isolation",
            null,
            ScriptText: null,
            Boost: boost);

        var result = await SessionHost.RunAsync(options, target, trace, stepKind: stepKind);

        Assert.Equal(2, result.Execution.ResultSets.Count);
        var calleeIsolation = Convert.ToInt32(result.Execution.ResultSets[0].Rows[0][0]);
        var before = (int)result.Execution.ResultSets[1].Rows[0][0]!;
        var after = (int)result.Execution.ResultSets[1].Rows[0][1]!;
        return new IsolationResult(calleeIsolation, before, after);
    }
}
