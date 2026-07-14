using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.SqlClient;
using TsqlDbg.Core.Sessions;
using TsqlDbg.Core.Targets;
using TsqlDbg.Core.Tracing;
using Xunit;

namespace TsqlDbg.Integration;

// DESIGN §20.4 corpus fixture p22_error_context_dynsql: C21 through dynamic SQL.
//
// A58 (§11.6) CHANGED THIS FIXTURE'S VERDICT, and the change is the point. Before A58, C10 made
// step-into refuse dynamic SQL, so EVERY pass stepped over the EXEC(@s) and re-materialized the
// active error context (§10.7) around it -- and because re-materialization raises a NEW error,
// the dynamic child's ERROR_NUMBER() read RAISERROR's own 50000 instead of the real 8134. The
// manifest's own words were "C10 means there is no exact pass for this fixture at all".
//
// C21's prescribed remedy has always been "step INTO the callee for exact per-statement values
// (§10.7/R7)" -- it was simply unavailable for dynamic SQL. A58 makes it available: the Into pass
// now PUSHES a dynamic frame, R7 substitution reaches the ERROR_*() references inside the dynamic
// text, and the shadow serves the TRUE 8134. So this fixture now has an EXACT pass, exactly like
// its procedure-callee twin p21 -- and the C21 exemption narrows to the over/boost passes, which
// still step over and still re-materialize. That is a caveat retiring on a path, not a
// regression: the debugger got strictly more faithful, so the assertion got strictly stronger.
//
// Still the corpus-level twin of Fact7RematerializationLiveTests on the over/boost passes,
// proving the §10.7 shell reaches dynamic children.
public sealed class P22ErrorContextDynsqlFidelityTests
{
    private const string ConnEnvVar = "TSQLDBG_TEST_CONN";

    private sealed record DynsqlResult(string? Msg, int? Num);

    private sealed record Manifest(
        [property: JsonPropertyName("fixture")] string Fixture,
        [property: JsonPropertyName("exemptions")] List<Exemption> Exemptions);

    private sealed record Exemption(
        [property: JsonPropertyName("field")] string Field,
        [property: JsonPropertyName("caveatId")] string CaveatId,
        [property: JsonPropertyName("reason")] string Reason,
        [property: JsonPropertyName("passes")] List<string>? Passes);

    [SkippableFact]
    public async Task DebuggerRun_MatchesNativeRun_ForErrorContextDynsql_ExceptC21ExemptedField()
    {
        var rawConnectionString = Environment.GetEnvironmentVariable(ConnEnvVar);
        Skip.If(
            string.IsNullOrWhiteSpace(rawConnectionString),
            $"{ConnEnvVar} is not set; skipping fidelity test (never fake a pass — CLAUDE.md).");

        await AssertExemptionAsync();

        var csb = new SqlConnectionStringBuilder(rawConnectionString);
        var server = csb.DataSource;
        var database = csb.InitialCatalog;

        await DeployFixtureAsync(rawConnectionString!);

        var native = await RunNativeAsync(rawConnectionString!);
        var debugged = await RunThroughDebuggerAsync(server, database, StepKind.Over);

        AssertFaithfulAndExempted(native, debugged);
    }

    // Into pass (A58, §11.6): EXACT, zero exemptions -- the same shape p21 has had all along for a
    // procedure callee. The EXEC(@s) now pushes a DYNAMIC FRAME, so R7 substitution reaches the
    // ERROR_*() references inside the dynamic text and reads the real error context rather than
    // the §10.7 re-materialization wrapper's. This is the assertion that would have caught a
    // regression back to step-over: it demands the TRUE 8134, which only a pushed frame produces.
    [SkippableFact]
    public async Task DebuggerRun_MatchesNativeRun_SteppedInto_Exact()
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

