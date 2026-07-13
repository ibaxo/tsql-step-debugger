using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.SqlClient;
using TsqlDbg.Core.Interpreter;
using TsqlDbg.Core.Sessions;
using TsqlDbg.Core.Targets;
using Xunit;

namespace TsqlDbg.Integration;

// DESIGN §20.4 + the M7 D1 SCOPE_IDENTITY() chain-sync fix (A26 §7.4, design note §1.4).
// p15_ext_frames pins the frame push/pop family (F3-1 post-pop poisoning, F3-1b
// callee-entry NULL) across all three passes; p15_ext_doom pins the doomed-mode family
// (F3-2). The Into pass is the biting pass for frames (RED pre-D1, GREEN post-D1); the
// doomed family bites on every pass. @@IDENTITY is C26-exempt on the Into pass (fact
// 31a — the push-seed INSERT perturbs it), per p15_ext.manifest.json. The non-hollow
// pre-D1 RED evidence is recorded in docs/archive/reviews/m7-hardening-core-opus.md §9.
[Collection("P15SharedFixture")]
public sealed class P15ExtScopeIdentityFramesFidelityTests
{
    private const string ConnEnvVar = "TSQLDBG_TEST_CONN";

    // All si fields are nullable: pre-D1 the poisoned reads surface NULL where native is
    // 100, so the RED is a legible "Expected 100, Actual null" diff (not an int-cast NRE).
    private sealed record FramesResult(int? SiEntry0, int? SiEntry1, int? SiA, int? SiB, int? AtIdentity0);
    private sealed record DoomResult(int? SiD);

    private sealed record Manifest(
        [property: JsonPropertyName("fixture")] string Fixture,
        [property: JsonPropertyName("exemptions")] List<Exemption> Exemptions);

    private sealed record Exemption(
        [property: JsonPropertyName("field")] string Field,
        [property: JsonPropertyName("caveatId")] string CaveatId,
        [property: JsonPropertyName("reason")] string Reason,
        [property: JsonPropertyName("passes")] List<string>? Passes);

    // ---- p15_ext_frames — the frame push/pop chain (cells si_entry_0/1, si_a, si_b) ----
    [SkippableFact] public Task Frames_Over() => RunFramesAsync(StepKind.Over, boost: false, isInto: false);
    [SkippableFact] public Task Frames_Into() => RunFramesAsync(StepKind.Into, boost: false, isInto: true);
    [SkippableFact] public Task Frames_ContinueBoost() => RunFramesAsync(StepKind.Over, boost: true, isInto: false);

    // ---- p15_ext_doom — the doomed-mode chain (cell si_d) ----
    [SkippableFact] public Task Doom_Over() => RunDoomAsync(StepKind.Over, boost: false);
    [SkippableFact] public Task Doom_Into() => RunDoomAsync(StepKind.Into, boost: false);
    [SkippableFact] public Task Doom_ContinueBoost() => RunDoomAsync(StepKind.Over, boost: true);

    private async Task RunFramesAsync(StepKind stepKind, bool boost, bool isInto)
    {
        var (server, database, conn) = RequireConnection();

        // §20.3.1.6: load the manifest and assert the C26 exemption + its per-pass filter.
        var manifest = await LoadManifestAsync();
        var exemption = Assert.Single(manifest.Exemptions);
        Assert.Equal("AtIdentity0", exemption.Field);
        Assert.Equal("C26", exemption.CaveatId);
        Assert.Equal(new[] { "into" }, exemption.Passes);

        await DeployFixtureAsync(conn);
        var native = await RunFramesNativeAsync(conn);
        var debugged = await RunFramesDebuggerAsync(server, database, stepKind, boost);

        // SCOPE_IDENTITY() cells — asserted EXACTLY on every pass (D1's target).
        Assert.Equal(native.SiEntry0, debugged.SiEntry0);   // F3-1b: callee entry NULL
        Assert.Equal(native.SiEntry1, debugged.SiEntry1);
        Assert.Equal(native.SiA, debugged.SiA);             // cell a
        Assert.Equal(native.SiB, debugged.SiB);             // cell b (F3-1)
        Assert.Null(native.SiEntry0);                       // native ground truth
        Assert.Equal((int?)100, native.SiA);
        Assert.Equal((int?)100, native.SiB);

        // @@IDENTITY (at_identity_0): C26. Exempt on the Into pass only — assert the
        // KNOWN divergent values explicitly (the p24 precedent, never silently skip).
        if (isInto)
        {
            Assert.Equal(100, native.AtIdentity0);          // native: caller's own last identity
            Assert.Null(debugged.AtIdentity0);              // debugger: perturbed to NULL by the push seed (C26)
        }
        else
        {
            // Over/boost step OVER the EXEC — no push seed — so @@IDENTITY matches (not exempt).
            Assert.Equal(native.AtIdentity0, debugged.AtIdentity0);
            Assert.Equal(100, native.AtIdentity0);
        }
    }

