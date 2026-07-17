// DESIGN §6 — the resumable, request-driven interpreter cursor. Phase-0 (Fable): linear
// slice. M2 (Fable): IF/WHILE phase machines, GOTO/BREAK/CONTINUE, RETURN, WAITFOR,
// JumpTo — decisions + live-verified engine facts in
// docs/archive/reviews/m2-cursor-design-notes-fable.md and docs/engine-facts.md facts 11-14.
// Gated to Fable/Opus for first implementation per CLAUDE.md; Sonnet extends within
// this shape.
//
// Driver contract (reference loop — M2 driver integration is the next Sonnet task;
// DeclareVariables/ExecuteUnit are the M1 loop, one behavior change noted):
//
//   var action = cursor.Peek();
//   switch (action) {
//     case DeclareVariables d:
//         // CHANGED by fact-14 hoisting: registration + state-table DDL happen at frame
//         // init now (Session.InitializeAsync registers every DECLARE in Index.All).
//         // Performing this action = run each non-null InitializerSql synthetic SET, in
//         // order — every time the SU executes (a DECLARE in a WHILE body re-runs its
//         // initializer per iteration, fact 14 case C).
//     case ExecuteUnit e:
//         // unchanged M1: BuildForUnit → ok=0 control row is session-fatal until §10 (M3),
//         // shadows.ObserveSuccess(control) on ok=1.
//     case EvaluatePredicate p:
//         // ComposedBatchBuilder.BuildForPredicate(frame, engine, ctx, p.Predicate,
//         // sourceText, shadows) → execute. The batch's single user result set carries
//         // column "p" (1/0). The control row's rc/scope_identity reflect the debugger's
//         // wrapper SELECT, never native truth — do NOT ObserveSuccess it. Instead:
//         //     shadows.ObservePredicateEvaluation();   // fact 12: a debuggee IF/WHILE
//         //                                             // predicate eval resets
//         //                                             // @@ROWCOUNT/@@ERROR to 0
//         // ok=0 (the predicate itself faulted) is session-fatal until §10 lands (M3).
//         cursor.Advance(new AdvanceSignal.PredicateEvaluated(pValue == 1));
//         // PredicateEvaluated both consumes the stop and selects the branch; answering
//         // a predicate stop with Normal throws. The next stop may be on the SAME line
//         // (single-statement branch written on the IF/WHILE line) — both are real,
//         // separate steps (§6: predicate eval is itself one visible step).
//     case Jump j:
//         // GOTO/BREAK/CONTINUE — no server round trip; publish the stop, then:
//         cursor.Advance(AdvanceSignal.Normal);          // the cursor performs the jump
//     case ReturnFromFrame r:
//         // r.Expression is non-null only in procedure frames (script frames with
//         // RETURN <value> are refused at validation — engine error 178, fact 13):
//         // BuildForScalarEval(...) → column "p" → the frame's return code (__ret);
//         // surface it in SessionResult for §20.3.1's returnCode. Shadow handling after
//         // this eval is moot in M2 (the session ends); M4 must probe @@ROWCOUNT/@@ERROR
//         // semantics across module exit before reusing this for callee frames.
//         cursor.Advance(AdvanceSignal.Normal);          // completes the frame:
//         // cursor.IsCompleted is now true → session end (normal), teardown rollback.
//     case WaitFor w:
//         // launch config waitfor:"skip" (default): Debug Console note, no server work —
//         // but shadows.ObserveWaitFor() (fact 17: a real WAITFOR resets @@ROWCOUNT and
//         // @@ERROR to 0; skip mode must mirror it); "honor": BuildForUnit(w.Unit) like
//         // any executable (blocks for the delay; control row carries native truth).
//         cursor.Advance(AdvanceSignal.Normal);
//     case Rethrow r:                                  // M3 — bare ;THROW (§10.2)
//         // Re-raise the ACTIVE error context: no server work. Take the top
//         // ErrorContextStack values; cursor.RouteError() — routed: reconcile contexts
//         // to CatchDepth-1 then push the re-raised context (same values, new origin);
//         // unhandled: TERMINAL for the frame even when healthy (THROW is
//         // batch-aborting natively, verified). Never Advance a Rethrow.
//   }
//
//   // M3 fault handling around ExecuteUnit/EvaluatePredicate/initializers (§10.3):
//   //   ok=0 control row → build ErrorContext (line mapping §10.2) →
//   //     cursor.RouteError():
//   //       true  → trim ErrorContextStack to cursor.CatchDepth-1, push context,
//   //               shadows.SetErrorContext(top) + shadows.ObserveFault(number)
//   //               (fact 18: @@ERROR = number, @@ROWCOUNT = 0 at CATCH entry);
//   //       false → unhandled: doomed (xact_state -1) = terminal; healthy = native
//   //               statement-level continuation (facts 18/21) →
//   //               cursor.ContinueAfterUnhandledFault() (faulted predicates take the
//   //               FALSE path; RETURN faults complete the frame with 0 — session side).
//   //   After EVERY cursor mutation (steps, jumps, routes): reconcile — pop
//   //   ErrorContextStack down to cursor.CatchDepth (END CATCH / GOTO / BREAK /
//   //   CONTINUE / RETURN departures all pop this way, §10.2).
//   // Stops: publish at cursor.Current after every Advance (M1 shape unchanged).
//   // continue: loop without publishing, checking Current against the §13-verified
//   // breakpoint set BEFORE performing its action (a breakpoint on a WHILE line hits on
//   // every predicate re-evaluation — each is a real visit of that unit).
//   // Conditional breakpoints (§13): parse the condition string standalone → the SAME
//   // BuildForPredicate shell — but a breakpoint condition is DEBUGGER-initiated and
//   // invisible to the debuggee: do NOT call ObservePredicateEvaluation for it (only
//   // debuggee IF/WHILE predicates reset shadows, fact 12). Same rule for watch/hover/
//   // logpoint evals later (§12.3/§12.4 side-effect-free convention).
//   // "Jump to Cursor" (§13): map the gotoTargets line via Index.TryMapBreakpointLine,
//   // then cursor.JumpTo(unit) — stops ON the target without executing anything. M3
//   // must add §13's TRY-nesting policy check before allowing goto across TRY
//   // boundaries (unreachable in M2: TryCatchStatement is milestone-gated).
//
// The cursor NEVER touches IStatementExecutor — it is a pure state machine (DESIGN §3,
// §20.2: unit-testable with a fake driver).
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace TsqlDbg.Core.Interpreter;

