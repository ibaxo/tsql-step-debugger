using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.SqlClient;
using TsqlDbg.Core.Sessions;
using TsqlDbg.Core.Targets;
using Xunit;

namespace TsqlDbg.Integration;

// DESIGN §20.4 corpus fixture p24_post_rollback_write_skew — the M4-exit C24
// obligation (docs/archive/reviews/m3-gate-review-fable.md §3, re-ruled in
// docs/archive/reviews/m4-c23-doom-temp-severity-fable.md §5.3 after C23's own fixture
// obligation turned out unfulfillable). Second *.manifest.json in the corpus,
// exercising C24 as a pure, field-exemptable value skew.
public sealed class P24PostRollbackWriteSkewFidelityTests
{
    private const string ConnEnvVar = "TSQLDBG_TEST_CONN";

    private sealed record SkewResult(int? CaughtNumber, int TrancountAfterWrite);

    private sealed record Manifest(
        [property: JsonPropertyName("fixture")] string Fixture,
        [property: JsonPropertyName("exemptions")] List<Exemption> Exemptions);

    private sealed record Exemption(
        [property: JsonPropertyName("field")] string Field,
        [property: JsonPropertyName("caveatId")] string CaveatId,
        [property: JsonPropertyName("reason")] string Reason);

    [SkippableFact]
    public async Task DebuggerRun_MatchesNativeRun_ForPostRollbackWriteSkew_ExceptC24ExemptedField()
    {
        var rawConnectionString = Environment.GetEnvironmentVariable(ConnEnvVar);
        Skip.If(
            string.IsNullOrWhiteSpace(rawConnectionString),
            $"{ConnEnvVar} is not set; skipping fidelity test (never fake a pass — CLAUDE.md).");

        // §20.3.1 point 6: "no caveat id -> no exemption" — load and assert the
        // citation rather than trusting the fixture's own comment.
        var manifest = await LoadManifestAsync();
        var exemption = Assert.Single(manifest.Exemptions);
        Assert.Equal("TrancountAfterWrite", exemption.Field);
        Assert.Equal("C24", exemption.CaveatId);

        var csb = new SqlConnectionStringBuilder(rawConnectionString);
        var server = csb.DataSource;
        var database = csb.InitialCatalog;

        await DeployFixtureAsync(rawConnectionString!);

        var native = await RunNativeAsync(rawConnectionString!);
        var debugged = await RunThroughDebuggerAsync(server, database);

        // Not exempted: must match exactly.
        Assert.Equal(native.CaughtNumber, debugged.CaughtNumber);
        Assert.Equal(8134, native.CaughtNumber);

        // Exempted (manifest cites C24 above): assert the KNOWN divergent values
        // explicitly rather than silently skip the comparison.
        Assert.Equal(0, native.TrancountAfterWrite);
        Assert.Equal(1, debugged.TrancountAfterWrite);
    }

    // M7 matrix cell (design note §6.1/§6.2): mechanical pass-2 addition -- this
    // fixture is EXEC-free, so Into is trajectory-identical to Over; asserted (runs
    // the full pipeline and can genuinely fail), not assumed. The C24 exemption
    // applies on every pass (§6.2: "p24/C24 all passes") -- loaded and asserted here
    // too rather than silently skipping the divergent field.
    [SkippableFact]
    public async Task DebuggerRun_MatchesNativeRun_SteppedInto_ExceptC24ExemptedField()
    {
        var rawConnectionString = Environment.GetEnvironmentVariable(ConnEnvVar);
        Skip.If(
            string.IsNullOrWhiteSpace(rawConnectionString),
            $"{ConnEnvVar} is not set; skipping fidelity test (never fake a pass — CLAUDE.md).");

        var manifest = await LoadManifestAsync();
        var exemption = Assert.Single(manifest.Exemptions);
        Assert.Equal("TrancountAfterWrite", exemption.Field);
        Assert.Equal("C24", exemption.CaveatId);

        var csb = new SqlConnectionStringBuilder(rawConnectionString);
        var server = csb.DataSource;
        var database = csb.InitialCatalog;

        await DeployFixtureAsync(rawConnectionString!);

        var native = await RunNativeAsync(rawConnectionString!);
        var debugged = await RunThroughDebuggerAsync(server, database, StepKind.Into);

        Assert.Equal(native.CaughtNumber, debugged.CaughtNumber);
        Assert.Equal(8134, native.CaughtNumber);
        Assert.Equal(0, native.TrancountAfterWrite);
        Assert.Equal(1, debugged.TrancountAfterWrite);
    }

