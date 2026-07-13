using Microsoft.Data.SqlClient;
using TsqlDbg.Core.Sessions;
using TsqlDbg.Core.Targets;
using TsqlDbg.Core.Tracing;
using Xunit;

namespace TsqlDbg.Integration;

// DESIGN §20.4 corpus fixture p23_missing_temp_uncatchable + M7 D3 §3.4 (docs/archive/reviews/
// m7-hardening-design-notes-fable.md): §10.1 fact-1b addendum -- caller-CATCH
// propagation of a callee's deferred-resolution error. See the fixture's own header
// comment for the same-scope-uncatchable/enclosing-scope-catchable shape it exercises
// (facts 1b/6/23-F) and the D2 schema-qualified err_procedure it load-bears on. Per
// the design note: "the sharpest end-to-end pin of the §10.1 oracle machinery in the
// corpus" -- every pass is EXACT, no exemptions anywhere.
public sealed class P23MissingTempUncatchableFidelityTests
{
    private const string ConnEnvVar = "TSQLDBG_TEST_CONN";

    private sealed record LogResult(int Result, int? ErrNumber, string? ErrProcedure, string? ErrMessage);

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
        Assert.Equal(new LogResult(208, 208, "dbo.p23_inner", native.ErrMessage), native);
    }

    // Into pass: EXACT, zero exemptions (design note §3.2/§3.4) -- §10.1 propagate ->
    // sameScopeUncatchable -> abnormal pop -> caller CATCH; err_procedure is
    // schema-qualified via D2 (pre-authorized: dbo.p23_inner, fact 31c) since the
    // origin frame (the pushed p23_inner callee) is a module frame with a raw-NULL
    // engine value.
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

    // Pass 3 (§6.2 MANDATORY non-hollow declaration): no IF/WHILE anywhere in either
    // proc (TRY/CATCH + EXEC only), so boost never attempts a plan on any arrival --
    // equals the Over pass by pure interpreted fallback. Asserted via a recording
    // trace, not just a comment.
    private sealed class RecordingSink : ITraceSink
    {
        public List<(string Category, string Message)> Events { get; } = new();
        public void Event(string category, string message) => Events.Add((category, message));
    }

    [SkippableFact]
    public async Task DebuggerRun_MatchesNativeRun_ContinueBoost_EqualsOver()
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
        var sqlPath = Path.Combine(AppContext.BaseDirectory, "corpus", "p23_missing_temp_uncatchable.sql");
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

    private static async Task<LogResult> RunNativeAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var tran = (SqlTransaction)await connection.BeginTransactionAsync();

        await using var command = connection.CreateCommand();
        command.Transaction = tran;
        command.CommandText = "DECLARE @R int = 0; EXEC dbo.p23_missing_temp_uncatchable @Result = @R OUTPUT;";
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        var result = new LogResult(
            reader.GetInt32(0),
            reader.IsDBNull(1) ? null : reader.GetInt32(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3));
        await reader.DisposeAsync();

        await tran.RollbackAsync();
        return result;
    }

    private static async Task<LogResult> RunThroughDebuggerAsync(
        string server, string database, StepKind stepKind, bool boost = false, ITraceSink? trace = null)
    {
        var target = new TargetEntry(server, "test", AllowWrites: false, Options: null);
        var options = new SessionOptions(
            server,
            database,
            LaunchMode.Procedure,
            "dbo.p23_missing_temp_uncatchable",
            new Dictionary<string, string> { ["@Result"] = "0" },
            ScriptText: null,
            Boost: boost);

        var result = await SessionHost.RunAsync(options, target, trace, stepKind: stepKind);

        var resultSet = Assert.Single(result.Execution.ResultSets);
        var row = Assert.Single(resultSet.Rows);
        return new LogResult((int)row[0]!, (int?)row[1], (string?)row[2], (string?)row[3]);
    }
}
