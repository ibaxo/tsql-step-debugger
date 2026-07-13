using Microsoft.Data.SqlClient;
using TsqlDbg.Core.Execution;
using TsqlDbg.Core.Targets;
using TsqlDbg.Core.Tracing;

namespace TsqlDbg.Core.Sessions;

// DESIGN §4 steps 1-2 + connection teardown: real-connection plumbing that Session
// deliberately does not own (see Session's remarks) so Session stays unit-testable via
// a fake IStatementExecutor. This class is exercised by the integration/fidelity
// harness (tests/TsqlDbg.Integration), not unit tests.
public static class SessionHost
{
    // M4 (§22, design notes D9/§8 item 4): stepKind defaults to Over (M0-M3 fidelity
    // pass 1, and the "continue" degenerate case for frame-0-only fixtures); pass 2
    // (StepKind.Into) exercises the debugger's own frame push/pop/OUTPUT-copy-back
    // machinery instead of letting the server handle EXEC natively as one opaque
    // statement — a step-over EXEC never invokes that code at all.
    public static async Task<SessionResult> RunAsync(
        SessionOptions options,
        TargetEntry target,
        ITraceSink? trace = null,
        StepKind stepKind = StepKind.Over,
        CancellationToken cancellationToken = default,
        string? password = null)
    {
        var sink = trace ?? NullTraceSink.Instance;
        var nonce = SqlConnectionStringFactory.NewNonce();
        var connectionString = SqlConnectionStringFactory.Build(options, target, nonce, password);

        sink.Event("connection.open", $"server={options.Server} database={options.Database} nonce={nonce}");
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var executor = new SqlStatementExecutor(connection, options.CommandTimeoutSeconds);
        try
        {
            var session = new Session(options, executor, sink, nonce);
            return await session.RunToEndAsync(stepKind, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await executor.DisposeAsync().ConfigureAwait(false);
            sink.Event("connection.close", $"nonce={nonce}");
        }
    }
}
