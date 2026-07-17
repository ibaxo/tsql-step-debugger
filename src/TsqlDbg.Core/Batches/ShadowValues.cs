using TsqlDbg.Core.Rewrite;

namespace TsqlDbg.Core.Batches;

/// <summary>
/// The values behind R7's ERROR_*() substitutions — one caught error, as reported by
/// the §7.3 control row (already line-mapped by the session per §10.2 before it gets
/// here). Message is never null (a caught error always has one); Procedure/Line are
/// null for faults in our own ad-hoc batches.
/// </summary>
public sealed record ErrorContextValues(
    int Number, int Severity, int State, int? Line, string? Procedure, string Message);

// DESIGN §7.4: shadow substitutes (R4-R7) are assigned from the adapter's last observed
// values. R4/R5/R6 come from control rows and the fact-12/17/18 observation rules
// below; R7's Err* come from the ACTIVE error context (§10.2), set/cleared by the
// session as its ErrorContextStack changes.
public sealed class ShadowValues
{
    public int? RowCount { get; private set; }
    public int? ErrorNumber { get; private set; }
    public decimal? ScopeIdentity { get; private set; }

    /// <summary>Top of the session's ErrorContextStack, or null outside any CATCH (§10.2).</summary>
    public ErrorContextValues? ErrorContext { get; private set; }

    public static ShadowValues Initial() => new();

    // <param name="scopeChainInSync">DESIGN §7.4 / A26 (D1): the live SCOPE_IDENTITY()
    // capture is native truth only while the session's real chain equals the current
    // frame's native scope chain (facts 26d/26e). The session runs every frame in ONE
    // real scope, so after a frame pop or doom entry the chains desync — the session
    // passes <c>false</c> and the shadow KEEPS its client-modeled value (skip the
    // capture). A completed insert-family statement re-synchronizes both chains
    // (fact 31b); the session passes <c>true</c> then, and the capture is taken.
    // rc/err are per-statement live truth in every regime and are always captured.</param>
    public void ObserveSuccess(ControlRow row, bool scopeChainInSync = true)
    {
        RowCount = row.Rc;
        // §7.3's Ok path historically had no raw "err" column: @@ERROR is 0 by
        // definition after any statement that did not fault. D5/A13 breaks that
        // implication for oracle-free stepped-over EXEC batches — an EXEC that itself
        // failed statement-level (e.g. 2812 missing proc) is ABSORBED, the batch
        // continues, and the caller-scope @@ERROR is non-zero (fact 24 d). Those
        // batches carry the captured value in err_after; every other batch leaves it
        // null → 0, the pre-D5 behavior exactly.
        ErrorNumber = row.ErrAfter ?? 0;
        if (scopeChainInSync)
        {
            ScopeIdentity = row.ScopeIdentity;
        }
    }

    // Engine fact 12 (docs/engine-facts.md, verified live): evaluating a debuggee
    // IF/WHILE predicate resets @@ROWCOUNT and @@ERROR to 0 — the predicate itself sees
    // the pre-evaluation values (via ordinary R4/R5 shadow rewrites), the taken branch
    // sees zeros. Called by the driver after every EvaluatePredicate round trip; never
    // sourced from the predicate batch's control row (its rc reflects the debugger's
    // wrapper SELECT, not native truth). SCOPE_IDENTITY() is scope-stable and unaffected
    // (no predicate form can perform an identity insert). Debugger-initiated predicate
    // evaluations (breakpoint conditions §13, watch §12.4) are invisible to the debuggee
    // and must NOT call this.
    public void ObservePredicateEvaluation()
    {
        RowCount = 0;
        ErrorNumber = 0;
    }

    // Engine fact 17 (M3, verified live): a real WAITFOR resets @@ROWCOUNT and @@ERROR
    // to 0. waitfor:"skip" mode never sends the statement, so it must mirror the reset
    // here; "honor" mode is faithful automatically via the control row.
    public void ObserveWaitFor()
    {
        RowCount = 0;
        ErrorNumber = 0;
    }

    // §5.4/A48 (verified live 2026-07-13): module-creating DDL (CREATE/ALTER PROCEDURE/
    // FUNCTION/VIEW/TRIGGER) resets @@ROWCOUNT and @@ERROR to 0 — same shape as a WAITFOR
    // or predicate eval (facts 12/17) — and cannot perform an identity insert, so
    // SCOPE_IDENTITY() is left as-is. Module DDL runs BARE (its own batch, no §7.1 control
    // row), so the driver mirrors the reset here after a successful CREATE/ALTER.
    public void ObserveModuleDdl()
    {
        RowCount = 0;
        ErrorNumber = 0;
    }

    // A63/N1 (verified live): a cursor declaration — `DECLARE c CURSOR …` OR `SET @c = CURSOR FOR …`
    // — does NOT modify @@ROWCOUNT; it retains the PRIOR statement's value (e.g. an `UPDATE … ; SET @c
    // = CURSOR …; IF @@ROWCOUNT > 0` branches on the UPDATE's count). This is unlike a predicate /
    // WAITFOR / module-DDL, which RESET it to 0. The composed batch zeroes @@ROWCOUNT anyway (its
    // guard `IF CURSOR_STATUS(...)` predicate + the preamble SETs), so its captured control row is 0 —
    // wrong. Preserve RowCount (and ScopeIdentity — a cursor declare can perform no identity insert);
    // @@ERROR is 0 after any successful statement. Covers both the variable (N1) and named (N2) forms.
    public void ObserveCursorDeclare()
    {
        ErrorNumber = 0;
        // RowCount and ScopeIdentity intentionally left as-is (preserved).
    }

