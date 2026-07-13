using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.SqlClient;
using TsqlDbg.Core.Sessions;
using TsqlDbg.Core.Targets;
using Xunit;

namespace TsqlDbg.Integration;

// DESIGN §20.4 corpus fixture p10_table_variable + §22 M4 accept criterion. See the
// fixture's own header comment for the R1/C25 table-variable-vs-rollback divergence
// it exercises. First fixture in the corpus to use a *.manifest.json exemption
// (§20.3.1.6) — DESIGN's own "no caveat id -> no exemption" rule is enforced here by
// actually reading the manifest and asserting its citation, not skipping the field
// comparison silently.
public sealed class P10TableVariableFidelityTests
{
    private const string ConnEnvVar = "TSQLDBG_TEST_CONN";

    private sealed record TableVarResult(int BeforeRollback, int AfterRollback);

    private sealed record Manifest(
        [property: JsonPropertyName("fixture")] string Fixture,
        [property: JsonPropertyName("exemptions")] List<Exemption> Exemptions);

    private sealed record Exemption(
        [property: JsonPropertyName("field")] string Field,
        [property: JsonPropertyName("caveatId")] string CaveatId,
        [property: JsonPropertyName("reason")] string Reason);

    [SkippableFact]
    public async Task DebuggerRun_MatchesNativeRun_ForTableVariable_ExceptC25ExemptedField()
    {
        var rawConnectionString = Environment.GetEnvironmentVariable(ConnEnvVar);
        Skip.If(
            string.IsNullOrWhiteSpace(rawConnectionString),
            $"{ConnEnvVar} is not set; skipping fidelity test (never fake a pass — CLAUDE.md).");

        // §20.3.1 point 6: "a manifest may exclude a column/field from comparison for
        // a specific pass only by citing a §21 caveat id" — load it and assert the
        // citation is actually present rather than trusting the fixture's comment.
        var manifest = await LoadManifestAsync();
        var exemption = Assert.Single(manifest.Exemptions);
        Assert.Equal("AfterRollback", exemption.Field);
        Assert.Equal("C25", exemption.CaveatId);

        var csb = new SqlConnectionStringBuilder(rawConnectionString);
        var server = csb.DataSource;
        var database = csb.InitialCatalog;

        await DeployFixtureAsync(rawConnectionString!);

        var native = await RunNativeAsync(rawConnectionString!);
        var debugged = await RunThroughDebuggerAsync(server, database);

        // Not exempted: must match exactly.
        Assert.Equal(native.BeforeRollback, debugged.BeforeRollback);

        // Exempted (manifest cites C25 above): assert the KNOWN divergent values
        // explicitly, rather than silently skip the comparison — native preserves the
        // table variable's contents across the rollback (fact 2, non-transactional);
        // the debugger's #temp realization does not (R1, transactional; re-created
        // empty at the detached edge, D8).
        Assert.Equal(3, native.AfterRollback);
        Assert.Equal(0, debugged.AfterRollback);
    }

    // M7 matrix cell (design note §6.1/§6.2): mechanical pass-2 addition -- this
    // fixture is EXEC-free, so Into is trajectory-identical to Over; asserted (runs
    // the full pipeline and can genuinely fail), not assumed. The C25 exemption
    // applies on every pass (§6.2: "p10/C25 all passes, as today") -- loaded and
    // asserted here too rather than silently skipping the divergent field.
    [SkippableFact]
    public async Task DebuggerRun_MatchesNativeRun_SteppedInto_ExceptC25ExemptedField()
    {
        var rawConnectionString = Environment.GetEnvironmentVariable(ConnEnvVar);
        Skip.If(
            string.IsNullOrWhiteSpace(rawConnectionString),
            $"{ConnEnvVar} is not set; skipping fidelity test (never fake a pass — CLAUDE.md).");

        var manifest = await LoadManifestAsync();
        var exemption = Assert.Single(manifest.Exemptions);
        Assert.Equal("AfterRollback", exemption.Field);
        Assert.Equal("C25", exemption.CaveatId);

        var csb = new SqlConnectionStringBuilder(rawConnectionString);
        var server = csb.DataSource;
        var database = csb.InitialCatalog;

        await DeployFixtureAsync(rawConnectionString!);

        var native = await RunNativeAsync(rawConnectionString!);
        var debugged = await RunThroughDebuggerAsync(server, database, StepKind.Into);

        Assert.Equal(native.BeforeRollback, debugged.BeforeRollback);
        Assert.Equal(3, native.AfterRollback);
        Assert.Equal(0, debugged.AfterRollback);
    }

    // M6 item 6 (B9): fidelity pass 3 — continue with boost on. Assertions identical
    // to the pass-1 method above (manifest exemptions and all — §20.3.1.6 unchanged).
    [SkippableFact]
    public async Task DebuggerRun_MatchesNativeRun_ContinueBoost()
    {
        var rawConnectionString = Environment.GetEnvironmentVariable(ConnEnvVar);
        Skip.If(
            string.IsNullOrWhiteSpace(rawConnectionString),
            $"{ConnEnvVar} is not set; skipping fidelity test (never fake a pass — CLAUDE.md).");

        var manifest = await LoadManifestAsync();
        var exemption = Assert.Single(manifest.Exemptions);
        Assert.Equal("AfterRollback", exemption.Field);
        Assert.Equal("C25", exemption.CaveatId);

        var csb = new SqlConnectionStringBuilder(rawConnectionString);
        var server = csb.DataSource;
        var database = csb.InitialCatalog;

        await DeployFixtureAsync(rawConnectionString!);

        var native = await RunNativeAsync(rawConnectionString!);
        var debugged = await RunThroughDebuggerAsync(server, database, boost: true);

        Assert.Equal(native.BeforeRollback, debugged.BeforeRollback);
        Assert.Equal(3, native.AfterRollback);
        Assert.Equal(0, debugged.AfterRollback);
    }

    private static async Task<Manifest> LoadManifestAsync()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "corpus", "p10.manifest.json");
        var json = await File.ReadAllTextAsync(path);
        return JsonSerializer.Deserialize<Manifest>(json)
            ?? throw new InvalidOperationException("p10.manifest.json failed to deserialize.");
    }

    private static async Task DeployFixtureAsync(string connectionString)
    {
        var sqlPath = Path.Combine(AppContext.BaseDirectory, "corpus", "p10_table_variable.sql");
        var script = await File.ReadAllTextAsync(sqlPath);

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = script;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<TableVarResult> RunNativeAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var tran = (SqlTransaction)await connection.BeginTransactionAsync();

        await using var command = connection.CreateCommand();
        command.Transaction = tran;
        command.CommandText = "EXEC dbo.p10_table_variable @Seed = 1;";
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        var result = new TableVarResult(reader.GetInt32(0), reader.GetInt32(1));
        await reader.DisposeAsync();

        await tran.RollbackAsync();
        return result;
    }

    private static async Task<TableVarResult> RunThroughDebuggerAsync(
        string server, string database, StepKind stepKind = StepKind.Over, bool boost = false)
    {
        var target = new TargetEntry(server, "test", AllowWrites: false, Options: null);
        var options = new SessionOptions(
            server,
            database,
            LaunchMode.Procedure,
            "dbo.p10_table_variable",
            new Dictionary<string, string> { ["@Seed"] = "1" },
            ScriptText: null,
            Boost: boost);

        var result = await SessionHost.RunAsync(options, target, stepKind: stepKind);

        var resultSet = Assert.Single(result.Execution.ResultSets);
        var row = Assert.Single(resultSet.Rows);
        return new TableVarResult((int)row[0]!, (int)row[1]!);
    }
}
