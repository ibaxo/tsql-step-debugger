using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.SqlClient;
using TsqlDbg.Core.Sessions;
using TsqlDbg.Core.Targets;
using TsqlDbg.Core.Tracing;
using Xunit;

namespace TsqlDbg.Integration;

// DESIGN §20.4 corpus fixture p22_error_context_dynsql + M7 D3 §3.3 (docs/archive/reviews/
// m7-hardening-design-notes-fable.md): C21 through dynamic SQL -- NO exact pass
// exists for this fixture. C10 (dynamic SQL is an atomic black box) makes step-into
// refuse and fall back to step-over, so every pass re-materializes the active error
// context (§10.7) around the EXEC(@s) -- exempt Num (C21) on all three passes; Msg
// stays faithful everywhere. Also the corpus-level twin of
// Fact7RematerializationLiveTests, proving the §10.7 shell reaches dynamic children.
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
        [property: JsonPropertyName("reason")] string Reason);

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

    // step-into refuses ALL dynamic SQL (C10) -- trajectory-identical to the Over pass.
    [SkippableFact]
    public async Task DebuggerRun_MatchesNativeRun_SteppedInto()
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
        var debugged = await RunThroughDebuggerAsync(server, database, StepKind.Into);

        AssertFaithfulAndExempted(native, debugged);
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

    private static void AssertFaithfulAndExempted(DynsqlResult native, DynsqlResult debugged)
    {
        // Faithful half (§10.7's whole point -- the fact-7 fix): the message matches.
        Assert.Equal(native.Msg, debugged.Msg);
        Assert.Equal("Divide by zero error encountered.", native.Msg);

        // Exempted (C21, ALL passes) -- known divergent values, asserted explicitly
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
