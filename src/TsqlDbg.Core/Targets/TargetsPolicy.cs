namespace TsqlDbg.Core.Targets;

// DESIGN §16 enforcement + §4 session-init step 1 (path resolution + allowlist check).
public static class TargetsPolicy
{
    // DESIGN §4 step 1: "Path from launch config targetsFile, else MSSQL_DEBUG_TARGETS
    // env, else ${workspaceFolder}/targets.json." envVarReader/workspaceFolder are
    // injected (not read from Environment directly) so this stays testable without a
    // real process environment.
    public static string ResolvePath(string? launchConfigTargetsFile, string? workspaceFolder, Func<string, string?> envVarReader)
    {
        if (!string.IsNullOrEmpty(launchConfigTargetsFile))
        {
            return launchConfigTargetsFile;
        }

        var fromEnv = envVarReader("MSSQL_DEBUG_TARGETS");
        if (!string.IsNullOrEmpty(fromEnv))
        {
            return fromEnv;
        }

        if (!string.IsNullOrEmpty(workspaceFolder))
        {
            return Path.Combine(workspaceFolder, "targets.json");
        }

        throw new TargetsPolicyException(
            "Could not resolve targets.json location: no launch config 'targetsFile', no " +
            "MSSQL_DEBUG_TARGETS env var, and no workspaceFolder.");
    }

    // Unknown server -> refuse (DESIGN §16). Called before any connection is opened.
    public static TargetEntry Resolve(TargetsFile targetsFile, string server)
    {
        if (!targetsFile.TryGet(server, out var entry))
        {
            throw new TargetsPolicyException(
                $"Server '{server}' is not present in targets.json. Refusing to launch " +
                "(DESIGN.md §16: unknown server -> refuse).");
        }

        return entry;
    }
}