/// <summary>
/// Interpretation phase of an entry-stack node (DESIGN §6: "an IfStatement entry has
/// phases EvalPredicate → InThen | InElse → Done"; "WhileStatement: EvalPredicate →
/// InBody → back to EvalPredicate"). M2 wires the IF/WHILE members; InTryBlock/
/// InCatchBlock remain the committed M3 shape.
/// </summary>
public enum EntryPhase
{
    Iterating,
    // M2 (§6): IF/WHILE phase machines.
    EvaluatingIfPredicate, InThenBranch, InElseBranch,
    EvaluatingWhilePredicate, InWhileBody,
    // M3 (§10): produced by TRY/CATCH interpretation + routing.
    InTryBlock, InCatchBlock,
}

/// <summary>
/// §10.3/§11.5: the outcome of <see cref="ExecutionCursor.RouteError"/>. The session needs more than
/// "did it route" — a route into an EMPTY CATCH (no stoppable statement) runs the cursor STRAIGHT
/// THROUGH END CATCH, handling the error vacuously, and must NOT be treated like a stop inside a CATCH
/// (no context push, the shadow @@ERROR/@@ROWCOUNT zero on the transit, and the cursor may have
/// completed the body). Only the cursor can tell the two apart reliably — a frame-level CatchDepth
/// delta is ambiguous when routing truncated intervening CATCH occupancies (e.g. a bare THROW
/// re-raising out of one CATCH into an outer empty one).
/// </summary>
public enum RouteOutcome
{
    /// <summary>No eligible (armed) TRY in this frame — cursor UNCHANGED. The old <c>false</c>.</summary>
    NoEligibleCatch,

    /// <summary>Routed and STOPPED inside the CATCH (its first stoppable statement). The old <c>true</c>
    /// for a non-empty CATCH.</summary>
    EnteredCatch,

    /// <summary>Routed into an EMPTY CATCH and ran THROUGH END CATCH — the error is handled vacuously.
    /// The cursor is now either COMPLETED (empty CATCH last in the body) or on the first statement AFTER
    /// END CATCH (continuation). The session applies the §10.3/§11.5 empty-CATCH transit.</summary>
    TransitedEmptyCatch,
}

/// <summary>What the driver reports back to move the cursor.</summary>
public abstract record AdvanceSignal
{
    private AdvanceSignal() { }

    /// <summary>The current unit was performed; move to the next stop.</summary>
    public static readonly AdvanceSignal Normal = new NormalSignal();
    public sealed record NormalSignal : AdvanceSignal;

    /// <summary>Outcome of an <see cref="InterpreterAction.EvaluatePredicate"/> round
    /// trip (§6). Required — and only valid — while stopped on an IF/WHILE unit.</summary>
    public sealed record PredicateEvaluated(bool Value) : AdvanceSignal;

    // M3 note: fault routing did NOT become an Advance signal — a fault interrupts an
    // action mid-flight (ExecuteUnit/EvaluatePredicate/initializer), it doesn't answer
    // one. It is a separate cursor operation: ExecutionCursor.RouteError() (routed →
    // first CATCH statement; false → unhandled, cursor unchanged) and
    // ContinueAfterUnhandledFault() for native statement-level continuation (facts 18/21).
}

/// <summary>What performing the current unit means. The cursor yields intent; the driver owns effects.</summary>
public abstract record InterpreterAction
{
    private InterpreterAction() { }

    /// <summary>Send the unit through the composed-batch pipeline (§7.1).</summary>
    public sealed record ExecuteUnit(StatementUnit Unit) : InterpreterAction;

