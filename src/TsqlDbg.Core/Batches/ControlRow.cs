namespace TsqlDbg.Core.Batches;

// DESIGN §7.5 display projection: one pair of columns per live variable.
public sealed record DisplayValue(string? Text, bool IsNull);

// DESIGN §7.3: "Control row schema (fixed contract): __dbg_ctl(1), ok(bit), rc,
// scope_identity, trancount, xact_state, err_number, err_severity, err_state,
// err_line, err_procedure, err_message, v_<name>… (display projection). Core parses
// by column name; add-only evolution." Rc is only ever populated on the Ok path
// (§7.1's template computes it only inside BEGIN TRY); the Err* fields only on the
// CATCH path — the two branches are mutually exclusive per batch.
// D5/A13 adds err_after (add-only): emitted ONLY by oracle-free stepped-over EXEC
// batches, carrying the caller-scope @@ERROR captured right after the EXEC — under
// absorption "the batch succeeded" no longer implies @@ERROR = 0 (an EXEC that itself
// failed statement-level, e.g. 2812, is absorbed and the batch continues; fact 24 d).
// M6/F2 (ruled 2026-07-07) adds scope_identity to BOOSTED CATCH rows (add-only, same
// pattern): completed in-slice identity inserts must reach the R6 shadow at fault
// time, and SCOPE_IDENTITY() survives into CATCH (fact 26d) — so ScopeIdentity is
// Ok-path-or-boosted-CATCH; non-boosted CATCH rows keep it null.
public sealed record ControlRow(
    bool Ok,
    int? Rc,
    decimal? ScopeIdentity,
    int Trancount,
    int XactState,
    int? ErrNumber,
    int? ErrSeverity,
    int? ErrState,
    int? ErrLine,
    string? ErrProcedure,
    string? ErrMessage,
    IReadOnlyDictionary<int, DisplayValue> DisplayValues,
    int? ErrAfter = null);
