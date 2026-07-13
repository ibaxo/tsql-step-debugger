namespace TsqlDbg.Core.Execution;

/// <summary>
/// One ADO.NET parameter attached to a composed batch (M3, §10.4): while the
/// transaction is doomed (XACT_STATE() = -1) the state table cannot be written
/// (error 3930), so variable values ride parameters seeded from the session's binary
/// snapshot instead of the table — and the resurrection re-seed UPDATE uses the same
/// mechanism. Values are the snapshot's raw objects (DBNull/null → NULL); the
/// executor maps .NET types to sensible SqlDbTypes (notably DateTime → datetime2) and
/// the batch text wraps every read in CONVERT(&lt;declared type&gt;, @p) so declared
/// precision/scale wins.
/// </summary>
public sealed record BatchParameter(string Name, object? Value);

/// <summary>
/// Executor-level failure with no §7.3 control row (DESIGN §10.1's "propagate" class:
/// compile/deferred-name-resolution errors, severity ≥ 20, broken connections,
/// timeouts). SqlStatementExecutor wraps SqlException into this so Session's
/// classification stays fake-testable (§3/§20.2).
/// </summary>
public sealed class StatementExecutionException : System.Exception
{
    /// <summary>SqlException.Class — 20+ is connection-fatal (§10.1).</summary>
    public int SeverityClass { get; }

    /// <summary>SqlException.Number; -2 = command timeout (§10.5).</summary>
    public int Number { get; }

    /// <summary>SqlError.Procedure of the first error, when the server named one —
    /// M4 (§10.2): a compile-class fault in a stepped-into callee routed to a caller
    /// CATCH must give ERROR_PROCEDURE()/ERROR_LINE() genuine engine data (fact 23-F),
    /// so the wrap carries them verbatim. Null/0 when the server sent none.</summary>
    public string? Procedure { get; }

    /// <summary>SqlError.LineNumber of the first error (see <see cref="Procedure"/>).</summary>
    public int LineNumber { get; }

    /// <summary>M8 (§10.1/§8.3): the connection was no longer <c>Open</c> after the
    /// command threw (a transport-level severed connection / connection-reset that
    /// SqlClient did not surface as severity ≥ 20). Connection-fatal like severity ≥ 20:
    /// session-fatal even in multi-batch script mode — a batch-terminal fault would try to
    /// advance to the next batch, but a dead connection cannot run one, so this ends the
    /// session (design note §8.3).</summary>
    public bool ConnectionBroken { get; }

    public StatementExecutionException(
        string message, int severityClass, int number, System.Exception? inner = null,
        string? procedure = null, int lineNumber = 0, bool connectionBroken = false)
        : base(message, inner)
    {
        SeverityClass = severityClass;
        Number = number;
        Procedure = string.IsNullOrEmpty(procedure) ? null : procedure;
        LineNumber = lineNumber;
        ConnectionBroken = connectionBroken;
    }
}
