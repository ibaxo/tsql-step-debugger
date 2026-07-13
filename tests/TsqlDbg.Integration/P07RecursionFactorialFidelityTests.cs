using Microsoft.Data.SqlClient;
using TsqlDbg.Core.Sessions;
using TsqlDbg.Core.Targets;
using TsqlDbg.Core.Tracing;
using Xunit;

namespace TsqlDbg.Integration;

// DESIGN §20.4 corpus fixture p07_recursion_factorial + §22 M4 accept criterion.
// See the fixture's own header comment for the recursion/frame-ordinal shapes it
// exercises.
public sealed class P07RecursionFactorialFidelityTests
{
    private const string ConnEnvVar = "TSQLDBG_TEST_CONN";

    private sealed class RecordingSink : ITraceSink
    {
        public List<(string Category, string Message)> Events { get; } = new();
        public void Event(string category, string message) => Events.Add((category, message));
    }

    // M7 matrix cell (design note §6.1): the MEANINGFUL pass-1 (Over) addition. This
    // fixture's ONLY non-boost pass was Into; a stepped-OVER call runs the WHOLE
    // recursion natively as one absorbed D5 statement (§10.1/A13, fact 24 Group A) --
    // the outer EXEC never pushes a frame, so no per-frame push/pop/OUTPUT-copy-back
    // machinery runs at all, exercising a distinct code path from the Into pass. ANY
    // first-run diff here is a fidelity finding (design note §6.1), not a mechanical
    // cell.
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
        Assert.Equal(120L, native);   // 5! -- sanity on the native side too
    }

    [SkippableFact]
    public async Task DebuggerRun_MatchesNativeRun_ForRecursionFactorial()
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
        Assert.Equal(120L, native);   // 5! -- sanity on the native side too
    }

    // M6 item 6 (B9) + M7 §6.1 pass-3 normalization: fidelity pass 3 — continue with
    // boost on, now with the §20.3 pass-3 shape (continue = Over fallback, NOT the old
    // Into-hardcoded helper). §6.2 non-hollow boost declaration (MANDATORY): under
    // Over fallback the cursor never leaves frame 0 (the outer proc, whose body is
    // DECLARE/EXEC/SELECT with NO IF/WHILE), so boost is never even ATTEMPTED -- an
    // all-refusal-by-absence fixture. Asserted via a recording trace (no boost.fire,
    // indeed no boost.* at all), not just declared in a comment.
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
        var sqlPath = Path.Combine(AppContext.BaseDirectory, "corpus", "p07_recursion_factorial.sql");
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

    private static async Task<long> RunNativeAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var tran = (SqlTransaction)await connection.BeginTransactionAsync();

        await using var command = connection.CreateCommand();
        command.Transaction = tran;
        command.CommandText = "EXEC dbo.p07_recursion_factorial @N = 5;";
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        var result = reader.GetInt64(0);
        await reader.DisposeAsync();

        await tran.RollbackAsync();
        return result;
    }

    // stepKind selects the pass: Into (pass 2) pushes/pops a real frame per recursive
    // activation; Over (pass 1 / pass-3 continue fallback) runs the whole recursion
    // natively as one absorbed D5 statement.
    private static async Task<long> RunThroughDebuggerAsync(
        string server, string database, StepKind stepKind = StepKind.Over, bool boost = false, ITraceSink? trace = null)
    {
        var target = new TargetEntry(server, "test", AllowWrites: false, Options: null);
        var options = new SessionOptions(
            server,
            database,
            LaunchMode.Procedure,
            "dbo.p07_recursion_factorial",
            new Dictionary<string, string> { ["@N"] = "5" },
            ScriptText: null,
            Boost: boost);

        var result = await SessionHost.RunAsync(options, target, trace, stepKind: stepKind);

        var resultSet = Assert.Single(result.Execution.ResultSets);
        var row = Assert.Single(resultSet.Rows);
        return (long)row[0]!;
    }
}
