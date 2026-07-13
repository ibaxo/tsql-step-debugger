using Microsoft.Data.SqlClient;
using TsqlDbg.Core.Sessions;
using TsqlDbg.Core.Targets;
using Xunit;

namespace TsqlDbg.Integration;

// DESIGN §2 (A57): compatLevel:0 (auto) resolves the ScriptDom parser from the server's product
// major version, read from SqlConnection.ServerVersion at login (LiveSession.OpenAsync) — a wire
// only exercisable LIVE (a FakeStatementExecutor has no real ServerVersion). These probes drive
// LiveSession end-to-end and cross-check EffectiveCompatLevel against the server's own reported
// version via an INDEPENDENT query (SERVERPROPERTY, a different path than the .ServerVersion
// property the resolver reads), so the assertion isn't circular.
public sealed class CompatLevelAutoDetectLiveTests
{
    private const string ConnEnvVar = "TSQLDBG_TEST_CONN";

    private static async Task<int> ReadServerMajorAsync(string connectionString)
    {
        await using var probe = new SqlConnection(connectionString);
        await probe.OpenAsync();
        await using var cmd = probe.CreateCommand();
        // PARSENAME(...,4) is the leftmost of the 4-part "major.minor.build.revision".
        cmd.CommandText = "SELECT CAST(PARSENAME(CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(64)), 4) AS int);";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    [SkippableFact]
    public async Task Auto_ResolvesParserFromServerProductVersion()
    {
        var raw = Environment.GetEnvironmentVariable(ConnEnvVar);
        Skip.If(string.IsNullOrWhiteSpace(raw),
            $"{ConnEnvVar} is not set; skipping live probe (never fake a pass — CLAUDE.md).");

        var csb = new SqlConnectionStringBuilder(raw);
        var serverMajor = await ReadServerMajorAsync(raw!);
        var expected = Math.Clamp(serverMajor * 10, 150, 170);

        // compatLevel:0 = auto — LiveSession.OpenAsync reads ServerVersion, Session resolves it.
        var options = new SessionOptions(
            csb.DataSource, csb.InitialCatalog, LaunchMode.Script, null, null, "SELECT 1;", CompatLevel: 0);
        var target = new TargetEntry(csb.DataSource, "test", AllowWrites: false, Options: null);

        await using var liveSession = await LiveSession.OpenAsync(options, target);

        Assert.Equal(expected, liveSession.Session.EffectiveCompatLevel);
        Assert.Contains(liveSession.Session.EffectiveCompatLevel, new[] { 150, 160, 170 });

        // An in-band server (15/16/17) auto-detects silently — no clamp/fallback warning.
        if (serverMajor is >= 15 and <= 17)
        {
            Assert.DoesNotContain(liveSession.Session.LaunchWarnings, w => w.Contains("compatLevel"));
        }
    }

    [SkippableFact]
    public async Task ExplicitPin_OverridesAutoDetection()
    {
        var raw = Environment.GetEnvironmentVariable(ConnEnvVar);
        Skip.If(string.IsNullOrWhiteSpace(raw),
            $"{ConnEnvVar} is not set; skipping live probe (never fake a pass — CLAUDE.md).");

        var csb = new SqlConnectionStringBuilder(raw);

        // An explicit 160 is honored verbatim regardless of the server's actual version.
        var options = new SessionOptions(
            csb.DataSource, csb.InitialCatalog, LaunchMode.Script, null, null, "SELECT 1;", CompatLevel: 160);
        var target = new TargetEntry(csb.DataSource, "test", AllowWrites: false, Options: null);

        await using var liveSession = await LiveSession.OpenAsync(options, target);

        Assert.Equal(160, liveSession.Session.EffectiveCompatLevel);
        Assert.DoesNotContain(liveSession.Session.LaunchWarnings, w => w.Contains("compatLevel"));
    }
}