    /// <summary>
    /// Interpreted DECLARE (§7.2/§8.2 as amended by fact 14 hoisting): declarations were
    /// registered and their state-table columns created at frame init; performing this
    /// action means running each non-null <see cref="VariableDeclaration.InitializerSql"/>
    /// as a synthetic <c>SET {Name} = {InitializerSql}</c> through the composed-batch
    /// pipeline — every time the SU executes (fact 14 case C: a re-reached DECLARE
    /// re-runs only its initializer).
    /// </summary>
    public sealed record DeclareVariables(StatementUnit Unit, IReadOnlyList<VariableDeclaration> Declarations) : InterpreterAction;

    /// <summary>M2 (§6): evaluate an IF/WHILE predicate through the normal pipeline
    /// (ComposedBatchBuilder.BuildForPredicate — rewritten, error-wrapped, faultable)
    /// and answer with <see cref="AdvanceSignal.PredicateEvaluated"/>.</summary>
    public sealed record EvaluatePredicate(StatementUnit Unit, BooleanExpression Predicate) : InterpreterAction;

    /// <summary>M2 (§6): GOTO/BREAK/CONTINUE — pure cursor bookkeeping, no server work;
    /// Advance(Normal) makes the cursor perform the jump itself.</summary>
    public sealed record Jump(StatementUnit Unit) : InterpreterAction;

    /// <summary>M2 (§6): RETURN — the driver evaluates <see cref="Expression"/> (when
    /// present) via ComposedBatchBuilder.BuildForScalarEval into the frame's __ret, then
    /// Advance(Normal) completes the frame (frame 0 → session end; §11.5 pop is M4).</summary>
    public sealed record ReturnFromFrame(StatementUnit Unit, ScalarExpression? Expression) : InterpreterAction;

    /// <summary>M2 (§6): WAITFOR DELAY/TIME — the driver skips (Debug Console note) or
    /// honors (BuildForUnit) per launch config <c>waitfor</c>, then Advance(Normal).</summary>
    public sealed record WaitFor(StatementUnit Unit) : InterpreterAction;

    /// <summary>M3 (§10.2): bare <c>;THROW</c> — re-raise the ACTIVE error context.
    /// Interpreted client-side (the saved context IS the exact original error — no
    /// server round trip, no re-materialization infidelity): the driver builds the
    /// re-raised context from the top of its ErrorContextStack and calls
    /// <see cref="ExecutionCursor.RouteError"/>; never Advance. Routing naturally starts
    /// from the enclosing scope because the rethrow's own CATCH entry is in
    /// <see cref="EntryPhase.InCatchBlock"/> and therefore ineligible (§10.3 step 2) —
    /// while a TRY *nested inside* the CATCH is eligible, matching the engine (verified:
    /// a bare THROW inside such a TRY is caught by its own CATCH).</summary>
    public sealed record Rethrow(StatementUnit Unit) : InterpreterAction;

    /// <summary>M4 (§9/R1, D7): <c>DECLARE @t TABLE (…)</c> — a stoppable no-op. The
    /// #temp realization was created (hoisted) at frame init/push, exactly like scalar
    /// declarations join the state table at init (fact 14: DECLARE is compile-time, and
    /// table variables have no initializer syntax, so reaching the SU performs nothing).
    /// The driver just Advance(Normal)s.</summary>
    public sealed record TableVarDeclare(StatementUnit Unit) : InterpreterAction;
}

/// <summary>
/// DESIGN §6: explicit stack of (node, phase, childIndex) entries, advanced by driver
/// requests — no blocking recursion. The entry stack encodes a purely LEXICAL position
/// (verified: fact 11 — the engine's own jump semantics carry no dynamic state either),
/// which is what makes GOTO / Jump-to-Cursor a full-stack rebuild from a static path.
/// BEGIN…END and labels are never stops. DESIGN §6's per-frame LoopStack is realized by
/// the While entries already on this stack (no parallel structure to keep in sync), and
/// — M3, same argument — §6's per-frame TryContextStack is realized by the Try entries
/// (Phase InTryBlock = armed, InCatchBlock = consumed; §10.3's eligibility rule falls
/// out of the phase itself). The label map lives on
/// <see cref="StatementIndex.ControlFlow"/>. Single-threaded by contract: the adapter
/// serializes all access (DESIGN §3 threading model).
/// </summary>
public sealed class ExecutionCursor
{
    private sealed class Entry
    {
        public IList<TSqlStatement> Children = Array.Empty<TSqlStatement>();
        public EntryPhase Phase = EntryPhase.Iterating;
        public TSqlFragment? Node;                            // owning control node (null for the root)
        public int ChildIndex = -1;                           // -1 = before first child
    }

    private readonly List<Entry> _stack = new();              // index 0 = root; end = innermost
    private StatementUnit? _current;

    /// <summary>Flat SU index in source order — §13 breakpoint mapping, trace, label map.</summary>
    public StatementIndex Index { get; }