    // M6 item 6 (B9): fidelity pass 3 — continue with boost on. Assertions identical
    // to the pass-1 method above (manifest exemptions and all — §20.3.1.6 unchanged,
    // by B2's refusal-equivalence: a doomed session refuses boost outright).
    [SkippableFact]
    public async Task DebuggerRun_MatchesNativeRun_ContinueBoost()
    {
        var rawConnectionString = Environment.GetEnvironmentVariable(ConnEnvVar);
        Skip.If(
            string.IsNullOrWhiteSpace(rawConnectionString),
            $"{ConnEnvVar} is not set; skipping fidelity test (never fake a pass — CLAUDE.md).");

        var manifest = await LoadManifestAsync();
        var exemption = Assert.Single(manifest.Exemptions);
        Assert.Equal("TrancountAfterWrite", exemption.Field);
        Assert.Equal("C24", exemption.CaveatId);

        var csb = new SqlConnectionStringBuilder(rawConnectionString);
        var server = csb.DataSource;
        var database = csb.InitialCatalog;

        await DeployFixtureAsync(rawConnectionString!);

        var native = await RunNativeAsync(rawConnectionString!);
        var debugged = await RunThroughDebuggerAsync(server, database, boost: true);

        Assert.Equal(native.CaughtNumber, debugged.CaughtNumber);
        Assert.Equal(8134, native.CaughtNumber);
        Assert.Equal(0, native.TrancountAfterWrite);
        Assert.Equal(1, debugged.TrancountAfterWrite);
    }

    private static async Task<Manifest> LoadManifestAsync()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "corpus", "p24.manifest.json");
        var json = await File.ReadAllTextAsync(path);
        return JsonSerializer.Deserialize<Manifest>(json)
            ?? throw new InvalidOperationException("p24.manifest.json failed to deserialize.");
    }

    private static async Task DeployFixtureAsync(string connectionString)
    {
        var sqlPath = Path.Combine(AppContext.BaseDirectory, "corpus", "p24_post_rollback_write_skew.sql");
        var script = await File.ReadAllTextAsync(sqlPath);

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = script;
        await command.ExecuteNonQueryAsync();
    }

    // Native run per the p05 pattern: the debuggee's own ROLLBACK needs a REAL,
    // explicit ambient transaction to cross (raw BEGIN TRANSACTION text, not an
    // ADO.NET SqlTransaction object — a mid-batch T-SQL ROLLBACK would desync
    // SqlTransaction's own client-side bookkeeping from the server's real trancount).
    private static async Task<SkewResult> RunNativeAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        await using (var beginCommand = connection.CreateCommand())
        {
            beginCommand.CommandText = "BEGIN TRANSACTION;";
            await beginCommand.ExecuteNonQueryAsync();
        }

        SkewResult result;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "EXEC dbo.p24_post_rollback_write_skew @Seed = 1;";
            await using var reader = await command.ExecuteReaderAsync();
            await reader.ReadAsync();
            result = new SkewResult(
                reader.IsDBNull(0) ? null : reader.GetInt32(0),
                reader.GetInt32(1));
        }

        await using (var cleanupCommand = connection.CreateCommand())
        {
            cleanupCommand.CommandText = "IF @@TRANCOUNT > 0 ROLLBACK;";
            await cleanupCommand.ExecuteNonQueryAsync();
        }

        return result;
    }

    private static async Task<SkewResult> RunThroughDebuggerAsync(
        string server, string database, StepKind stepKind = StepKind.Over, bool boost = false)
    {
        var target = new TargetEntry(server, "test", AllowWrites: false, Options: null);
        var options = new SessionOptions(
            server,
            database,
            LaunchMode.Procedure,
            "dbo.p24_post_rollback_write_skew",
            new Dictionary<string, string> { ["@Seed"] = "1" },
            ScriptText: null,
            Boost: boost);

        var result = await SessionHost.RunAsync(options, target, stepKind: stepKind);

        var resultSet = Assert.Single(result.Execution.ResultSets);
        var row = Assert.Single(resultSet.Rows);
        return new SkewResult((int?)row[0], (int)row[1]!);
    }
}
