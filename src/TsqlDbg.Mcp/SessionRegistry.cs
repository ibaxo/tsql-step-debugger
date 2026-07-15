using System.Collections.Concurrent;
using TsqlDbg.Core.Tracing;

namespace TsqlDbg.Mcp;

// DESIGN §24.2: owns the live set of debug sessions, the maxSessions cap, the idle-timeout
// sweep, and the unconditional teardown-all backstop. Registered as a DI singleton and
// injected into the tool classes. Thread-safe: the MCP SDK may dispatch tools concurrently
// across DIFFERENT sessions (different connections); per-session serialization is the
// session's own gate (McpDebugSession).
public sealed class SessionRegistry : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, McpDebugSession> _sessions = new();
    private readonly McpServerConfig _config;
    private readonly ITraceSink? _trace;
    private readonly Func<string, string?> _env;
    private readonly Timer _idleSweep;
    private int _counter;

    public SessionRegistry(McpServerConfig config, Func<string, string?>? envReader = null)
    {
        _config = config;
        _env = envReader ?? Environment.GetEnvironmentVariable;
        _trace = string.IsNullOrEmpty(config.HostTracePath) ? null : new FileTraceSink(config.HostTracePath);

        // DESIGN §24.1(5): the idle backstop for an agent that opens a session and wanders off
        // with locks held (C3). Sweeps every 30s; tears down (rollback) anything idle past the
        // configured timeout.
        _idleSweep = new Timer(_ => SweepIdle(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    public IReadOnlyCollection<McpDebugSession> All => _sessions.Values.ToList();

    public McpServerConfig Config => _config;

    // DESIGN §24.2/§24.1: allowlist-gate → cap check → open (§4 init) → register. Throws
    // TargetResolver.RefusedException (default-deny) or InvalidOperationException (cap reached).
    public async Task<McpDebugSession> OpenAsync(SessionArgs args, CancellationToken ct)
    {
        var target = TargetResolver.Resolve(_config, args.Server, _env);

        if (_sessions.Count >= _config.MaxSessions)
        {
            throw new InvalidOperationException(
                $"Cannot open a new session: the max of {_config.MaxSessions} live sessions is reached " +
                "(DESIGN §24.1/§24.2). End an existing session first, or raise --max-sessions. A live " +
                "session is never evicted to make room — that would silently roll back an agent's work.");
        }

        // DESIGN §24.1(4)/C27: a SQL-auth password arrives ONLY via the process env var — never
        // a tool argument (which would land in the agent's transcript). Read once, passed
        // transiently into the open, never retained by this registry.
        string? password = null;
        if (args.IsSqlAuth)
        {
            password = _env("TSQLDBG_SQL_PASSWORD");
            if (string.IsNullOrEmpty(password))
            {
                throw new InvalidOperationException(
                    "authType='sql' requires a password via the TSQLDBG_SQL_PASSWORD environment variable " +
                    "(DESIGN §24.1(4)/C27) — it must not be passed as a tool argument. None was set.");
            }
        }

        var id = $"s{Interlocked.Increment(ref _counter)}";
        var session = await McpDebugSession.OpenAsync(id, args, target, _trace, password, ct).ConfigureAwait(false);
        _sessions[id] = session;
        return session;
    }

    public McpDebugSession Get(string sessionId)
        => _sessions.TryGetValue(sessionId, out var s)
            ? s
            : throw new KeyNotFoundException($"No session '{sessionId}' (it may have ended or timed out).");

    // DESIGN §24.2: end_session — teardown (commit-gated in EndAsync) then deregister.
    public async Task<SessionSummary> EndAsync(string sessionId)
    {
        if (!_sessions.TryRemove(sessionId, out var session))
        {
            throw new KeyNotFoundException($"No session '{sessionId}' to end.");
        }

        return await session.EndAsync().ConfigureAwait(false);
    }

    // DESIGN §24.2/§24.3: a trace tool tears down internally (RunTraceAsync); deregister after.
    public void Remove(string sessionId) => _sessions.TryRemove(sessionId, out _);

    private void SweepIdle()
    {
        var cutoff = DateTime.UtcNow.AddSeconds(-_config.SessionIdleTimeoutSeconds);
        foreach (var kvp in _sessions)
        {
            if (kvp.Value.LastActivityUtc < cutoff && _sessions.TryRemove(kvp.Key, out var stale))
            {
                _trace?.Event("mcp.session.idle_teardown", $"session={kvp.Key} idle>{_config.SessionIdleTimeoutSeconds}s");
                // DESIGN §24.1(5): unconditional rollback (DisposeAsync, no commit path).
                _ = stale.DisposeAsync().AsTask();
            }
        }
    }

    // DESIGN §24.2: host shutdown / lost stdio — tear EVERY live session down by rollback.
    public async ValueTask DisposeAsync()
    {
        await _idleSweep.DisposeAsync().ConfigureAwait(false);
        foreach (var kvp in _sessions)
        {
            if (_sessions.TryRemove(kvp.Key, out var session))
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
        }

        (_trace as IDisposable)?.Dispose();
    }
}
