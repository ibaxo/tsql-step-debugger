using TsqlDbg.Core.Targets;

namespace TsqlDbg.Mcp;

// DESIGN §24.1(1)/§16: the programmatic surface's default-deny allowlist gate. This is the
// load-bearing safety difference from the interactive adapter — there is NO human to consent,
// so an unknown server, an unresolvable targets path, or an absent file is a HARD refusal,
// never a proceed-under-informed-consent fallback. Called before any connection is opened.
public static class TargetResolver
{
    public sealed class RefusedException : Exception
    {
        public RefusedException(string message) : base(message) { }
    }

    // DESIGN §24.1(1): resolve `server` to an allowlist entry or throw RefusedException.
    // envVarReader injected for testability (mirrors TargetsPolicy). The file is only read,
    // never written (CLAUDE.md safety rule 7).
    public static TargetEntry Resolve(McpServerConfig config, string server, Func<string, string?> envVarReader)
    {
        var path = config.ResolveTargetsPath(envVarReader);
        if (string.IsNullOrEmpty(path))
        {
            throw new RefusedException(
                "Refusing to launch: no targets.json is configured (set MSSQL_DEBUG_TARGETS or pass " +
                "--targets). The programmatic surface is default-deny — it will not connect to a server " +
                "that is not on an allowlist (DESIGN §24.1/§16). There is no human present to consent.");
        }

        TargetsFile targetsFile;
        try
        {
            targetsFile = TargetsFile.Load(path);
        }
        catch (Exception ex)
        {
            throw new RefusedException(
                $"Refusing to launch: could not load the allowlist '{path}' ({ex.Message}). " +
                "The programmatic surface requires a readable targets.json (DESIGN §24.1/§16).");
        }

        if (!targetsFile.TryGet(server, out var entry))
        {
            throw new RefusedException(
                $"Refusing to launch: server '{server}' is not present in the allowlist '{path}'. " +
                "The programmatic surface is default-deny (DESIGN §24.1/§16: unknown server → refuse). " +
                "Add the server to targets.json to allow it.");
        }

        return entry;
    }

    // DESIGN §24.1(2)/§16: the programmatic commit gate. Commit requires BOTH the session's
    // commitMode:"commit" AND the resolved target's allowWrites:true — there is no modal to
    // stand in for authorization here, so the double gate IS the authorization. Any other
    // combination tears down by rollback (the caller passes the returned bool to
    // TeardownAsync's decision callback).
    public static bool CommitAuthorized(bool commitModeRequested, TargetEntry target)
        => commitModeRequested && target.AllowWrites;
}