    private ExecutionCursor(StatementIndex index, IList<TSqlStatement> body)
    {
        Index = index;
        _stack.Add(new Entry { Children = body, Phase = EntryPhase.Iterating });
        MoveToNextStop();
    }

    /// <summary>
    /// Validates the whole body — engine-parity compile diagnostics first
    /// (<see cref="ParseTimeDiagnosticException"/>, facts 13/14), then milestone gates
    /// (<see cref="MilestoneNotSupportedException"/>) — builds the index, and positions
    /// the cursor on the first stoppable unit (stopOnEntry semantics:
    /// <see cref="Current"/> before any <see cref="Advance"/> is the first statement).
    /// <paramref name="preDeclaredVariables"/>: the frame's parameters (visible from
    /// offset 0 for the fact-14 use-before-declare check).
    /// </summary>
    public static ExecutionCursor Create(
        IList<TSqlStatement> body, string fullScript,
        FrameKind frameKind = FrameKind.Procedure, IEnumerable<string>? preDeclaredVariables = null)
    {
        var index = StatementIndex.Build(body, fullScript, frameKind, preDeclaredVariables);
        return new ExecutionCursor(index, body);
    }

    /// <summary>The unit the cursor is stopped ON (about to perform); null once completed.</summary>
    public StatementUnit? Current => _current;

    public bool IsCompleted => _current is null;

    /// <summary>A54 (§6/§11.5): true when this cursor completed via an EXPLICIT <c>RETURN</c>
    /// (the Advance Return case cleared the stack), false when it completed by body
    /// EXHAUSTION (ran off the end). Read only once <see cref="IsCompleted"/> — it lets the
    /// driver park a body-end pop at the implicit-return stop but NOT an explicit RETURN,
    /// which is already a stoppable line. Default false; set only in the Return case.</summary>
    public bool CompletedByExplicitReturn { get; private set; }

    /// <summary>
    /// M3 (§10.2): the number of CATCH blocks the cursor is currently inside. The
    /// session's ErrorContextStack is kept aligned with this after every cursor
    /// mutation (route pushes; END CATCH / GOTO / BREAK / CONTINUE / RETURN / JumpTo
    /// departures pop) — entries and contexts are both LIFO over the same lexical
    /// regions, so a simple count reconcile is exact.
    /// </summary>
    public int CatchDepth
    {
        get
        {
            var n = 0;
            foreach (var e in _stack)
            {
                if (e.Phase == EntryPhase.InCatchBlock) n++;
            }

            return n;
        }
    }

    /// <summary>Intent of performing <see cref="Current"/>. Idempotent; throws when completed.</summary>
    public InterpreterAction Peek()
    {
        var u = _current ?? throw new InvalidOperationException("Cursor is completed; nothing to peek.");
        return u.Kind switch
        {
            SuKind.Declare when u.SubKind == SuSubKind.TableVarDeclare
                => new InterpreterAction.TableVarDeclare(u),
            SuKind.Declare => new InterpreterAction.DeclareVariables(
                u, VariableDeclaration.Extract((DeclareVariableStatement)u.Fragment, Index.FullScript)),
            SuKind.Executable => new InterpreterAction.ExecuteUnit(u),
            SuKind.Control => u.SubKind switch
            {
                SuSubKind.If => new InterpreterAction.EvaluatePredicate(u, ((IfStatement)u.Fragment).Predicate),
                SuSubKind.While => new InterpreterAction.EvaluatePredicate(u, ((WhileStatement)u.Fragment).Predicate),
                SuSubKind.Goto or SuSubKind.Break or SuSubKind.Continue => new InterpreterAction.Jump(u),
                SuSubKind.Return => new InterpreterAction.ReturnFromFrame(u, ((ReturnStatement)u.Fragment).Expression),
                SuSubKind.WaitFor => new InterpreterAction.WaitFor(u),
                SuSubKind.Rethrow => new InterpreterAction.Rethrow(u),
                _ => throw new InvalidOperationException($"Control unit with unexpected subkind {u.SubKind} — internal bug."),
            },
            _ => throw new InvalidOperationException($"Cursor stopped on non-stoppable kind {u.Kind} — internal bug."),
        };
    }

    /// <summary>Moves past <see cref="Current"/> according to the driver's signal.</summary>
    public void Advance(AdvanceSignal signal)
    {
        var current = _current ?? throw new InvalidOperationException("Cursor is completed; cannot advance.");
        switch (signal)
        {
            case AdvanceSignal.NormalSignal:
                switch (current.SubKind)
                {
                    case SuSubKind.If or SuSubKind.While:
                        throw new InvalidOperationException(
                            "Stopped on an IF/WHILE predicate — answer with PredicateEvaluated, not Normal (§6).");
                    case SuSubKind.Rethrow:
                        throw new InvalidOperationException(
                            "Bare THROW re-raises the active error context — route it via RouteError (§10.2/§10.3), not Advance(Normal).");
                    case SuSubKind.Goto:
                        PerformGoto((GoToStatement)current.Fragment);
                        break;
                    case SuSubKind.Break:
                        PerformBreak();
                        break;
                    case SuSubKind.Continue:
                        PerformContinue();
                        break;
                    case SuSubKind.Return:
                        // §6: RETURN unwinds the frame; frame 0 → session end (normal).
                        // M4's §11.5 pop keys off the same completion (the driver saw
                        // ReturnFromFrame before advancing, so it knows why).
                        _stack.Clear();
                        _current = null;
                        CompletedByExplicitReturn = true;   // A54: an explicit RETURN is never parked (§6)
                        break;
                    default:
                        MoveToNextStop();
                        break;
                }

                break;

            case AdvanceSignal.PredicateEvaluated evaluated:
                AdvancePredicate(current, evaluated.Value);
                break;

            default:
                throw new NotSupportedException($"Unknown advance signal {signal.GetType().Name}.");
        }
    }