        Assert.Equal(native, debugged);                              // zero exemptions
        Assert.Equal(8134, native.Num);                              // the REAL divide-by-zero, not RAISERROR's 50000
        Assert.Equal("Divide by zero error encountered.", native.Msg);
    }

    // Pass 3 (§6.2 MANDATORY non-hollow declaration): no IF/WHILE anywhere in this
    // fixture (TRY/CATCH + dynamic EXEC only) -- boost never even attempts a plan.
    // Asserted via a recording trace, not just a comment.
    private sealed class RecordingSink : ITraceSink
    {
        public List<(string Category, string Message)> Events { get; } = new();
        public void Event(string category, string message) => Events.Add((category, message));
    }

    [SkippableFact]
    public async Task DebuggerRun_MatchesNativeRun_ContinueBoost()
    {
        var rawConnectionString = Environment.GetEnvironmentVariable(ConnEnvVar);
        Skip.If(
            string.IsNullOrWhiteSpace(rawConnectionString),
            $"{ConnEnvVar} is not set; skipping fidelity test (never fake a pass — CLAUDE.md).");

        await AssertExemptionAsync();

        var csb = new SqlConnectionStringBuilder(rawConnectionString);
        var server = csb.DataSource;
        var database = csb.InitialCatalog;

        await DeployFixtureAsync(rawConnectionString!);

        var native = await RunNativeAsync(rawConnectionString!);
        var trace = new RecordingSink();
        var debugged = await RunThroughDebuggerAsync(server, database, StepKind.Over, boost: true, trace: trace);

        AssertFaithfulAndExempted(native, debugged);
        Assert.DoesNotContain(trace.Events, e => e.Category.StartsWith("boost."));
    }

    // The OVER/BOOST passes only: they step over the EXEC(@s), so §10.7 re-materializes the error
    // context around it and the dynamic child reads RAISERROR's 50000 (C21). The Into pass is
    // exact and asserts equality instead — see DebuggerRun_MatchesNativeRun_SteppedInto_Exact.
    private static void AssertFaithfulAndExempted(DynsqlResult native, DynsqlResult debugged)
    {
        // Faithful half (§10.7's whole point -- the fact-7 fix): the message matches.
        Assert.Equal(native.Msg, debugged.Msg);
        Assert.Equal("Divide by zero error encountered.", native.Msg);

        // Exempted (C21, over/boost) -- known divergent values, asserted explicitly
        // rather than silently skipped (the p10/p24 non-hollow pattern).
        Assert.Equal(8134, native.Num);
        Assert.Equal(50000, debugged.Num);
    }

    private static async Task AssertExemptionAsync()
    {
        var manifest = await LoadManifestAsync();
        var exemption = Assert.Single(manifest.Exemptions);
        Assert.Equal("Num", exemption.Field);
        Assert.Equal("C21", exemption.CaveatId);
        // A58: the exemption is now pass-scoped — the Into pass is exact (§11.6), so it must NOT
        // be exempt. Pinning the array here is what stops the exemption from silently widening
        // back to "all passes" and re-hiding a step-over regression.
        Assert.Equal(new[] { "over", "boost" }, exemption.Passes);
    }

    private static async Task<Manifest> LoadManifestAsync()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "corpus", "p22.manifest.json");
        var json = await File.ReadAllTextAsync(path);
        return JsonSerializer.Deserialize<Manifest>(json)
            ?? throw new InvalidOperationException("p22.manifest.json failed to deserialize.");
    }

    private static async Task DeployFixtureAsync(string connectionString)
    {
        var sqlPath = Path.Combine(AppContext.BaseDirectory, "corpus", "p22_error_context_dynsql.sql");
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

    private static async Task<DynsqlResult> RunNativeAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var tran = (SqlTransaction)await connection.BeginTransactionAsync();

        await using var command = connection.CreateCommand();
        command.Transaction = tran;
        command.CommandText = "EXEC dbo.p22_error_context_dynsql;";
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        var result = new DynsqlResult(
            reader.IsDBNull(0) ? null : reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetInt32(1));
        await reader.DisposeAsync();

        await tran.RollbackAsync();
        return result;
    }

    private static async Task<DynsqlResult> RunThroughDebuggerAsync(
        string server, string database, StepKind stepKind, bool boost = false, ITraceSink? trace = null)
    {
        var target = new TargetEntry(server, "test", AllowWrites: false, Options: null);
        var options = new SessionOptions(
            server,
            database,
            LaunchMode.Procedure,
            "dbo.p22_error_context_dynsql",
            new Dictionary<string, string>(),
            ScriptText: null,
            Boost: boost);

        var result = await SessionHost.RunAsync(options, target, trace, stepKind: stepKind);

        var resultSet = Assert.Single(result.Execution.ResultSets);
        var row = Assert.Single(resultSet.Rows);
        return new DynsqlResult((string?)row[0], (int?)row[1]);
    }
}
