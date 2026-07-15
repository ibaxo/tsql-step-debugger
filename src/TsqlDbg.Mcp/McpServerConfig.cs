namespace TsqlDbg.Mcp;

// DESIGN §24.9: server-level configuration for the MCP host process, read once at
// startup from the environment (and, where noted, process args). Per-session options
// arrive as tool arguments instead (SessionArgs).
public sealed class McpServerConfig
{
    // DESIGN §24.1(1)/§16/§4-step-1: allowlist location. The env var is the recommended
    // machine-wide home; a launch --targets overrides it. Unlike the adapter there is no
    // workspaceFolder fallback (a headless host has no workspace) and no informed-consent
    // path — an unresolvable/absent file means every start_session refuses (§24.1).
    public string? TargetsFile { get; init; }

    // DESIGN §24.1(5)/§24.2: max concurrently-live sessions; a further start refuses
    // rather than evicting a live one (evicting would silently roll back an agent's work).
    public int MaxSessions { get; init; } = 4;

    // DESIGN §24.1(5): idle auto-teardown (unconditional rollback) — the C3 mitigation for
    // an agent that opens a session and wanders off with locks held.
    public int SessionIdleTimeoutSeconds { get; init; } = 300;

    // DESIGN §24.8: where trace files are written. Default: an OS-temp subdirectory.
    public string TraceOutputDirectory { get; init; } =
        Path.Combine(Path.GetTempPath(), "tsqldbg-mcp-traces");

    // DESIGN §19: the host's own protocol/SQL trace file (distinct from a session's logLevel).
    public string? HostTracePath { get; init; }

    // DESIGN §24.9/§4-step-1: resolve the targets path the same order the adapter uses,
    // minus the workspaceFolder leg. Env reader injected for testability (mirrors
    // TargetsPolicy.ResolvePath). Returns null when nothing supplies a path — the caller
    // (TargetResolver) then refuses, per §24.1's default-deny.
    public string? ResolveTargetsPath(Func<string, string?> envVarReader)
    {
        if (!string.IsNullOrEmpty(TargetsFile))
        {
            return TargetsFile;
        }

        var fromEnv = envVarReader("MSSQL_DEBUG_TARGETS");
        return string.IsNullOrEmpty(fromEnv) ? null : fromEnv;
    }

    // DESIGN §24.9: parse the host process args. Everything is optional; env vars fill the
    // rest. Unknown args are ignored (forward-compat, matching the targets.json parser rule).
    public static McpServerConfig FromArgsAndEnvironment(string[] args, Func<string, string?> env)
    {
        string? targetsFile = env("MSSQL_DEBUG_TARGETS");
        string? traceDir = null;
        string? hostTrace = null;
        int maxSessions = 4;
        int idleTimeout = 300;

        for (var i = 0; i < args.Length - 1; i++)
        {
            switch (args[i])
            {
                case "--targets":
                    targetsFile = args[i + 1];
                    break;
                case "--trace":
                    hostTrace = args[i + 1];
                    break;
                case "--trace-dir":
                    traceDir = args[i + 1];
                    break;
                case "--max-sessions" when int.TryParse(args[i + 1], out var m) && m > 0:
                    maxSessions = m;
                    break;
                case "--idle-timeout-sec" when int.TryParse(args[i + 1], out var t) && t > 0:
                    idleTimeout = t;
                    break;
            }
        }

        return new McpServerConfig
        {
            TargetsFile = targetsFile,
            MaxSessions = maxSessions,
            SessionIdleTimeoutSeconds = idleTimeout,
            TraceOutputDirectory = traceDir ?? Path.Combine(Path.GetTempPath(), "tsqldbg-mcp-traces"),
            HostTracePath = hostTrace,
        };
    }
}