    /// <summary>
    /// §13's Jump-to-Cursor nesting policy: allowed only to targets whose enclosing
    /// TRY/CATCH regions are EXACTLY the current position's (same statements, same
    /// sides) — jumping within a region is fine; teleporting into or out of one is
    /// refused with a message (the adapter surfaces it; GOTO's leave-is-legal rule does
    /// not apply to a debugger teleport, which §13 words as "not inside a *different*
    /// TRY nesting than current").
    /// </summary>
    public bool CanJumpTo(StatementUnit target, out string? refusalReason)
    {
        if (!Index.ControlFlow.TryGetPath(target.Fragment, out var targetPath))
        {
            refusalReason = "Target is not part of this frame body.";
            return false;
        }

        // Current signature from the live entry stack (exact even mid-CATCH after
        // routing); target signature from its static path — both are the same lexical
        // vocabulary (fact 11 / D5).
        var current = new List<(TSqlFragment Owner, bool IsCatch)>();
        foreach (var e in _stack)
        {
            if (e.Phase == EntryPhase.InTryBlock) current.Add((e.Node!, false));
            else if (e.Phase == EntryPhase.InCatchBlock) current.Add((e.Node!, true));
        }

        var wanted = new List<(TSqlFragment Owner, bool IsCatch)>();
        foreach (var step in targetPath)
        {
            if (step.Kind == PathContainerKind.TryBlock) wanted.Add((step.Owner!, false));
            else if (step.Kind == PathContainerKind.CatchBlock) wanted.Add((step.Owner!, true));
        }

        if (current.Count != wanted.Count
            || !current.Zip(wanted).All(p => ReferenceEquals(p.First.Owner, p.Second.Owner) && p.First.IsCatch == p.Second.IsCatch))
        {
            refusalReason = "Jump to Cursor cannot enter or leave a TRY/CATCH scope (§13): the target is inside a " +
                            "different TRY nesting than the current statement.";
            return false;
        }

        refusalReason = null;
        return true;
    }

    /// <summary>
    /// §13 "Jump to Cursor": move the cursor to <paramref name="target"/> WITHOUT
    /// executing anything — the next <see cref="Peek"/> is the target's own action (an
    /// IF/WHILE target stops at its predicate eval). Shares the GOTO reconstruction.
    /// Enforces the §13 TRY-nesting policy (<see cref="CanJumpTo"/>) — the adapter
    /// should pre-check to phrase its own refusal, but the cursor is the backstop.
    /// </summary>
    public void JumpTo(StatementUnit target)
    {
        if (!CanJumpTo(target, out var refusalReason))
            throw new InvalidOperationException(refusalReason);
        if (!Index.ControlFlow.TryGetPath(target.Fragment, out var path))
            throw new ArgumentException("Target unit is not part of this frame body.", nameof(target));
        RebuildStackFromPath(path);
        if (target.SubKind is SuSubKind.If or SuSubKind.While)
        {
            // Re-create the exact invariants of a natural encounter (predicate pending).
            _stack.Add(new Entry
            {
                Phase = target.SubKind == SuSubKind.If ? EntryPhase.EvaluatingIfPredicate : EntryPhase.EvaluatingWhilePredicate,
                Node = target.Fragment,
            });
        }

        _current = target;
    }