    // Engine fact 18 (M3, verified live): at the first statement inside a CATCH block,
    // @@ERROR reads the caught error's number and @@ROWCOUNT reads 0. Called by the
    // session when a fault routes (§10.3 step 3); after that, normal per-statement
    // observation resumes (@@ERROR back to 0 on the first successful CATCH statement —
    // fact 18 probe B, which ObserveSuccess produces on its own). SCOPE_IDENTITY() is
    // left as-is (identity consumption by a failed statement is C6 territory — the
    // control row cannot see it either way).
    public void ObserveFault(int errorNumber)
    {
        RowCount = 0;
        ErrorNumber = errorNumber;
    }

    // §10.3/§11.5 empty-CATCH transit (verified live, probes X1/X4/X6, 2026-07-17): when a fault
    // routes into an EMPTY CATCH (no statement to stop on — the route runs straight through END
    // CATCH), the error is handled VACUOUSLY, and after END CATCH BOTH @@ERROR and @@ROWCOUNT read 0
    // — @@ROWCOUNT because fact 18 zeroes it at CATCH entry and the empty CATCH runs no statement to
    // change it; @@ERROR because there is no in-CATCH statement to leave it non-zero (unlike a NON-empty
    // CATCH, whose first statement is where ObserveFault's fact-18 @@ERROR=number is observed). These
    // zeros then carry across module exit (ShadowValues carry model, §11.5) so a caller reading @@ERROR
    // /@@ROWCOUNT after the EXEC of such a callee sees 0, matching native. The session calls this instead
    // of ObserveFault when RouteError vacated the CATCH (no net CatchDepth increase). SCOPE_IDENTITY() is
    // left as-is (per-scope; the pop's RestoreScopeIdentity handles it).
    public void ObserveHandledCatchReturn()
    {
        RowCount = 0;
        ErrorNumber = 0;
    }

    /// <summary>§10.2: the session sets this when a context is pushed / the top changes,
    /// and clears it when the stack empties. Sources R7's Err* literals.</summary>
    public void SetErrorContext(ErrorContextValues? context) => ErrorContext = context;

    // M4 (§11.5): SCOPE_IDENTITY() is per-scope natively — after an EXEC returns, the
    // caller reads its own scope's pre-call value, not the callee's. The session saves
    // the caller's value at push and restores it at pop. @@ROWCOUNT/@@ERROR are NOT
    // restored: natively they carry the callee's last statement across module exit.
    internal decimal? CaptureScopeIdentity() => ScopeIdentity;
    internal void RestoreScopeIdentity(decimal? value) => ScopeIdentity = value;

    // M6 F2 (§14/A21, ruled 2026-07-07): boosted CATCH control rows carry a live
    // scope_identity capture — completed in-slice identity inserts must reach the R6
    // shadow at fault time (a boosted batch's pre-BATCH shadow is stale where
    // interpreted mode's pre-statement shadow is native-current), and SCOPE_IDENTITY()
    // survives into CATCH (fact 26d). NULL is a legitimate native value here (a slice
    // insert into a non-identity table resets the chain) — apply unconditionally on
    // the boost fault path; rc/err ride the standard ObserveFault.
    public void ObserveBoostFaultScopeIdentity(decimal? value) => ScopeIdentity = value;

    // Literal T-SQL to splice into a DECLARE ... = <literal> initializer, or "NULL".
    public string Literal(ShadowKind kind) => kind switch
    {
        ShadowKind.Rowcount => RowCount?.ToString() ?? "NULL",
        ShadowKind.Error => ErrorNumber?.ToString() ?? "NULL",
        ShadowKind.ScopeIdentity => ScopeIdentity?.ToString() ?? "NULL",
        // R7 (§10.2): outside an active context these read NULL natively ("ERROR_*()
        // return NULL outside any CATCH", Appendix C fact 7's confirmed half) — but R7
        // only rewrites while a context is active, so a NULL here normally means the
        // rewrite flag and the context went out of sync; emitting NULL is still the
        // faithful value for that (unreachable) case.
        ShadowKind.ErrNumber => ErrorContext?.Number.ToString() ?? "NULL",
        ShadowKind.ErrSeverity => ErrorContext?.Severity.ToString() ?? "NULL",
        ShadowKind.ErrState => ErrorContext?.State.ToString() ?? "NULL",
        ShadowKind.ErrLine => ErrorContext?.Line?.ToString() ?? "NULL",
        ShadowKind.ErrProcedure => ErrorContext?.Procedure is { } p ? SqlStringLiteral(p) : "NULL",
        ShadowKind.ErrMessage => ErrorContext?.Message is { } m ? SqlStringLiteral(m) : "NULL",
        _ => throw new System.ArgumentOutOfRangeException(nameof(kind)),
    };

    internal static string SqlStringLiteral(string value) => "N'" + value.Replace("'", "''") + "'";
}
