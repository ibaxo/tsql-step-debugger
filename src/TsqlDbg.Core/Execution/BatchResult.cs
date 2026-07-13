namespace TsqlDbg.Core.Execution;

// DESIGN §10.4 / engine fact 22: an error the server appended AFTER every result set
// of the batch had streamed — the only known producer is 3998 ("Uncommittable
// transaction is detected at the end of the batch"), raised when a batch ends with a
// doomed transaction. The executor surfaces these instead of discarding the
// already-materialized control row; Session's watchdog validates they only ever
// accompany a doomed (xact_state = -1) control row.
public sealed record BatchTrailingError(int Number, int SeverityClass, string Message);

// Result of sending one composed batch (DESIGN §7.1) to the server. In M0 there is no
// control-row contract yet (§7.3 lands in M1) — ResultSets is just whatever the batch
// returned, in order, and Messages carries PRINT/InfoMessage text. M3 (§10.4/fact 22)
// adds TrailingErrors, add-only: null/empty for every healthy batch. D5/A13 adds
// AbsorbedErrors, add-only: non-null only for ExecuteAbsorbingAsync runs where
// severity ≤ 16 errors were absorbed as messages (fact 24 Group A) — each also
// appears in Messages as native-client error text, in stream order.
public sealed record BatchResult(
    IReadOnlyList<ResultSet> ResultSets,
    IReadOnlyList<string> Messages,
    IReadOnlyList<BatchTrailingError>? TrailingErrors = null,
    IReadOnlyList<BatchTrailingError>? AbsorbedErrors = null);
