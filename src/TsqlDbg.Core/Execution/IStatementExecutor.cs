namespace TsqlDbg.Core.Execution;

// DESIGN §3: "Core is UI-free and fully unit-testable: IStatementExecutor abstracts
// the server (ExecuteComposedBatch(text) -> ControlRow + resultsets + messages); tests
// inject a fake." The control-row parsing contract (§7.3) is layered on top of
// BatchResult starting M1; in M0 callers just read ResultSets/Messages directly.
// M3 adds the parameterized overload (§10.4 doomed-mode seeding + resurrection
// re-seed) and the StatementExecutionException wrapping contract: implementations
// surface server/transport failures that produced NO control row as
// StatementExecutionException so Session's §10.1 classification stays fake-testable.
public interface IStatementExecutor : IAsyncDisposable
{
    Task<BatchResult> ExecuteAsync(string batchText, CancellationToken cancellationToken = default);

    Task<BatchResult> ExecuteAsync(string batchText, IReadOnlyList<BatchParameter> parameters, CancellationToken cancellationToken = default);

    /// <summary>D5/A13 (§10.1, fact 24 Group A): execute with client-side error
    /// absorption — severity ≤ 16 errors arrive as messages (surfaced on
    /// <see cref="BatchResult.AbsorbedErrors"/> + <see cref="BatchResult.Messages"/>)
    /// instead of exceptions, so a stepped-over EXEC with no armed TRY anywhere runs
    /// oracle-free and the callee continues past statement-level errors exactly as
    /// native does (fact 23-H). Severity ≥ 17, attention, and transport failures still
    /// throw per the standard contract. Default implementation = no absorption
    /// (existing fakes keep compiling; the real executor overrides).</summary>
    Task<BatchResult> ExecuteAbsorbingAsync(string batchText, CancellationToken cancellationToken = default)
        => ExecuteAsync(batchText, cancellationToken);
}
