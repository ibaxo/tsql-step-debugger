using TsqlDbg.Core.Batches;

namespace TsqlDbg.Core.Sessions;

// DESIGN §10.2/§10.3 — the session half of the error model (the cursor half is
// ExecutionCursor.RouteError/ContinueAfterUnhandledFault/CatchDepth). M3 design decisions in
// docs/archive/reviews/m3-error-model-design-notes-fable.md.

/// <summary>How the last StepAsync resolved (§10.3).</summary>
public enum StepDisposition
{
    /// <summary>Normal completion of the unit's action (success paths, jumps, waitfor,
    /// predicate evals, RETURN).</summary>
    Performed,

    /// <summary>The unit faulted and routed: the cursor is on the first statement of
    /// the innermost eligible CATCH (§10.3 step 3). Driver publishes
    /// stopped(exception) subject to §10.6 filters.</summary>
    RoutedToCatch,

    /// <summary>§10.6 'all' filter: the fault is published AT the fault site before
    /// routing — the cursor still points at the faulted unit; the NEXT StepAsync
    /// performs the deferred route without server work.</summary>
    FaultAtSite,

    /// <summary>No eligible TRY, statement-level class: mirrored native behavior
    /// (facts 18/21, verified incl. inside CATCH blocks) — the error was reported like
    /// the native client would and execution continued natively: faulted predicates
    /// took the FALSE path, a faulted RETURN completed the frame with status 0, other
    /// units were skipped; @@ERROR/@@ROWCOUNT shadows read the fault number/0.</summary>
    UnhandledContinued,

    /// <summary>No eligible TRY, batch-aborting class (doomed transaction, THROW, or
    /// the §10.1 no-control-row propagate class): the frame is terminated — only
    /// inspection/REPL/terminate remain (§10.3 step 4). Session.IsBroken is now true.</summary>
    FrameFaulted,

    /// <summary>§10.5: an engine attention — command timeout (SqlException Number -2)
    /// or a pause via SqlCommand.Cancel() (SqlException Number 0). Statement rolled
    /// back, session healthy, NOT T-SQL-catchable. The cursor stays ON the unit: the
    /// user may retry (step again), skip (Jump to Cursor), or terminate. Driver
    /// publishes stopped reason:pause for either case (§10.5).</summary>
    EngineAttention,

    /// <summary>M4 (§11.3): a step-into pushed a frame — the cursor is on the callee's
    /// first SU. Driver publishes stopped reason:step with a taller stackTrace.</summary>
    SteppedIn,

    /// <summary>M4 (§11.5): the top frame ran to completion and popped — copy-back and
    /// EXEC @rc assignment happened (fact 23: completion-gated), the caller advanced
    /// past the call site, and the cursor is on the caller's next SU (or the caller
    /// completed too and the pop cascaded). Abnormal (error-routed) unwinds are NOT
    /// this — they surface as RoutedToCatch/FrameFaulted with frames already popped.</summary>
    FrameCompleted,

    /// <summary>A54 (§6/§11.5): a single step (`next`/`stepIn`) ran a MODULE frame off the
    /// END of its body with no explicit RETURN — the session PARKED at the implicit-return
    /// stop instead of popping. The frame is still on the stack (state table intact, final
    /// locals + OUTPUT params + pending `__ret` = 0 inspectable in its own scope); the
    /// cursor is completed but NOT popped. The driver publishes stopped reason:step at the
    /// module's closing line for `next`/`stepIn`; `continue`/`stepOut`/boost/RunToEnd run
    /// THROUGH it (like a GO boundary, A44). The NEXT step consumes the park
    /// (ConsumeReturnStopAsync) and performs the deferred §11.5 pop (copy-back + @rc +
    /// teardown). An explicit RETURN is never parked (it is already a stoppable line).</summary>
    AtImplicitReturn,

    /// <summary>M8 (§5.4/§8): a multi-batch <c>script</c> crossed a <c>GO</c> boundary — the
    /// batch scope was torn down and the next batch's fresh scope entered (ExitBatch +
    /// EnterBatch); the cursor now sits on the next batch's first SU. The driver publishes
    /// stopped reason:step at the new batch's entry, or continues under continue/boost.
    /// Analogous to FrameCompleted but at the GO seam rather than an EXEC return. Reached
    /// from a NORMAL boundary (the batch's cursor exhausted at depth 1) OR — lane 1b — from
    /// a §8.2 BATCH-TERMINAL fault whose fault-site stop was already published
    /// (FrameFaulted, session not broken) and the client now continues to the next batch;
    /// the boundary cross runs the §8.1 reconciliation (a doomed transaction is force-rolled
    /// back at the separator, fact 22). Only a connection-fatal / last-batch terminal fault
    /// ends the session.</summary>
    BatchCompleted,

    /// <summary>§10.4 A14 (ratified 2026-07-06 — docs/archive/reviews/m4-c23-doom-temp-severity-fable.md
    /// §4.3): while doomed, the SU's composed batch resolved a reference through a live
    /// §9 user-#temp registry entry — an object the fact-22 forced rollback destroyed
    /// with certainty (C23's object-existence face). NOTHING was executed; the cursor
    /// stays ON the SU and Error carries the diagnostic (original table names, C23
    /// citation). Two-phase like FaultAtSite: the NEXT StepAsync executes the batch
    /// anyway (routing per §10.1/§10.3 unchanged); Jump to Cursor skips. Driver
    /// publishes stopped reason:exception and always stops `continue` on it.</summary>
    DoomedTempPreflight,
}

/// <summary>M4 (§6): which step semantics StepAsync performs. Into differs from Over
/// only on an eligible EXEC unit (§11.1) — anywhere else it IS Over.</summary>
public enum StepKind { Over, Into }

public sealed record StepOutcome(StepDisposition Disposition, ErrorContextValues? Error = null)
{
    public static readonly StepOutcome Performed = new(StepDisposition.Performed);
}

/// <summary>One entry of the session's ErrorContextStack (§10.2): the caught error's
/// values plus where it originated. R7 substitutions and the §10.7 re-materialization
/// always read the TOP entry; nested TRY/CATCH nests entries.</summary>
public sealed record ErrorContext(ErrorContextValues Values, Interpreter.StatementUnit OriginUnit, int OriginFrame);