    /// <summary>
    /// §10.3 routing, cursor half: an error is escaping the CURRENT position (a faulted
    /// unit, a faulted predicate eval, or a bare-THROW re-raise — identical mechanics).
    /// Finds the innermost enclosing TRY whose CATCH is not already occupied — an entry
    /// in <see cref="EntryPhase.InCatchBlock"/> IS the occupied case (§10.3 step 2:
    /// consumed on CATCH entry, eligible again only after END CATCH; fact 6's
    /// outer-scope behavior) — so eligibility is simply Phase == InTryBlock.
    /// Routed: the stack truncates to that entry, flips it to the CATCH side, and the
    /// cursor stops on the first CATCH statement — UNLESS the CATCH is empty (no stoppable
    /// statement), in which case MoveToNextStop pops the flipped entry and the cursor runs
    /// THROUGH END CATCH (completing the body or landing on the statement after it). The
    /// return value distinguishes those (<see cref="RouteOutcome"/>): the session must NOT
    /// treat an empty-CATCH transit like a stop inside a CATCH (§10.3/§11.5 empty-CATCH
    /// transit — no context push, shadow zeroed). <see cref="RouteOutcome.NoEligibleCatch"/>
    /// leaves the cursor UNCHANGED; the session decides between native statement-level
    /// continuation and terminal outcomes (§10.3 step 4; caller-frame unwind is M4's
    /// §11.5-abnormal).
    /// </summary>
    public RouteOutcome RouteError()
    {
        if (_current is null) throw new InvalidOperationException("Cursor is completed; no position to route from.");

        for (var i = _stack.Count - 1; i >= 0; i--)
        {
            if (_stack[i].Phase != EntryPhase.InTryBlock) continue;
            // NOTE: HasArmedTry below must stay in lockstep with this eligibility
            // predicate — D5's oracle-free decision (§10.1) is exactly "RouteError
            // would return NoEligibleCatch in every frame".

            var tryCatch = (TryCatchStatement)_stack[i].Node!;
            _stack.RemoveRange(i + 1, _stack.Count - i - 1);
            var entry = _stack[i];
            entry.Phase = EntryPhase.InCatchBlock;
            entry.Children = tryCatch.CatchStatements.Statements;
            entry.ChildIndex = -1;
            MoveToNextStop();
            // An EMPTY CATCH (no stoppable statement) is popped by MoveToNextStop, so the
            // flipped entry no longer occupies the stack: the route ran THROUGH END CATCH.
            // A non-empty CATCH leaves the cursor stopped inside it (the entry survives —
            // even if the first stop is a nested descent, THIS entry stays InCatchBlock).
            var enteredCatch = _stack.Any(e => e.Phase == EntryPhase.InCatchBlock && ReferenceEquals(e.Node, tryCatch));
            return enteredCatch ? RouteOutcome.EnteredCatch : RouteOutcome.TransitedEmptyCatch;
        }

        return RouteOutcome.NoEligibleCatch;
    }

    /// <summary>D5/A13 (§10.1): non-mutating armed-TRY probe — true iff
    /// <see cref="RouteError"/> called right now would route in THIS frame. Same
    /// predicate as RouteError's search (Phase == InTryBlock; an occupied CATCH is
    /// InCatchBlock and therefore correctly NOT armed), so eligibility and routing can
    /// never disagree. The session ORs this across all frames to decide whether a
    /// stepped-over EXEC composes oracle-free (no armed TRY anywhere = native
    /// semantics are fact 23-H continuation, not transfer).</summary>
    public bool HasArmedTry => _stack.Any(e => e.Phase == EntryPhase.InTryBlock);

    /// <summary>
    /// M3 (§10.3 step 4 + facts 18/21): an unhandled STATEMENT-LEVEL error does not end
    /// the batch natively — execution resumes (RAISERROR 16 with no TRY was verified to
    /// continue). A faulted IF/WHILE predicate takes the FALSE path (fact 21 P1/P6,
    /// probed live at the §10 line review: the ELSE branch RUNS after the error — it is
    /// NOT a skip of the whole conditional; a WHILE exits); every other unit is skipped,
    /// resuming at the next stop. RETURN-expression faults are the session's special
    /// case (the frame completes with status 0, fact 21 P8) and never reach here.
    /// Never call for batch-aborting classes (doomed / THROW / no-control-row) — those
    /// are terminal for the frame (session's call).
    /// </summary>
    public void ContinueAfterUnhandledFault()
    {
        var current = _current ?? throw new InvalidOperationException("Cursor is completed; nothing to continue past.");
        if (_stack.Count > 0
            && ReferenceEquals(_stack[^1].Node, current.Fragment)
            && _stack[^1].Phase is EntryPhase.EvaluatingIfPredicate or EntryPhase.EvaluatingWhilePredicate)
        {
            AdvancePredicate(current, false);
            return;
        }

        MoveToNextStop();
    }

    /// <summary>
    /// DESIGN §14/A21 (B8) — successful boosted subtree: the whole IF/WHILE node ran
    /// natively to completion in one batch, so the cursor moves to AFTER the node.
    /// The dual of the predicate-entry push a natural arrival (or JumpTo) performs:
    /// valid only while stopped ON an IF/WHILE unit with its predicate pending; pops
    /// that entry and advances to the next natural stop (which may exhaust the frame
    /// — the session's completion settle handles the pop cascade).
    /// </summary>
    public void CompleteSubtree()
    {
        var current = _current ?? throw new InvalidOperationException("Cursor is completed; no subtree to complete.");
        if (_stack.Count == 0
            || !ReferenceEquals(_stack[^1].Node, current.Fragment)
            || _stack[^1].Phase is not (EntryPhase.EvaluatingIfPredicate or EntryPhase.EvaluatingWhilePredicate))
        {
            throw new InvalidOperationException(
                "CompleteSubtree is only valid while stopped ON an IF/WHILE node with its predicate pending (§14/A21 B8).");
        }

        _stack.RemoveAt(_stack.Count - 1);
        MoveToNextStop();
    }

