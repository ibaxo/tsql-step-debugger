using Microsoft.Data.SqlClient;
using TsqlDbg.Core.Execution;
using TsqlDbg.Core.Targets;
using TsqlDbg.Core.Tracing;

namespace TsqlDbg.Core.Sessions;

// DESIGN §4 gate amendment A2 (docs/archive/reviews/m0-gate-review-fable.md): "the launch
// response is sent only after session init succeeds; init failure fails the launch
// request with the error message." This means the Adapter now needs a connection
// that stays open across DAP round trips (opened + initialized during `launch`,
// stepped during `next`/`continue`, torn down on `disconnect`) rather than the
// open-run-close-in-one-call shape SessionHost.RunAsync uses for the fidelity
// harness. LiveSession is that long-lived wrapper; SessionHost.RunAsync is
// unchanged (still open-run-to-end-close in one call, for tests).
public sealed class LiveSession : IAsyncDisposable
{
    private readonly SqlConnection _connection;
    private readonly SqlStatementExecutor _executor;
    private readonly ITraceSink _trace;
    private readonly string _nonce;

    public Session Session { get; }

    private LiveSession(SqlConnection connection, SqlStatementExecutor executor, Session session, ITraceSink trace, string nonce)
    {
        _connection = connection;
        _executor = executor;
        Session = session;
        _trace = trace;
        _nonce = nonce;
    }

    public static async Task<LiveSession> OpenAsync(
        SessionOptions options, TargetEntry target, ITraceSink? trace = null, CancellationToken cancellationToken = default,
        string? password = null)
    {
        var sink = trace ?? NullTraceSink.Instance;
        var nonce = SqlConnectionStringFactory.NewNonce();
        // DESIGN §16 (A41): transient SQL password (adapter env channel); never stored/traced.
        var connectionString = SqlConnectionStringFactory.Build(options, target, nonce, password);

        sink.Event("connection.open", $"server={options.Server} database={options.Database} nonce={nonce}");
        var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var executor = new SqlStatementExecutor(connection, options.CommandTimeoutSeconds);
        // M5 I3 (§12.1): @@SPID via the connection itself — TDS returns it at login,
        // so this is truly zero extra round trips ("captured once at connection open").
        var session = new Session(
            options, executor, sink, nonce, connection.ServerProcessId, ParseServerMajorVersion(connection.ServerVersion));

        try
        {
            await session.InitializeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // DESIGN §4 teardown must run even when init itself fails partway through
            // (e.g. the state table was created but BEGIN TRAN hasn't happened yet).
            await session.TeardownAsync().ConfigureAwait(false);
            await executor.DisposeAsync().ConfigureAwait(false);
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return new LiveSession(connection, executor, session, sink, nonce);
    }

    // DESIGN §2 (A57): SqlConnection.ServerVersion is "major.minor.build" from the login
    // response — zero round trips, exactly like ServerProcessId above. The leading integer is
    // the product major version (16 -> compat 160). 0 if absent/unparseable, which leaves
    // compatLevel:auto at its 150 fallback (CompatLevelResolver).
    private static int ParseServerMajorVersion(string? serverVersion)
    {
        if (string.IsNullOrEmpty(serverVersion))
        {
            return 0;
        }

        var dot = serverVersion.IndexOf('.');
        var head = dot >= 0 ? serverVersion.Substring(0, dot) : serverVersion;
        return int.TryParse(head, out var major) && major > 0 ? major : 0;
    }

    // IAsyncDisposable's contract — the unconditional-rollback default (no decision
    // callback). disconnect/error/lost-adapter paths all go through this one.
    public ValueTask DisposeAsync() => DisposeAsync(commitDecision: null);

    // M7 (§16 commit-modal): the ONLY overload the adapter's explicit terminate
    // handler uses, and only when armed (commitMode=Commit) — see
    // Session.TeardownAsync's own remarks for the full gating.
    public async ValueTask DisposeAsync(Func<Task<bool>>? commitDecision)
    {
        await Session.TeardownAsync(commitDecision).ConfigureAwait(false);
        await _executor.DisposeAsync().ConfigureAwait(false);
        await _connection.DisposeAsync().ConfigureAwait(false);
        _trace.Event("connection.close", $"nonce={_nonce}");
    }
}