    private async Task RunDoomAsync(StepKind stepKind, bool boost)
    {
        var (server, database, conn) = RequireConnection();
        await DeployFixtureAsync(conn);
        var native = await RunDoomNativeAsync(conn);
        var debugged = await RunDoomDebuggerAsync(server, database, stepKind, boost);
        Assert.Equal(native.SiD, debugged.SiD);             // F3-2: post-resurrection si survives
        Assert.Equal((int?)100, native.SiD);                // native ground truth
    }

    private static (string Server, string Database, string ConnectionString) RequireConnection()
    {
        var raw = Environment.GetEnvironmentVariable(ConnEnvVar);
        Skip.If(string.IsNullOrWhiteSpace(raw),
            $"{ConnEnvVar} is not set; skipping fidelity test (never fake a pass — CLAUDE.md).");
        var csb = new SqlConnectionStringBuilder(raw);
        return (csb.DataSource, csb.InitialCatalog, raw!);
    }

    private static async Task<Manifest> LoadManifestAsync()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "corpus", "p15_ext.manifest.json");
        var json = await File.ReadAllTextAsync(path);
        return JsonSerializer.Deserialize<Manifest>(json)
            ?? throw new InvalidOperationException("p15_ext.manifest.json failed to deserialize.");
    }

    // GO-split loader (the ext procs share the p15 file after the existing byte-identical proc).
    private static async Task DeployFixtureAsync(string connectionString)
    {
        var sqlPath = Path.Combine(AppContext.BaseDirectory, "corpus", "p15_scope_identity_rowcount_chains.sql");
        var script = await File.ReadAllTextAsync(sqlPath);
        var batches = System.Text.RegularExpressions.Regex.Split(script, @"^\s*GO\s*$",
            System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        foreach (var batch in batches)
        {
            if (string.IsNullOrWhiteSpace(batch)) continue;
            await using var command = connection.CreateCommand();
            command.CommandText = batch;
            await command.ExecuteNonQueryAsync();
        }
    }

    private static async Task<FramesResult> RunFramesNativeAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var tran = (SqlTransaction)await connection.BeginTransactionAsync();

        await using var command = connection.CreateCommand();
        command.Transaction = tran;
        command.CommandText = "EXEC dbo.p15_ext_frames;";
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        var result = ReadFrames(reader);
        await reader.DisposeAsync();

        await tran.RollbackAsync();
        return result;
    }

    private static async Task<FramesResult> RunFramesDebuggerAsync(
        string server, string database, StepKind stepKind, bool boost)
    {
        var target = new TargetEntry(server, "test", AllowWrites: false, Options: null);
        var options = new SessionOptions(
            server, database, LaunchMode.Procedure, "dbo.p15_ext_frames",
            new Dictionary<string, string>(), ScriptText: null, Boost: boost);
        var result = await SessionHost.RunAsync(options, target, stepKind: stepKind);
        var resultSet = Assert.Single(result.Execution.ResultSets);
        var row = Assert.Single(resultSet.Rows);
        return new FramesResult(
            (int?)row[0], (int?)row[1], (int?)row[2], (int?)row[3], (int?)row[4]);
    }

    private static FramesResult ReadFrames(SqlDataReader reader) => new(
        reader.IsDBNull(0) ? null : reader.GetInt32(0),
        reader.IsDBNull(1) ? null : reader.GetInt32(1),
        reader.IsDBNull(2) ? null : reader.GetInt32(2),
        reader.IsDBNull(3) ? null : reader.GetInt32(3),
        reader.IsDBNull(4) ? null : reader.GetInt32(4));

    // Doom native run (p05/p24 pattern): the debuggee's own ROLLBACK needs a REAL,
    // explicit ambient transaction to cross (raw BEGIN TRANSACTION text, not an ADO.NET
    // SqlTransaction — a mid-batch T-SQL ROLLBACK would desync its client bookkeeping).
    private static async Task<DoomResult> RunDoomNativeAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        await using (var begin = connection.CreateCommand())
        {
            begin.CommandText = "BEGIN TRANSACTION;";
            await begin.ExecuteNonQueryAsync();
        }

        DoomResult result;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "EXEC dbo.p15_ext_doom;";
            await using var reader = await command.ExecuteReaderAsync();
            await reader.ReadAsync();
            result = new DoomResult(reader.IsDBNull(0) ? null : reader.GetInt32(0));
        }

        await using (var cleanup = connection.CreateCommand())
        {
            cleanup.CommandText = "IF @@TRANCOUNT > 0 ROLLBACK;";
            await cleanup.ExecuteNonQueryAsync();
        }

        return result;
    }

    private static async Task<DoomResult> RunDoomDebuggerAsync(
        string server, string database, StepKind stepKind, bool boost)
    {
        var target = new TargetEntry(server, "test", AllowWrites: false, Options: null);
        var options = new SessionOptions(
            server, database, LaunchMode.Procedure, "dbo.p15_ext_doom",
            new Dictionary<string, string>(), ScriptText: null, Boost: boost);
        var result = await SessionHost.RunAsync(options, target, stepKind: stepKind);
        var resultSet = Assert.Single(result.Execution.ResultSets);
        var row = Assert.Single(resultSet.Rows);
        return new DoomResult((int?)row[0]);
    }
}