    /// <summary>
    /// DESIGN §14/A21 (B7) — boosted attention / no-control-row recovery: rebuild the
    /// stack to the point immediately AFTER <paramref name="statement"/> (the list
    /// child a persisted boost marker follows) and advance to the next natural stop.
    /// For a marker at a loop-body end this lands on the predicate re-evaluation —
    /// exactly the native continuation. The rebuilt path can only re-enter TRY/CATCH
    /// scopes the boosted node already occupied (an eligible subtree contains none of
    /// its own — B3), so no §13-style nesting gate is needed here.
    /// </summary>
    public void ResumeAfter(TSqlStatement statement)
    {
        if (!Index.ControlFlow.TryGetPath(statement, out var path))
        {
            throw new ArgumentException("Statement is not part of this frame body.", nameof(statement));
        }

        RebuildStackFromPath(path);
        MoveToNextStop();
    }

    // ---------------------------------------------------------------------------------
    // Phase machines (§6).
    // ---------------------------------------------------------------------------------

    private void AdvancePredicate(StatementUnit current, bool value)
    {
        var top = _stack.Count > 0 ? _stack[^1] : null;
        if (top is null || !ReferenceEquals(top.Node, current.Fragment))
            throw new InvalidOperationException(
                "PredicateEvaluated is only valid while stopped on an IF/WHILE unit (§6).");

        switch (top.Phase)
        {
            case EntryPhase.EvaluatingIfPredicate:
                var ifStatement = (IfStatement)current.Fragment;
                if (value)
                    EnterBranch(top, ifStatement.ThenStatement, EntryPhase.InThenBranch);
                else if (ifStatement.ElseStatement is { } elseStatement)
                    EnterBranch(top, elseStatement, EntryPhase.InElseBranch);
                else
                    _stack.RemoveAt(_stack.Count - 1);        // no branch taken: resume after the IF
                MoveToNextStop();
                break;

            case EntryPhase.EvaluatingWhilePredicate:
                if (value)
                    EnterBranch(top, ((WhileStatement)current.Fragment).Statement, EntryPhase.InWhileBody);
                else
                    _stack.RemoveAt(_stack.Count - 1);        // loop exits: resume after the WHILE
                MoveToNextStop();
                break;

            default:
                throw new InvalidOperationException(
                    $"PredicateEvaluated received while the top entry is in phase {top.Phase} — internal bug.");
        }
    }

    private static void EnterBranch(Entry entry, TSqlStatement branch, EntryPhase phase)
    {
        entry.Children = new TSqlStatement[] { branch };      // BEGIN…END unwraps via normal descent
        entry.ChildIndex = -1;                                // fresh pass (also clears a stale index on WHILE re-entry / CONTINUE)
        entry.Phase = phase;
    }

    // ---------------------------------------------------------------------------------
    // The descent loop. M3 slots in exactly here: TryCatchStatement pushes
    // Entry{Node=tryCatch, Phase=InTryBlock} over TryStatements with no stop of its own
    // (like Structural), and §10.3 routing rebuilds to the CATCH side.
    // ---------------------------------------------------------------------------------
    private void MoveToNextStop()
    {
        while (true)
        {
            if (_stack.Count == 0) { _current = null; return; }   // frame body exhausted (§6: frame done)

            var top = _stack[^1];
            top.ChildIndex++;
            if (top.ChildIndex >= top.Children.Count)
            {
                if (top.Phase == EntryPhase.InWhileBody)
                {
                    // §6: "WHILE re-enters EvaluatingWhilePredicate after its body
                    // exhausts instead of popping" — each re-evaluation is a visible stop
                    // on the WHILE line. Also the rejoin point for GOTO-entered
                    // iterations (fact 11: landing in a body via GOTO skips the predicate;
                    // looping resumes here, at the natural body end).
                    top.Phase = EntryPhase.EvaluatingWhilePredicate;
                    _current = UnitOf(top.Node!);
                    return;
                }

                // Exhausted block / taken IF branch — and the M3 phases: an InTryBlock
                // pop is a fault-free TRY completing (its CATCH never runs, §10.3); an
                // InCatchBlock pop is END CATCH (the session reconciles its
                // ErrorContextStack against CatchDepth after every step — §10.2's
                // "END CATCH pops it").
                _stack.RemoveAt(_stack.Count - 1);
                continue;
            }

            var child = top.Children[top.ChildIndex];
            var c = SuClassifier.Classify(child);
            switch (c.Kind)
            {
                case SuKind.Structural when c.SubKind == SuSubKind.TryCatch:
                    // §6/§10: entering TRY is silent — BEGIN TRY does no work (unlike
                    // IF/WHILE there is no predicate step). The CATCH side is reachable
                    // only via RouteError (§10.3); a fault-free TRY pops through the
                    // default exhaustion path and the CATCH never runs.
                    _stack.Add(new Entry
                    {
                        Children = ((TryCatchStatement)child).TryStatements.Statements,
                        Phase = EntryPhase.InTryBlock,
                        Node = child,
                    });
                    continue;

                case SuKind.Structural:                       // transparent BEGIN…END descent
                    _stack.Add(new Entry
                    {
                        Children = ((BeginEndBlockStatement)child).StatementList.Statements,
                        Phase = EntryPhase.Iterating,
                        Node = child,
                    });
                    continue;

                case SuKind.Label:                            // no-op jump target: never a stop (§6)
                    continue;

                case SuKind.Control when c.SubKind == SuSubKind.If:
                    _stack.Add(new Entry { Phase = EntryPhase.EvaluatingIfPredicate, Node = child });
                    _current = UnitOf(child);
                    return;

                case SuKind.Control when c.SubKind == SuSubKind.While:
                    _stack.Add(new Entry { Phase = EntryPhase.EvaluatingWhilePredicate, Node = child });
                    _current = UnitOf(child);
                    return;

                case SuKind.Control:                          // Goto/Break/Continue/Return/WaitFor
                case SuKind.Executable:
                case SuKind.Declare:
                    _current = UnitOf(child);
                    return;

                case SuKind.Unsupported:
                    throw new InvalidOperationException(
                        $"Gated statement reached the cursor ({child.GetType().Name}) — validation should have refused. Internal bug.");
            }
        }
    }

