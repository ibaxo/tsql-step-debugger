using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.SqlClient;
using TsqlDbg.Core.Sessions;
using TsqlDbg.Core.Targets;
using TsqlDbg.Core.Tracing;
using Xunit;

namespace TsqlDbg.Integration;

// DESIGN §20.4 corpus fixture p21_error_context_callee + M7 D3 §3.2 (docs/archive/reviews/
// m7-hardening-design-notes-fable.md): C21's exact-vs-exempted split, in §20.4's own
// words. See the fixture's own header comment for the archetypal no-arg indirect
// ERROR_*() consumer (dbo.p21_logger) it exercises through §10.7 re-materialization,
// and for the D2 schema-qualified err_procedure synthesis this fixture load-bears on.
public sealed class P21ErrorContextCalleeFidelityTests
{
    private const string ConnEnvVar = "TSQLDBG_TEST_CONN";

    private sealed record LogResult(
        int Result, int? ErrNumber, int? ErrSeverity, int? ErrState, int? ErrLine,
        string? ErrProcedure, string? ErrMessage);

    private sealed record Manifest(
        [property: JsonPropertyName("fixture")] string Fixture,
        [property: JsonPropertyName("exemptions")] List<Exemption> Exemptions);

    private sealed record Exemption(
        [property: JsonPropertyName("field")] string Field,
        [property: JsonPropertyName("caveatId")] string CaveatId,
        [property: JsonPropertyName("reason")] string Reason,
        [property: JsonPropertyName("passes")] List<string>? Passes);

    [SkippableFact]
    public async Task DebuggerRun_MatchesNativeRun_SteppedOver_ExceptC21ExemptedFields()
    {
        var rawConnectionString = Environment.GetEnvironmentVariable(ConnEnvVar);
        Skip.If(
            string.IsNullOrWhiteSpace(rawConnectionString),
            $"{ConnEnvVar} is not set; skipping fidelity test (never fake a pass — CLAUDE.md).");

        await AssertExemptionsAsync();

        var csb = new SqlConnectionStringBuilder(rawConnectionString);
        var server = csb.DataSource;
        var database = csb.InitialCatalog;

        await DeployFixtureAsync(rawConnectionString!);

        var native = await RunNativeAsync(rawConnectionString!);
        var debugged = await RunThroughDebuggerAsync(server, database, StepKind.Over);

        AssertFaithfulTrioAndExemptions(native, debugged);
    }

    // Into pass: EXACT, zero exemptions (design note §3.2) -- R7 substitution reaches
    // p21_logger's direct ERROR_*() references inside the PUSHED logger frame (dynamic
    // extent, §10.2); err_procedure is schema-qualified via D2 (pre-authorized:
    // dbo.p21_error_context_callee, fact 31c).
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

        Assert.Equal(native, debugged);   // zero exemptions
        Assert.Equal(8134, native.ErrNumber);
        Assert.Equal("dbo.p21_error_context_callee", native.ErrProcedure);
    }

    // Pass 3 (§6.2 MANDATORY non-hollow declaration): no IF/WHILE anywhere in this
    // fixture (TRY/CATCH + EXEC/SET only) -- boost never even attempts a plan, so it
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

        await AssertExemptionsAsync();

        var csb = new SqlConnectionStringBuilder(rawConnectionString);
        var server = csb.DataSource;
        var database = csb.InitialCatalog;

        await DeployFixtureAsync(rawConnectionString!);

        var native = await RunNativeAsync(rawConnectionString!);
        var trace = new RecordingSink();
        var debugged = await RunThroughDebuggerAsync(server, database, StepKind.Over, boost: true, trace: trace);

        AssertFaithfulTrioAndExemptions(native, debugged);
        Assert.DoesNotContain(trace.Events, e => e.Category.StartsWith("boost."));
    }

    private static void AssertFaithfulTrioAndExemptions(LogResult native, LogResult debugged)
    {
        Assert.Equal(native.Result, debugged.Result);

        // C21's faithful trio -- exact on every pass.
        Assert.Equal(native.ErrSeverity, debugged.ErrSeverity);
        Assert.Equal(native.ErrState, debugged.ErrState);
        Assert.Equal(native.ErrMessage, debugged.ErrMessage);

        // Exempted trio (C21, over/boost only) -- known divergent values, asserted
        // explicitly rather than silently skipped (the p10/p24 non-hollow pattern).
        Assert.Equal(8134, native.ErrNumber);
        Assert.Equal(50000, debugged.ErrNumber);
        Assert.Equal("dbo.p21_error_context_callee", native.ErrProcedure);
        Assert.Null(debugged.ErrProcedure);
        // Both sides produce a genuine line number (the mechanism ran, not a missing/
        // error value) -- deliberately NOT pinned to specific integers, let alone an
        // inequality: native's line is this file's own physical layout (shifts if the
        // .sql is reformatted) and the wrapper's line is an internal composed-batch
        // template detail: the two happen to coincide for this fixture's exact layout
        // (both 9, empirically observed live) without that coincidence carrying any
        // fidelity meaning either way -- C21's text is about what ERROR_LINE() means
        // here (wrapper vs. original fault site), not a numeric-inequality guarantee.
        Assert.NotNull(native.ErrLine);
        Assert.NotNull(debugged.ErrLine);
    }

    private static async Task AssertExemptionsAsync()
    {
        var manifest = await LoadManifestAsync();
        Assert.Equal(3, manifest.Exemptions.Count);
        foreach (var field in new[] { "ErrNumber", "ErrLine", "ErrProcedure" })
        {
            var exemption = Assert.Single(manifest.Exemptions, e => e.Field == field);
            Assert.Equal("C21", exemption.CaveatId);
            Assert.Equal(new[] { "over", "boost" }, exemption.Passes);
        }
    }

    private static async Task<Manifest> LoadManifestAsync()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "corpus", "p21.manifest.json");
        var json = await File.ReadAllTextAsync(path);
        return JsonSerializer.Deserialize<Manifest>(json)
            ?? throw new InvalidOperationException("p21.manifest.json failed to deserialize.");
    }

    private static async Task DeployFixtureAsync(string connectionString)
    {
        var sqlPath = Path.Combine(AppContext.BaseDirectory, "corpus", "p21_error_context_callee.sql");
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
        command.CommandText = "DECLARE @R int = 0; EXEC dbo.p21_error_context_callee @Result = @R OUTPUT;";
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        var result = ReadRow(reader);
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
            "dbo.p21_error_context_callee",
            new Dictionary<string, string> { ["@Result"] = "0" },
            ScriptText: null,
            Boost: boost);

        var result = await SessionHost.RunAsync(options, target, trace, stepKind: stepKind);

        var resultSet = Assert.Single(result.Execution.ResultSets);
        var row = Assert.Single(resultSet.Rows);
        return new LogResult(
            (int)row[0]!,
            (int?)row[1],
            (int?)row[2],
            (int?)row[3],
            (int?)row[4],
            (string?)row[5],
            (string?)row[6]);
    }

    private static LogResult ReadRow(SqlDataReader reader) => new(
        reader.GetInt32(0),
        reader.IsDBNull(1) ? null : reader.GetInt32(1),
        reader.IsDBNull(2) ? null : reader.GetInt32(2),
        reader.IsDBNull(3) ? null : reader.GetInt32(3),
        reader.IsDBNull(4) ? null : reader.GetInt32(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.IsDBNull(6) ? null : reader.GetString(6));
}