    // ---------------------------------------------------------------------------------
    // Jumps. Full-stack reconstruction from a static lexical path is faithful because
    // the engine's control-flow position is itself purely lexical (fact 11); jumping out
    // of a loop dissolves its entry, jumping into a body lands with no predicate check.
    // ---------------------------------------------------------------------------------

    private void PerformGoto(GoToStatement gotoStatement)
    {
        // Validation guaranteed the label exists (engine 133) and that this jump enters
        // no TRY/CATCH scope it isn't already in (engine 1026) — fact 13.
        var key = ControlFlowMap.LabelKey(gotoStatement.LabelName.Value);
        var target = Index.ControlFlow.Labels[key];
        RebuildStackFromPath(target.Path);
        // The label itself is a no-op (never a stop): the stop after a GOTO is the first
        // stoppable unit past the label — or, when the label ends a WHILE body / the
        // frame, the loop's predicate re-eval / session end via normal exhaustion.
        MoveToNextStop();
    }

    private void PerformBreak()
    {
        var loopIndex = InnermostWhileEntry();
        _stack.RemoveRange(loopIndex, _stack.Count - loopIndex);   // pop the loop and everything inside it
        MoveToNextStop();                                          // parent resumes after the WHILE
    }

    private void PerformContinue()
    {
        var loopIndex = InnermostWhileEntry();
        _stack.RemoveRange(loopIndex + 1, _stack.Count - loopIndex - 1);
        var loop = _stack[loopIndex];
        loop.Phase = EntryPhase.EvaluatingWhilePredicate;          // §6: CONTINUE → predicate re-eval stop
        _current = UnitOf(loop.Node!);
    }

    private int InnermostWhileEntry()
    {
        for (var i = _stack.Count - 1; i >= 0; i--)
        {
            if (_stack[i].Node is WhileStatement) return i;
        }

        throw new InvalidOperationException(
            "BREAK/CONTINUE with no enclosing WHILE entry — validation should have refused (engine 135/136). Internal bug.");
    }

    private void RebuildStackFromPath(IReadOnlyList<PathStep> path)
    {
        // Jumps in/out of TRY scopes route through here. Entering a scope the jumper is
        // not already in was refused up front (GOTO: engine 1026 at validation; JumpTo:
        // the §13 nesting check) — so any TryBlock/CatchBlock step rebuilt here is a
        // region the cursor legitimately occupies. Leaving a CATCH this way drops
        // CatchDepth, and the session's reconcile pops the corresponding error contexts
        // (§10.2: "leaving via GOTO/RETURN pops it").
        _stack.Clear();
        foreach (var step in path)
        {
            _stack.Add(new Entry
            {
                Children = step.List,
                ChildIndex = step.Index,                       // points AT the child we are inside/at
                Node = step.Owner,
                Phase = step.Kind switch
                {
                    PathContainerKind.Root or PathContainerKind.Block => EntryPhase.Iterating,
                    PathContainerKind.IfThen => EntryPhase.InThenBranch,
                    PathContainerKind.IfElse => EntryPhase.InElseBranch,
                    PathContainerKind.WhileBody => EntryPhase.InWhileBody,
                    PathContainerKind.TryBlock => EntryPhase.InTryBlock,
                    PathContainerKind.CatchBlock => EntryPhase.InCatchBlock,
                    _ => throw new InvalidOperationException($"Unknown path container kind {step.Kind}."),
                },
            });
        }
    }

    private StatementUnit UnitOf(TSqlFragment fragment)
    {
        if (fragment is TSqlStatement statement && Index.TryGetUnit(statement, out var unit)) return unit;
        throw new InvalidOperationException("Statement not present in index — walk orders diverged. Internal bug.");
    }
}
