using System.Globalization;
using System.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using TsqlDbg.Core.Batches;
using TsqlDbg.Core.Execution;
using TsqlDbg.Core.Interpreter;
using TsqlDbg.Core.Parsing;
using TsqlDbg.Core.Rewrite;
using TsqlDbg.Core.State;
using TsqlDbg.Core.Tracing;

namespace TsqlDbg.Core.Sessions;

// DESIGN §4 "Session init sequence" + §6 interpreter loop + §7.1 composed batches +
// §8 state table, M1 slice per DESIGN.md §22: "Composed batch §7.1, state table,
// shadows R4-R6 ... next/continue". Frame 0 is interpreted statement-by-statement
// through the real per-SU pipeline (Phase-0's ExecutionCursor/FrameStack + Sonnet's
// ComposedBatchBuilder/StateTableDdlBuilder) — this replaces M0's single
// `EXEC <proc> <args>` round trip (see docs/archive/phase0-integration-log.md).
//
// M1 fault handling (decision on record, phase0-integration-notes.md): any ok=0
// control row or SqlException is session-fatal (§10 routing is M3) — the whole run
// aborts and rolls back. stopOnEntry/next/continue/breakpoints (the DAP-facing half
// of M1) are driven by the Adapter calling Peek()/Advance() one step at a time
// against a live Session; RunToEndAsync itself is the "continue with no stops"
// degenerate case, used directly by the fidelity harness and by launches where
// stopOnEntry is off end-to-end.
//
// DESIGN §3: Session takes an already-connected IStatementExecutor rather than
// opening a SqlConnection itself (real-server plumbing lives in SessionHost), so it
// stays exercisable by FakeStatementExecutor in tests/TsqlDbg.Core.Tests.
public sealed class Session
{
    private readonly SessionOptions _options;
    private readonly ITraceSink _trace;
    private readonly IStatementExecutor _executor;
    private readonly string _nonce;

    private FrameStack? _frames;                                  // M4 (§6/§11): the virtual call stack
    private FrameChainNameScope? _tempNameScope;                  // M5 I6/I7: concrete handle for EvaluationFrameOrdinal
    private RewriteEngine? _rewriteEngine;
    private RewriteContext? _rewriteContext;
    private ShadowValues? _shadows;
    private bool _initialized;

    // §7.4/A26 (D1): the server SCOPE_IDENTITY() chain no longer equals the current
    // frame's native scope chain. Set at a frame pop (the server chain may hold the
    // callee's last value) and at doom entry (parameterized transport reads an
    // sp_executesql child scope — fact 26e); cleared when a completed insert-family SU
    // re-synchronizes both chains (fact 31b). While set, ObserveDebuggeeSuccess skips
    // the R6 capture and the shadow serves the client-modeled value; boost never
    // dispatches (§14). A frame PUSH does NOT set it — the plain push-seed INSERT's
    // fact-26d clobber makes the server chain NULL like native callee entry (§1.2).
    private bool _scopeChainPoisoned;

    // ---- M3 §10 error model + watchdog state (docs/archive/reviews/m3-error-model-design-notes-fable.md)
    private readonly List<ErrorContext> _errorContexts = new();   // §10.2, aligned with Σ Cursor.CatchDepth (M4: all frames)
    private bool _doomed;                                         // §10.4: XACT_STATE() = -1 observed
    private int _doomTrancount = 1;                               // §10.4/fact 22: trancount the redoom preamble re-establishes
    private bool _detached;                                       // §10.4 amended (fact 22): trancount hit 0; safety txn re-open deferred
    private int _lastObservedTrancount = 1;                       // §10.4: edge detection for the trancount 1+ → 0 transition
    private int _lastObservedXactState = 1;                       // M5 I3 (§12.1 System scope): the stop's own control-row XACT_STATE()
    private bool _broken;                                         // §10.3 step 4 terminal
    private (ErrorContextValues Values, Interpreter.StatementUnit Unit, int XactState)? _pendingFault;   // §10.6 'all'
    private readonly List<string> _launchWarnings = new();
    // A56 (§12.3/§15): the debugger's own cosmetic/navigational annotations — NOCOUNT
    // forced-ON (C5), "-- GO: entering batch k of N" (§5.4), untracked SET options
    // (§11.2), the DML-trigger heads-up (C2) — are ROUTED here instead of into the
    // debuggee `messages` stream, so the adapter can surface them only under
    // logLevel:"verbose". This is a presentation split, not a fidelity one: real
    // debuggee output (PRINT/results/errors), halt-explanations, logpoints, and §16
    // notices are NOT diagnostics and always show. RunToEndAsync folds these back into
    // Execution.Messages so the offline record stays complete (the fidelity harness
    // never reads Messages; §20.3 compares result-set projections only).
    private readonly List<string> _diagnosticNotes = new();

    // ---- §10.4 A14 pre-flight C23 diagnostic (ratified 2026-07-06 —
    // ---- docs/archive/reviews/m4-c23-doom-temp-severity-fable.md §4.3)
    private readonly List<TempObjectEntry> _doomedTempResolutions = new();  // hits recorded by the name scope during one debuggee composition
    private bool _captureDoomedTemps;                             // recording gate: debuggee compositions only (debugger-initiated evals must not arm stops)
    private Interpreter.StatementUnit? _armedC23Unit;             // first phase published for this SU; the next step executes anyway
    private IReadOnlyList<string>? _c23TerminalNote;              // original names riding to PerformRouteAsync's terminal message

    // ---- M4 frames state (docs/archive/reviews/m4-frames-design-notes-fable.md)
    private readonly RuntimeOptionTracker _runtimeOptions = new();          // D6: §11.2 pop restores
    private readonly Dictionary<string, ModuleBlueprint> _moduleCache =
        new(StringComparer.OrdinalIgnoreCase);                              // §11.4/§15: definition fetch cache

    // A58 (§11.6): every dynamic-SQL frame this session has pushed, keyed by the content hash in
    // its ModuleIdentity. Unlike a module, a dynamic frame's text exists NOWHERE else — not on
    // disk, not in the catalog — so it must be retained here or its read-only virtual document
    // goes blank the moment the frame pops (VS Code keeps the editor open). Never invalidated: the
    // key IS the content, so an entry can never go stale.
    private readonly Dictionary<string, StatementIndex> _dynamicIndexes =
        new(StringComparer.Ordinal);
    private bool _untrackedOptionNoted;                                     // D6 residual, console-noted once
    private bool? _cursorDefaultIsGlobal;                                   // R3: db CURSOR_DEFAULT, checked lazily
    private int _spid;                                                      // M5 I3: captured once at connection open
    private bool _noCountNoted;                                             // C5 (M7): one-time cosmetic note
    private readonly Dictionary<string, bool> _dmlTriggerCache =
        new(StringComparer.OrdinalIgnoreCase);                              // C2 (M7): DML target name -> has triggers

    // A59 (§4 step 2a / §8.1 / §9): the database's user-defined types, loaded on the init
    // round trip and refreshed after an executed CREATE TYPE / DROP TYPE — a script may
    // define, across a GO, the type a later batch declares. Table-type STRUCTURE is fetched
    // on first use and cached for the session (a type cannot change under a live session
    // without a DDL statement this session executed, which invalidates it).
    private UserTypeCatalog _userTypes = UserTypeCatalog.Empty;
    // C14 (§9): the connected database's default collation, read once at init (rides the §4 step-2a
    // round trip). A table variable's char columns take the DATABASE collation natively, but its
    // #temp realization would take tempdb's — so un-COLLATE'd char columns get this appended
    // explicitly (fact: table-variable columns inherit the DB collation, #temp columns tempdb's,
    // verified live). Null only when the init read did not supply it (scripted unit tests), where
    // the append is skipped and behavior is unchanged.
    private string? _databaseCollation;
    private readonly Dictionary<string, TableTypeDefinition> _tableTypeCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AliasBaseType> _aliasBaseTypes =
        new(StringComparer.OrdinalIgnoreCase);          // [schema].[type] -> base type + collation

    // ---- M8 (§5.4) multi-batch (GO) script-mode lifecycle (docs/archive/reviews/multibatch-script-design-notes-opus.md)
    private IReadOnlyList<FrameZeroBlueprint>? _batches;                    // one blueprint per batch ITERATION; procedure mode / single-batch script = one element. §5.4/A43: a `GO N` batch is MATERIALIZED N times here (an iteration = the next batch), so the whole boundary machinery below is reused unchanged
    private IReadOnlyList<BatchPosition>? _batchPositions;                  // §5.4/A43: parallel to _batches — the PHYSICAL orientation (which physical batch, which GO N iteration) for the stackTrace annotation, since materialized _batches conflate iterations
    private int _currentBatchIndex;                                        // index into _batches of the live batch frame (the stack-bottom frame)

    // §5.4 (A43): a single `GO N` above this refuses at launch — a step-debugger guard so a
    // pathological count cannot materialize an unbounded blueprint list. Not a native limit.
    private const int MaxGoRepeat = 10000;
    private readonly TempObjectRegistry _sessionTempObjects = new();        // §9 session-persistent tier: connection-scoped objects promoted across GO boundaries
    private bool _pendingBatchAdvance;                                     // §8.2/A35 (lane 1b): a BATCH-TERMINAL fault fired with more batches remaining — the fault-site stop was published (FrameFaulted), and the NEXT step/continue crosses the GO boundary instead of ending the session
    private bool _batchListDone;                                           // §8.3 (M8 FIX): every batch after the live one was compile-refused by the client-side validator (ParseTimeDiagnosticException/MilestoneNotSupportedException) — no runnable batch remains, so the script is COMPLETE (a completion, not a fault; prior batches' output stands). Always false in procedure mode / single-batch scripts
    private bool _pendingReturnStop;                                       // A54 (§6/§11.5): the top frame is a MODULE frame parked at its implicit return (body ran off the end, no explicit RETURN) — the frame is NOT yet popped; the next step consumes this (ConsumeReturnStopAsync) and performs the deferred §11.5 pop. `next`/`stepIn` stop here; `continue`/`stepOut`/boost/RunToEnd run through. Always exclusive with AtBatchBoundary. See docs/archive/reviews/implicit-return-stop-opus.md

    private Frame CurrentFrame => _frames?.Current
        ?? throw new InvalidOperationException("Session.InitializeAsync must complete first.");
    private Frame RootFrame => _frames!.All[0];

    // DESIGN §2 (A57): the resolved ScriptDom parser compat level. Initialized to the REQUESTED
    // value (_compatLevel; 0 = "auto"); InitializeAsync resolves 0 against the server's
    // product major version below, before the first parse. Every parse site reads THIS field,
    // never _compatLevel.
    private int _compatLevel;

    // DESIGN §2/§12.1 (A57): the server's product MAJOR version (e.g. 16), captured once from the
    // login response by LiveSession.OpenAsync — zero round trips, like _spid. 0 = unknown (a
    // FakeStatementExecutor unit test, or an unparseable version) → auto falls back to 150.
    private readonly int _serverMajorVersion;

    public Session(
        SessionOptions options, IStatementExecutor executor, ITraceSink? trace = null, string? nonce = null, int spid = 0, int serverMajorVersion = 0)
    {
        _options = options;
        _executor = executor;
        _trace = trace ?? NullTraceSink.Instance;
        _nonce = nonce ?? SqlConnectionStringFactory.NewNonce();
        // M5 I3 (§12.1 System scope): "@@SPID — captured once at connection open
        // (constant per connection)" — literally so: LiveSession.OpenAsync reads
        // SqlConnection.ServerProcessId (a property TDS returns at login, ZERO extra
        // round trips) and passes it here, rather than this class issuing its own
        // "SELECT @@SPID" query during InitializeAsync. Defaults to 0 for
        // FakeStatementExecutor-backed unit tests, which have no real connection.
        _spid = spid;
        _serverMajorVersion = serverMajorVersion;
        // §2 (A57): starts as the requested value (0 = auto); InitializeAsync resolves it.
        _compatLevel = options.CompatLevel;
    }

    // DESIGN §13 breakpoint mapping / §12.1 Locals / --trace SU listing all key off
    // this once initialized. Null before InitializeAsync completes. M4: these three
    // describe the TOP frame — per-frame access goes through Frames.
    public StatementIndex? Index => _frames?.Current.Cursor.Index;

    // DESIGN §6: "Current before any Advance is the first statement" — the
    // stopOnEntry stop target. Null once the frame body is exhausted.
    public Interpreter.StatementUnit? Current => _frames?.Current.Cursor.Current;

    // M4: the session is completed only when the ROOT frame's body is exhausted —
    // callee completion pops within the same StepAsync and never leaves this true.
    // M8 (§5.4): in a multi-batch script, an exhausted batch that is NOT the last is a
    // GO boundary, not session end — the driver crosses it (AdvanceToNextBatch); the
    // session completes only once the LAST batch's cursor is exhausted. For procedure
    // mode / single-batch scripts MoreBatchesRemain is always false, so this reduces to
    // the M4 single-frame semantics exactly. M8 FIX (§8.3): _batchListDone short-circuits
    // this when every batch after the live one was compile-refused (client-side) — no
    // runnable batch remains, so the script is complete even though the live batch frame is
    // an earlier batch's (its scope was already torn down at the boundary).
    // A54 (§6/§11.5): the Depth == 1 guard (previously implied by the settle-before-return
    // invariant — callees always popped within the same StepAsync) is now explicit: a park
    // leaves a COMPLETED callee cursor on the stack at depth > 1, which must never read as
    // session-complete. `!_pendingReturnStop` likewise holds the session open while the
    // ROOT proc is parked at its own implicit return (procedure mode) — the next step
    // consumes the park and only THEN does the session end.
    public bool IsCompleted => _frames is null
        || _batchListDone
        || (_frames.Depth == 1 && _frames.Current.Cursor.IsCompleted && !MoreBatchesRemain && !_pendingReturnStop);

    // M8 (§5.4): more GO batches remain after the live one. Always false in procedure
    // mode and single-batch scripts (one blueprint).
    private bool MoreBatchesRemain => _batches is not null && _currentBatchIndex < _batches.Count - 1;

    // M8 (§5.4): the live batch frame's cursor is exhausted at depth 1 (every EXEC
    // callee has popped — EXEC is synchronous) and more batches remain — the normal GO
    // boundary AdvanceToNextBatch crosses. A callee mid-flight (depth > 1) or the last
    // batch make this false.
    private bool AtBatchBoundary => _frames is not null
        && _frames.Depth == 1
        && _frames.Current.Cursor.IsCompleted
        && MoreBatchesRemain;

    /// <summary>A54 (§6/§11.5): true while the top frame is a MODULE frame PARKED at its
    /// implicit return — its body ran off the end (no explicit RETURN) on a single step and
    /// the §11.5 pop is deferred one step for inspection. The adapter reads this to publish
    /// the stop (next/stepIn) or run through it (continue/stepOut); while parked
    /// <see cref="Current"/> is null (the cursor is completed but not popped).</summary>
    public bool AtImplicitReturn => _pendingReturnStop;

    // A54 (§6/§11.5): should the CURRENT top frame park at an implicit-return stop rather
    // than pop now? True when its body just ran off the end (cursor completed, NOT via an
    // explicit RETURN — that is already a stoppable line) and it is a MODULE frame. Excludes
    // an already-parked frame (the consume step pops instead of re-parking the same frame).
    private bool ShouldParkAtImplicitReturn()
        => _frames is not null
        && !_pendingReturnStop
        && _frames.Current.Cursor.IsCompleted
        && !_frames.Current.Cursor.CompletedByExplicitReturn
        && IsModuleFrame(_frames.Current);

    // A54: a MODULE frame is any stepped-into callee (CallSite non-null) or the top-level
    // proc in PROCEDURE mode (frame 0, CallSite null); NOT the ad-hoc SCRIPT frame 0, which
    // has no OUTPUT params / return code and just ends. (A GO batch frame is a script frame
    // too — Mode is Script — so a GO boundary is never an implicit return; the two rest
    // states stay mutually exclusive.)
    private bool IsModuleFrame(Frame frame)
        => frame.CallSite is not null || _options.Mode == LaunchMode.Procedure;

    /// <summary>M4 (§11/§18): the virtual call stack, bottom (frame 0) → top. The DAP
    /// stackTrace reverses it; scopes/variables requests key per-frame reads off it.</summary>
    public IReadOnlyList<Frame> Frames => _frames?.All ?? Array.Empty<Frame>();

    /// <summary>M4: the frame the cursor lives in (top of stack); null before init.</summary>
    public Frame? TopFrame => _frames?.Current;

    /// <summary>M8 (§5.4): zero-based index of the live PHYSICAL GO batch (the stack-bottom
    /// batch frame), for the stackTrace annotation. Distinct `GO N` iterations of one
    /// physical batch share this index (§5.4/A43). Always 0 in procedure mode /
    /// single-batch scripts.</summary>
    public int CurrentBatchIndex => _batchPositions is { Count: > 0 } p ? p[_currentBatchIndex].PhysicalIndex : 0;

    /// <summary>M8 (§5.4): total number of PHYSICAL GO batches that run (a `GO 0` batch is
    /// skipped and not counted; §5.4/A43). 1 in procedure mode and single-batch scripts
    /// (the batch-frame stackTrace annotation is suppressed when this is 1 and the batch is
    /// not itself repeated).</summary>
    public int BatchCount => _batchPositions is { Count: > 0 } p ? p[_currentBatchIndex].PhysicalCount : 1;

    /// <summary>§5.4 (A43): the 1-based `GO N` iteration of the live batch (1 for an
    /// ordinary, non-repeated batch). Drives the `[batch k/N ×i/M]` annotation.</summary>
    public int CurrentBatchIteration => _batchPositions is { Count: > 0 } p ? p[_currentBatchIndex].Iteration : 1;

    /// <summary>§5.4 (A43): the `GO N` repeat count of the live batch (1 for an ordinary,
    /// non-repeated batch). > 1 means the annotation shows the iteration.</summary>
    public int CurrentBatchRepeat => _batchPositions is { Count: > 0 } p ? p[_currentBatchIndex].Repeat : 1;

    /// <summary>M8 (§8.2/A35 — lane 1b): true after a BATCH-TERMINAL fault in a
    /// multi-batch script with more batches remaining. The fault site was published
    /// (<see cref="StepDisposition.FrameFaulted"/>) so the §10.6 filters still stop there;
    /// the session is NOT broken. The next <c>StepAsync</c> / <c>Continue</c> crosses the
    /// GO boundary (<see cref="StepDisposition.BatchCompleted"/>) — the sqlcmd/SSMS
    /// default of continuing to the next batch after a failed one. The adapter drives the
    /// interactive "Continue after the exception stop advances to the next batch" UX off
    /// this. Always false in procedure mode / single-batch scripts.</summary>
    public bool PendingBatchAdvance => _pendingBatchAdvance;

    /// <summary>M8 (§9 — lane 1b): the session-persistent temp registry — connection-scoped
    /// objects (user <c>#temp</c>/<c>##global</c>, GLOBAL cursors) promoted across GO
    /// boundaries. Read-only inspection state (like <see cref="Frames"/> / <see
    /// cref="IsDoomed"/>): a boundary force-rollback marks entries created inside the doomed
    /// transaction dead here (they are not promoted / no longer resolve). Empty in
    /// procedure mode / single-batch scripts. </summary>
    public IReadOnlyList<Interpreter.TempObjectEntry> SessionTempObjects => _sessionTempObjects.All;

    /// <summary>M6 S2 (design note §3): resolves a module's <see cref="StatementIndex"/>
    /// for breakpoint mapping / virtual-document content without requiring the module
    /// to be on the live call stack. A live frame's own index wins when the module is
    /// currently executing (any frame, not just top — recursion, p19); otherwise a
    /// side-effect-free OBJECT_DEFINITION fetch + parse, the same shape step-into's
    /// blueprint resolution uses, minus the frame push. Script modules are never
    /// fetched — the script frame is live for the session's entire life, so a miss
    /// there means the caller asked about a different session's document, not a
    /// pending resolution. Refused (null index, message set) on a broken session or
    /// an unresolvable/unparseable definition.</summary>
    public async Task<(StatementIndex? Index, string? Message)> TryGetModuleIndexAsync(
        ModuleIdentity identity, CancellationToken cancellationToken = default)
    {
        var live = _frames?.All.FirstOrDefault(f => f.Module.Equals(identity));
        if (live is not null)
        {
            return (live.Cursor.Index, null);
        }

        if (identity.IsScript)
        {
            return (null, "The script frame is not part of this session.");
        }

        // A58 (§11.6): a dynamic frame's text has no catalog definition to re-fetch — it is served
        // from the session's retention map, so the virtual document survives the frame's pop.
        if (identity.IsDynamic)
        {
            return _dynamicIndexes.TryGetValue(identity.Name, out var dynamicIndex)
                ? (dynamicIndex, null)
                : (null, "That dynamic SQL was not executed in this session.");
        }

        if (_broken)
        {
            return (null, "Session terminated — module definitions cannot be fetched.");
        }

        var blueprint = await FetchModuleBlueprintAsync(identity.Display, cancellationToken).ConfigureAwait(false);
        if (blueprint is null)
        {
            return (null, $"No readable definition for '{identity.Display}' (missing, encrypted, or VIEW DEFINITION denied).");
        }

        var parsed = ScriptParser.Parse(blueprint.Definition, blueprint.QuotedIdentifier, _compatLevel, out var parseErrors);
        if (parseErrors.Count > 0)
        {
            return (null, $"'{identity.Display}' did not parse.");
        }

        try
        {
            var calleeParameters = ExtractCalleeParameters(parsed, blueprint.Definition);
            var cursor = ExecutionCursor.Create(
                FrameBodyResolver.ResolveProcedureBody(parsed), blueprint.Definition,
                FrameKind.Procedure, calleeParameters.Select(p => p.Declaration.Name));
            return (cursor.Index, null);
        }
        catch (Exception ex) when (ex is MilestoneNotSupportedException or ParseTimeDiagnosticException)
        {
            return (null, $"'{identity.Display}': {ex.Message}");
        }
    }

    /// <summary>M8 (§5.4/A36, design note §7): script-mode breakpoints span the WHOLE
    /// file, not just the currently active batch — every batch's blueprint is already
    /// resolved at launch (<see cref="_batches"/>), so mapping is immediate; unlike a
    /// stepped-into procedure there is no "pending until this module loads" path. Scans
    /// batches in file order, applying the normal §13 forward-scan rule (first SU with
    /// OriginalStartLine ≥ requestedLine) within each one. Because batches occupy
    /// DISJOINT, increasing file-line ranges, the first batch with a match IS "the batch
    /// whose line range contains the requested line" — a line that falls in the gap
    /// between two batches (the GO separator itself, or a comment before the next
    /// batch's first statement) falls FORWARD into the next batch, exactly like a
    /// label/comment line falls forward to the next unit within a single frame today.
    /// The adapter's per-line breakpoint store stays batch-index-agnostic after this
    /// call resolves a line: because batches never share a line, whichever batch is
    /// live when that line is reached is automatically the right one (design note §7 —
    /// "the per-line store already works across batches"). Returns false when the line
    /// is past the last SU of the last batch (caller answers verified:false), matching
    /// the single-frame <see cref="StatementIndex.TryMapBreakpointLine"/> contract. A
    /// batch that cannot itself be validated (<see cref="MilestoneNotSupportedException"/>/
    /// <see cref="ParseTimeDiagnosticException"/> — the same gates <see
    /// cref="TryGetModuleIndexAsync"/> tolerates) contributes no mappable lines rather
    /// than faulting the whole setBreakpoints request; it surfaces for real only if the
    /// session ever reaches it (<c>EnterBatchAsync</c>).</summary>
    public bool TryMapScriptBreakpointLine(int requestedLine, out int batchIndex, out Interpreter.StatementUnit unit)
    {
        if (_batches is not null)
        {
            var frameKind = _options.Mode == LaunchMode.Script ? FrameKind.Script : FrameKind.Procedure;
            for (var i = 0; i < _batches.Count; i++)
            {
                var blueprint = _batches[i];
                StatementIndex index;
                try
                {
                    index = ExecutionCursor.Create(
                        blueprint.Statements, blueprint.SourceText, frameKind,
                        blueprint.Parameters.Select(p => p.Name)).Index;
                }
                catch (Exception ex) when (ex is MilestoneNotSupportedException or ParseTimeDiagnosticException)
                {
                    continue;
                }

                if (index.TryMapBreakpointLine(requestedLine, out unit))
                {
                    batchIndex = i;
                    return true;
                }
            }
        }

        batchIndex = -1;
        unit = null!;
        return false;
    }

    // ---- M3 §10 driver surface (the adapter's DAP wiring — §10.6 — reads these) ------

    /// <summary>How the most recent StepAsync resolved (§10.3). Performed until then.</summary>
    public StepOutcome LastStep { get; private set; } = StepOutcome.Performed;

    /// <summary>§10.6 'all' exception filter: when true, a fault first returns
    /// FaultAtSite with the cursor still ON the faulted unit; the NEXT StepAsync
    /// performs the deferred route (no server work). setExceptionBreakpoints may flip
    /// this mid-session.</summary>
    public bool BreakOnAllErrors { get; set; }

    /// <summary>Top of the ErrorContextStack (§10.2) — the Error Context scope's
    /// source (§10.6/§12.2) and exceptionInfo payload; null outside any CATCH.</summary>
    public ErrorContext? ActiveErrorContext => _errorContexts.Count > 0 ? _errorContexts[^1] : null;

    public int ErrorContextDepth => _errorContexts.Count;

    /// <summary>§10.4: uncommittable transaction observed and not yet exited via ROLLBACK.</summary>
    public bool IsDoomed => _doomed;

    /// <summary>§10.4 amended (fact 22, deferred resurrection): the debuggee ended the
    /// transaction (ROLLBACK/COMMIT) and the safety transaction has NOT been re-opened
    /// yet — trancount observables are currently faithful to native (0). It re-opens
    /// automatically before the next statement that requires rollback protection.</summary>
    public bool IsTransactionDetached => _detached;

    /// <summary>§10.3 step 4 terminal state — only inspection/REPL/terminate remain.</summary>
    public bool IsBroken => _broken;

    /// <summary>Session-start policy notes (§10.4 COMMIT scan on rollback-mode
    /// sessions); the adapter prints them to the Debug Console at launch. Always shown —
    /// these are user-facing warnings, not gated diagnostics (A56).</summary>
    public IReadOnlyList<string> LaunchWarnings => _launchWarnings;

    // DESIGN §2 (A57): the concrete ScriptDom parser compat level in effect (150/160/170) —
    // equals the launch compatLevel when pinned, or the server-derived value when auto (0).
    // Meaningful after InitializeAsync (before that it is the requested value).
    public int EffectiveCompatLevel => _compatLevel;

    /// <summary>A56 (§12.3/§15): drains the debugger's diagnostic annotations accumulated
    /// since the last drain (NOCOUNT/GO-batch/untracked-SET/DML-trigger notes), clearing
    /// the buffer. The adapter drains after each step and at launch, surfacing them only
    /// under logLevel:"verbose"; RunToEndAsync folds them into the offline record.</summary>
    public IReadOnlyList<string> DrainDiagnosticNotes()
    {
        if (_diagnosticNotes.Count == 0)
        {
            return Array.Empty<string>();
        }

        var drained = _diagnosticNotes.ToArray();
        _diagnosticNotes.Clear();
        return drained;
    }

    // ---- M5 I3 (§12.1 System scope) — served from tracked state, zero round trips ----

    /// <summary>@@SPID, captured once at connection open (constant per connection).</summary>
    public int Spid => _spid;

    /// <summary>@@TRANCOUNT from the STOP's own control row (§7.3) — engine truth of
    /// the batch that produced the stop, not a live probe.</summary>
    public int LastObservedTrancount => _lastObservedTrancount;

    /// <summary>XACT_STATE() from the stop's own control row (§7.3).</summary>
    public int LastObservedXactState => _lastObservedXactState;

    /// <summary>§11.2 runtime-tracked isolation level + on/off SET options, keyed by
    /// option name ("TRANSACTION ISOLATION LEVEL" among them) — the System scope's
    /// source for everything besides trancount/xact_state/spid/XACT_ABORT.</summary>
    public IReadOnlyDictionary<string, string> RuntimeOptionsSnapshot => _runtimeOptions.Snapshot();

    /// <summary>DESIGN §15 / M5 I4: rows per Temp Tables page (launch config
    /// <c>tempTablePageSize</c>, default 50).</summary>
    public int TempTablePageSize => _options.TempTablePageSize;

    /// <summary>DESIGN §12.3/§15: result-set row cap for Debug Console rendering (launch
    /// config <c>maxConsoleRows</c>, default 200) — used by the adapter's stepped-result-
    /// set projection (A50), the same cap the REPL applies.</summary>
    public int MaxConsoleRows => _options.MaxConsoleRows;

    /// <summary>DESIGN §15 / M5 I4: client-side truncation length for temp-table row
    /// cells (launch config <c>displayValueChars</c>, default 256).</summary>
    public int DisplayValueChars => _options.DisplayValueChars;

    /// <summary>DESIGN §15 / M5 I6: the console's own CommandTimeout — the DAP layer
    /// enforces it via a linked CancellationTokenSource around EvaluateReplAsync.</summary>
    public int ConsoleTimeoutSeconds => _options.ConsoleTimeoutSeconds;

    /// <summary>DESIGN §15 / M5 I7: the shared per-stop watch budget (ms) — the DAP
    /// layer enforces it via StopSnapshot's per-epoch stopwatch, not inside Session.</summary>
    public int WatchBudgetMs => _options.WatchBudgetMs;

    // DESIGN §18: stackTrace frame name, "dbo.Proc (line N)". M4: the top frame's.
    public string ModuleDisplay => _frames?.Current.Module.Display ?? "<none>";

    // DESIGN §13 "Jump to Cursor": moves the cursor to `target` WITHOUT executing
    // anything in between (the cursor's own JumpTo re-creates predicate-stop
    // invariants for an IF/WHILE target — see ExecutionCursor.JumpTo). M4: targets are
    // CURRENT-frame units only — a cross-frame teleport is refused by the index lookup
    // inside the cursor (§13's nesting policy a fortiori).
    public void JumpTo(Interpreter.StatementUnit target)
    {
        if (!_initialized || _frames is null)
        {
            throw new InvalidOperationException("Session.InitializeAsync must complete before jumping.");
        }

        CurrentFrame.Cursor.JumpTo(target);
        ReconcileErrorContexts();                  // §10.2: a jump that leaves a CATCH pops its context
        _pendingFault = null;                      // §10.6/§13: jumping away from a FaultAtSite stop
                                                   // abandons the deferred route — it must not fire
                                                   // later from the new position (fact 21 review fix)
        _armedC23Unit = null;                      // A14: Jump to Cursor is the pre-flight stop's
        _c23TerminalNote = null;                   // documented skip path — abandon both phases
    }

    // DESIGN §12.1 Locals scope source: the TOP frame's variables in catalog order;
    // ordinal matches the §7.5 display-projection v_<ord> columns. Per-frame reads
    // (DAP frameId) go through Frames[i].Variables.
    public IReadOnlyList<VariableSlot> Variables => _frames?.Current.Variables.All ?? Array.Empty<VariableSlot>();

    // DESIGN §8.1: "Adapter keeps a binary snapshot ... this is also the
    // Variables-pane source." Fetched on demand (driven by the DAP `variables`
    // request), not automatically after every step — §15: "never add per-step
    // queries without a lazy/budgeted design."
    public Task<StateSnapshot> GetStateSnapshotAsync(CancellationToken cancellationToken = default)
        => GetStateSnapshotAsync(CurrentFrame, cancellationToken);

    /// <summary>M4: the per-frame variant — the DAP `variables` request carries a
    /// frameId; any live frame's state is readable while stopped (§12.1).</summary>
    public async Task<StateSnapshot> GetStateSnapshotAsync(Frame frame, CancellationToken cancellationToken = default)
    {
        if (!_initialized || _frames is null)
        {
            throw new InvalidOperationException("Session.InitializeAsync must complete before reading a snapshot.");
        }

        // §10.4: while doomed the table is unwritable (3930) and therefore stale from
        // the first post-doom step — the in-memory snapshot (updated from every batch's
        // __dbg_state set) is authoritative. Frames > 0 always have one (D1).
        if (_doomed && frame.Snapshot is not null)
        {
            return StateSnapshot.FromValues(frame.Snapshot, frame.Variables.All);
        }

        var result = await ExecuteAndTraceAsync(StateTableDdlBuilder.BuildSelectAll(frame.Ordinal), cancellationToken).ConfigureAwait(false);
        var resultSet = result.ResultSets.Count > 0 ? result.ResultSets[0] : null;
        return resultSet is null ? StateSnapshot.Empty : StateSnapshot.FromResultSet(resultSet, frame.Variables.All);
    }

    // ---- M5 I4 (§12.2 Temp Tables scope) — plain, read-only round trips against an
    // ALREADY-RESOLVED physical name (no rewriting, no frame-variable declarations: the
    // registry already carries the exact object name). Called only for healthy/detached
    // sessions — doomed/broken display strings are eager and client-side (I4's "A14
    // certainty argument, reused"), never probed. Side-effect-free by construction, so
    // — like EvaluateConditionAsync — these never touch shadow state and report a fault
    // instead of throwing (I9).

    // M6 R5 (M5-gate carry-over): explicit doomed/broken early-outs — a refusal
    // result, never an exception — enforcing IN CODE the contract the comment above
    // already asserts (today's only caller, the display layer, never invokes these
    // while doomed/broken; this is defense in depth for a future caller who doesn't
    // know that invariant). No executor round trip on either arm.
    private bool TryRefuseInspectionProbe(out string? refusalMessage)
    {
        if (_broken)
        {
            refusalMessage = "session terminated — inspection is limited to the captured stop state.";
            return true;
        }

        if (_doomed)
        {
            refusalMessage = "unavailable while the transaction is doomed (§10.4) — the eager client-side display already covers this state.";
            return true;
        }

        refusalMessage = null;
        return false;
    }

    /// <summary>§12.2: <c>SELECT COUNT(*)</c> against an already-resolved temp table or
    /// table-variable realization.</summary>
    public async Task<(int? RowCount, string? FaultMessage)> GetTempObjectRowCountAsync(
        string physicalName, CancellationToken cancellationToken = default)
    {
        if (TryRefuseInspectionProbe(out var refusal))
        {
            return (null, refusal);
        }

        var bracketed = RewriteContext.BracketIdentifier(physicalName);
        try
        {
            var result = await ExecuteAndTraceAsync($"SELECT COUNT(*) AS c FROM {bracketed};", cancellationToken).ConfigureAwait(false);
            var count = result.ResultSets is [{ Rows: [[var c, ..], ..] }, ..] ? Convert.ToInt32(c) : 0;
            return (count, null);
        }
        catch (StatementExecutionException ex)
        {
            return (null, ex.Message);
        }
    }

    /// <summary>§12.2 paging shape: <c>OFFSET/FETCH</c> against an already-resolved
    /// physical name; <c>ORDER BY (SELECT NULL)</c> — no engine-guaranteed page
    /// stability, a documented display-only residual (design note §2 I4).</summary>
    public async Task<(ResultSet? Page, string? FaultMessage)> GetTempObjectPageAsync(
        string physicalName, int start, int count, CancellationToken cancellationToken = default)
    {
        if (TryRefuseInspectionProbe(out var refusal))
        {
            return (null, refusal);
        }

        var bracketed = RewriteContext.BracketIdentifier(physicalName);
        try
        {
            var result = await ExecuteAndTraceAsync(
                $"SELECT * FROM {bracketed} ORDER BY (SELECT NULL) OFFSET {start} ROWS FETCH NEXT {count} ROWS ONLY;",
                cancellationToken).ConfigureAwait(false);
            var page = result.ResultSets.Count > 0
                ? result.ResultSets[0]
                : new ResultSet(Array.Empty<string>(), Array.Empty<IReadOnlyList<object?>>());
            return (page, null);
        }
        catch (StatementExecutionException ex)
        {
            return (null, ex.Message);
        }
    }

    /// <summary>§12.2 cursor entries: lazy <c>CURSOR_STATUS</c>, healthy/detached only
    /// per I4 — R3 always renames to GLOBAL.</summary>
    public async Task<(string? Status, string? FaultMessage)> GetCursorStatusAsync(
        string physicalCursorName, CancellationToken cancellationToken = default)
    {
        if (TryRefuseInspectionProbe(out var refusal))
        {
            return (null, refusal);
        }

        var literal = ShadowValues.SqlStringLiteral(physicalCursorName);
        try
        {
            var result = await ExecuteAndTraceAsync(
                $"SELECT CURSOR_STATUS('global', {literal}) AS s;", cancellationToken).ConfigureAwait(false);
            if (result.ResultSets is not [{ Rows: [[var s, ..], ..] }, ..])
            {
                return (null, "no CURSOR_STATUS row returned");
            }

            return (DescribeCursorStatus(Convert.ToInt32(s)), null);
        }
        catch (StatementExecutionException ex)
        {
            return (null, ex.Message);
        }
    }

    private static string DescribeCursorStatus(int status) => status switch
    {
        1 => "open, rows present",
        0 => "open, empty",
        -1 => "closed",
        -2 => "cursor variable has no cursor allocated",
        -3 => "does not exist",
        _ => $"status {status}",
    };

    // ---- M5 I8 (§8.3 setVariable, A19) ------------------------------------------

    public enum SetVariableOutcome { Applied, Refused }

    /// <summary>One arm's outcome (§8.3 state matrix). <see cref="Note"/> is an
    /// optional console-note text (the doomed arm's "client-side until rollback"
    /// wording); <see cref="RefusalReason"/> is set exactly when Outcome is Refused.</summary>
    public sealed record SetVariableResult(SetVariableOutcome Outcome, object? AppliedValue, string? Note, string? RefusalReason);

    // DESIGN §8.3/A19: healthy path stands (literal parse → client-side validate/
    // convert → parameterized UPDATE → snapshot refresh); doomed/detached/broken add
    // their own arms. Debugger-initiated (I9): touches no shadow state, never composes
    // through ComposeDebuggeeBatch, reports faults instead of throwing.
    public async Task<SetVariableResult> SetVariableAsync(
        Frame frame, string variableName, string literalText, CancellationToken cancellationToken = default)
    {
        if (!_initialized || _frames is null)
        {
            throw new InvalidOperationException("Session.InitializeAsync must complete before setVariable.");
        }

        if (_broken)
        {
            return new SetVariableResult(SetVariableOutcome.Refused, null, null,
                "session terminated (§10.3) — setVariable is refused; only inspection and teardown remain.");
        }

        if (!frame.Variables.TryGet(variableName, out var slot))
        {
            return new SetVariableResult(SetVariableOutcome.Refused, null, null, $"no such variable '{variableName}' in this frame.");
        }

        // A59 (§8.3): the STORAGE type decides. An alias-typed variable is exactly as
        // writable as its base type — and its declared name could never pass this test,
        // nor survive the CONVERT below (fact 34b).
        if (!HasSafeLiteralForm(slot.Declaration.StorageType))
        {
            return new SetVariableResult(SetVariableOutcome.Refused, null, null,
                $"'{slot.Declaration.DataTypeSql}' has no safe literal form for setVariable (§8.3) — " +
                "use a write-mode REPL statement instead.");
        }

        var expression = ScriptParser.ParseScalarExpression(literalText, frame.SetEnv.QuotedIdentifier, _compatLevel, out var parseErrors);
        if (expression is null || parseErrors.Count > 0)
        {
            return new SetVariableResult(SetVariableOutcome.Refused, null, null,
                $"could not parse '{literalText}': {string.Join("; ", parseErrors.Select(e => e.Message))}");
        }

        if (!TryConvertLiteral(expression, out var value, out var rejectionReason))
        {
            return new SetVariableResult(SetVariableOutcome.Refused, null, null, rejectionReason);
        }

        // §10.4/A19: the client-side Frame.Snapshot slot is kept in sync regardless of
        // arm — it is ALREADY the authoritative doomed-mode value source (A8) and the
        // resurrection re-seed source; a healthy/detached write updating it too just
        // avoids a staleness window if the session dooms on a LATER step.
        if (frame.Snapshot is not null && slot.Ordinal < frame.Snapshot.Length)
        {
            frame.Snapshot[slot.Ordinal] = value is DBNull ? null : value;
        }

        if (_doomed)
        {
            // A19: NO server work — the state table is destroyed under 3930 (fact 22).
            return new SetVariableResult(SetVariableOutcome.Applied, value,
                "the transaction is doomed (§10.4) — this edit is client-side until the debuggee's ROLLBACK, " +
                "which reseeds the state table from it.", null);
        }

        // Healthy/detached: the SAME parameterized UPDATE either way — detached gets
        // NO protection re-open (state tables are debugger housekeeping, not debuggee
        // data; §16's rollback-ability contract is about debuggee writes, not this).
        var table = StateTableIdentifiers.TableName(frame.Ordinal);
        var column = StateTableIdentifiers.ColumnName(variableName);
        const string parameterName = "@p";
        var updateText = $"UPDATE {table} SET {column} = CONVERT({slot.Declaration.StorageType}, {parameterName});";
        try
        {
            await ExecuteAndTraceAsync(updateText, new List<BatchParameter> { new(parameterName, value) }, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (StatementExecutionException ex)
        {
            return new SetVariableResult(SetVariableOutcome.Refused, null, null,
                $"setVariable faulted: {ex.Message} (error {ex.Number}).");
        }

        return new SetVariableResult(SetVariableOutcome.Applied, value, null, null);
    }

    // DESIGN §8.3: built-in SQL types with a genuine LITERAL form (a plain constant
    // CONVERTs into them) — CLR/UDT types and the "needs a static method, not a
    // literal" special types (hierarchyid/geography/geometry) have no entry here and
    // are refused up front with a clear message rather than a raw server error.
    private static readonly HashSet<string> SafeLiteralTypeKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "bigint", "int", "smallint", "tinyint", "bit",
        "decimal", "numeric", "money", "smallmoney", "float", "real",
        "char", "varchar", "text", "nchar", "nvarchar", "ntext",
        "date", "datetime", "datetime2", "smalldatetime", "datetimeoffset", "time",
        "binary", "varbinary", "image", "rowversion", "timestamp",
        "uniqueidentifier", "xml", "sql_variant",
    };

    private static bool HasSafeLiteralForm(string dataTypeSql)
    {
        var name = dataTypeSql.Split('(', ' ', '[', ']')[0].Trim();
        return SafeLiteralTypeKeywords.Contains(name);
    }

    // §8.3 "validate/convert to the declared type client-side": confirms the parsed
    // expression is a plain literal (optionally unary +/-) and converts it to a
    // reasonably-typed .NET value for the ADO.NET parameter — the UPDATE's own
    // CONVERT(declaredType, @p) does the PRECISE, engine-authoritative coercion
    // (exact precision/scale/collation), matching the CONVERT idiom used everywhere
    // else in this codebase (§10.4 doomed seeding, §7.5 display projection) rather
    // than hand-rolling full SQL type semantics in C#.
    private static bool TryConvertLiteral(ScalarExpression expression, out object? value, out string? rejectionReason)
    {
        value = null;
        rejectionReason = null;

        var negative = false;
        var expr = expression;
        if (expr is UnaryExpression { UnaryExpressionType: UnaryExpressionType.Negative } neg)
        {
            negative = true;
            expr = neg.Expression;
        }
        else if (expr is UnaryExpression { UnaryExpressionType: UnaryExpressionType.Positive } pos)
        {
            expr = pos.Expression;
        }

        if (expr is not Literal literal)
        {
            rejectionReason = "not a literal value — setVariable accepts only literal constants (no expressions, columns, or function calls).";
            return false;
        }

        try
        {
            switch (literal)
            {
                case NullLiteral:
                    value = DBNull.Value;
                    return true;
                case IntegerLiteral il:
                    var i = long.Parse(il.Value, CultureInfo.InvariantCulture);
                    value = negative ? -i : i;
                    return true;
                case NumericLiteral nl:
                    var n = decimal.Parse(nl.Value, CultureInfo.InvariantCulture);
                    value = negative ? -n : n;
                    return true;
                case RealLiteral rl:
                    var r = double.Parse(rl.Value, CultureInfo.InvariantCulture);
                    value = negative ? -r : r;
                    return true;
                case MoneyLiteral ml:
                    var m = decimal.Parse(ml.Value.TrimStart('$'), NumberStyles.Number, CultureInfo.InvariantCulture);
                    value = negative ? -m : m;
                    return true;
                case StringLiteral sl:
                    if (negative)
                    {
                        rejectionReason = "unary minus on a string literal is not valid.";
                        return false;
                    }

                    value = sl.Value;
                    return true;
                case BinaryLiteral bl:
                    if (negative)
                    {
                        rejectionReason = "unary minus on a binary literal is not valid.";
                        return false;
                    }

                    value = ParseHexLiteral(bl.Value);
                    return true;
                default:
                    rejectionReason = $"literal type {literal.LiteralType} has no safe client-side conversion (CLR/exotic types) — " +
                                       "use a write-mode REPL statement instead.";
                    return false;
            }
        }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            rejectionReason = $"could not parse literal '{literal.Value}' for {literal.LiteralType}: {ex.Message}";
            return false;
        }
    }

    private static byte[] ParseHexLiteral(string hex)
    {
        var digits = hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? hex[2..] : hex;
        if (digits.Length % 2 != 0)
        {
            digits = "0" + digits;
        }

        var bytes = new byte[digits.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = Convert.ToByte(digits.Substring(i * 2, 2), 16);
        }

        return bytes;
    }

    // DESIGN §13: a conditional breakpoint's condition, evaluated when its line is
    // reached. Debugger-initiated — unlike a debuggee IF/WHILE predicate (§6 M2 D3)
    // this is invisible to the debuggee and must not touch shadow state at all (no
    // ObservePredicateEvaluation, no ObserveSuccess). "A faulting condition = console
    // warning + break anyway" (§13) is the caller's job, not this method's — a fault
    // here is reported (FaultMessage set), not thrown, since it is never session-fatal.
    public async Task<(bool? Value, string? FaultMessage)> EvaluateConditionAsync(
        string conditionText, CancellationToken cancellationToken = default)
    {
        if (!_initialized || _frames is null)
        {
            throw new InvalidOperationException("Session.InitializeAsync must complete before evaluating a condition.");
        }

        var frame = CurrentFrame;
        var predicate = ScriptParser.ParseBooleanExpression(
            conditionText, frame.SetEnv.QuotedIdentifier, _compatLevel, out var errors);
        if (predicate is null || errors.Count > 0)
        {
            return (null, $"Could not parse breakpoint condition '{conditionText}': " +
                          string.Join("; ", errors.Select(e => e.Message)));
        }

        // DebuggerInitiated (fact 19): a FAULTING condition under the debuggee's
        // XACT_ABORT ON would otherwise doom the transaction as a debugger artifact —
        // the shell sandwiches XACT_ABORT OFF/restore. R7 still substitutes any direct
        // ERROR_*() the user typed (exact values from the active context); no
        // re-materialization (§12.3 side-effect-free rule). While doomed, values seed
        // from the snapshot like every other batch.
        var composition = new BatchComposition
        {
            DebuggerInitiated = true,
            XactAbortOn = frame.XactAbortOn,
            DoomedSeedValues = _doomed ? frame.Snapshot : null,
            // Fact 22: a condition evaluated while doomed must see genuine doomed
            // observables (the user may well be watching XACT_STATE() itself) — the
            // server-side doom evaporated with the previous batch's forced rollback.
            RedoomTrancount = _doomed ? _doomTrancount : null,
            HealthyPrefixDdl = _doomed ? CollectHealingDdl() : null,
        };
        var batch = ComposedBatchBuilder.BuildForPredicate(
            frame, _rewriteEngine!, _rewriteContext!, predicate, conditionText, _shadows!, composition);
        BatchResult batchResult;
        try
        {
            batchResult = await ExecuteAndTraceAsync(batch, cancellationToken).ConfigureAwait(false);
        }
        catch (StatementExecutionException ex)
        {
            // §10 line review F5: this method's contract is "reported, not thrown" for
            // every fault path — a no-control-row failure (timeout/compile-class, §10.1)
            // in a condition eval is no different from a routed server fault above and
            // must not escape as an exception either.
            return (null, $"Breakpoint condition '{conditionText}' faulted: {ex.Message} (error {ex.Number}).");
        }

        var (control, userSets, _) = ControlRowParser.Parse(batchResult, frame.Variables.Count);
        if (!control.Ok)
        {
            return (null, $"Breakpoint condition '{conditionText}' faulted: {control.ErrMessage} (error {control.ErrNumber}).");
        }

        return (ExtractScalarColumn(userSets, batch) == 1, null);
    }

    // ---- M5 I6 (§12.3 REPL, A17) -------------------------------------------------

    public enum ReplOutcome { Rendered, Refused }

    /// <summary>Rendered = the statement ran (possibly with a T-SQL-level fault the
    /// console's own TRY/CATCH caught — shown as text, exactly like a native client
    /// would print it); Refused = the debugger itself declined BEFORE sending anything
    /// (whitelist/parse/state-matrix) — the caller surfaces this as a DAP error.</summary>
    // A46: VariablesChanged is true when a write-mode console statement wrote variable
    // state back (the adapter then invalidates the frame's Locals cache so the Variables
    // panel refetches). A61: TableContentChanged is its table-content analog — true when
    // the statement was a non-variable-only write (DELETE/INSERT/UPDATE/MERGE against a
    // table var / #temp / real table), so the adapter drops the Temp Tables display caches
    // and refetches the live rowcount. Both optional/defaulted so the Refused/read paths
    // construct unchanged.
    public sealed record ReplResult(ReplOutcome Outcome, string? Rendered, string? RefusalMessage,
        bool VariablesChanged = false, bool TableContentChanged = false);

    // §12.3 pipeline: parse (selected frame's parser) -> whitelist -> R1-R7 rewrite
    // against that frame -> own micro TRY/CATCH, DebuggerInitiated -> no shadow
    // observation, no state write -> render capped at MaxConsoleRows. State matrix
    // (A17): broken refuses outright; doomed forces read-only (a write hits native
    // 3930, refused before sending); detached re-opens protection first for a write
    // (§10.4 deferred resurrection, C24 note); healthy runs inside the open safety
    // transaction. One statement per evaluation.
    public async Task<ReplResult> EvaluateReplAsync(
        Frame frame, string statementText, CancellationToken cancellationToken = default)
    {
        if (!_initialized || _frames is null)
        {
            throw new InvalidOperationException("Session.InitializeAsync must complete before REPL evaluation.");
        }

        if (_broken)
        {
            return new ReplResult(ReplOutcome.Refused, null,
                "session terminated — inspection is limited to the captured stop state.");
        }

        var statementList = ScriptParser.ParseStatementList(
            statementText, frame.SetEnv.QuotedIdentifier, _compatLevel, out var parseErrors);
        if (statementList is null || parseErrors.Count > 0)
        {
            return new ReplResult(ReplOutcome.Refused, null,
                $"could not parse '{statementText}': {string.Join("; ", parseErrors.Select(e => e.Message))}");
        }

        if (statementList.Statements.Count != 1)
        {
            return new ReplResult(ReplOutcome.Refused, null,
                "one statement at a time — the console evaluates exactly one T-SQL statement per request.");
        }

        var statement = statementList.Statements[0];
        var classification = ReplWhitelist.Classify(statement, statementText, _options.AllowConsoleWrites);
        if (!classification.IsAllowed)
        {
            return new ReplResult(ReplOutcome.Refused, null, classification.RefusalMessage);
        }

        if (_doomed && classification.IsWrite && !classification.IsVariableOnlyWrite)
        {
            // A17/A46: while doomed only a DATABASE write is refused — it would hit native
            // 3930 (no fabrication; the engine itself rejects it). A variable-only
            // assignment (SET @x = …) touches no table, so it is allowed: it persists to
            // the frame snapshot (§10.4/A8, the authoritative doomed-mode value source),
            // exactly like setVariable while doomed. The write-back's own state-table
            // UPDATE is XACT_STATE-guarded, so it self-skips — no 3930 from the harness.
            return new ReplResult(ReplOutcome.Refused, null,
                "the transaction is doomed (XACT_STATE() = -1) — a database write hits native error 3930 " +
                "until it is rolled back (a variable assignment like `SET @x = …` is still allowed).");
        }

        var messages = new List<string>();
        if (_detached && classification.IsWrite && !classification.IsVariableOnlyWrite)
        {
            // §10.4 deferred resurrection (A9), REPL's own trigger: a console DATABASE
            // write is bound by §16's rollback-ability contract exactly like a debuggee
            // write. A variable-only write is EXCLUDED (A46): it only touches the state
            // table (debugger housekeeping, not debuggee data) — setVariable's detached
            // arm re-opens no protection either (§8.3/A19), and the console matches it.
            // Duplicated (not reused from EnsureTransactionProtectionAsync) because
            // that method dispatches on a StatementUnit's Kind/SubKind — REPL already
            // knows classification.IsWrite from the whitelist above, a different (and
            // simpler) decision path; the ACTION taken is the same three lines.
            await ExecuteAndTraceAsync("BEGIN TRANSACTION;", cancellationToken).ConfigureAwait(false);
            _detached = false;
            _lastObservedTrancount = 1;
            messages.Add(
                "Safety transaction re-opened before this console write (§10.4 deferred resurrection) — the write " +
                "must remain rollback-able; @@TRANCOUNT reads 1 from here where a native post-rollback run would " +
                "read 0 (caveat C24 applies from here).");
        }

        var composition = new BatchComposition
        {
            DebuggerInitiated = true,
            XactAbortOn = frame.XactAbortOn,
            RedoomTrancount = _doomed ? _doomTrancount : null,
            HealthyPrefixDdl = _doomed ? CollectHealingDdl() : null,
            // A45 (§12.3): seed the frame's variables so a bare @var resolves in the
            // console. Doomed → from the snapshot (the state table is stale under 3930,
            // §10.4); healthy/detached → BuildForRepl reads the state table directly.
            DoomedSeedValues = _doomed ? frame.Snapshot : null,
        };

        // §9/design note §5 item 7: resolve temp/table-var/cursor names through the
        // SELECTED frame's chain (DAP frameId), not necessarily the top frame.
        // §12.3 residual: a console statement that itself CREATES a new temp/
        // table-var/cursor gets R1-R3 disabled for the WHOLE statement (the create
        // target must keep its literal name — "deliberately NOT R2-patched and NOT
        // registered"; R2/R1/R3 themselves have no per-target opt-out, only a global
        // TempNames-null/non-null switch, and modifying those rules is §7.4 rewriter
        // core, gated to Fable/Opus — this is the documented, conservative trade-off).
        var savedTempNames = _rewriteContext!.TempNames;
        _tempNameScope!.EvaluationFrameOrdinal = frame.Ordinal;
        _rewriteContext.TempNames = classification.CreatesNewTempObject ? null : savedTempNames;
        // A46 (§12.3): in write mode the console persists variable changes (SET @x = …,
        // SELECT @x = …, EXEC … @x OUTPUT) — a state write-back + snapshot read-back, the
        // same as an interpreted statement. Gated on allowConsoleWrites (Ivan's ruling:
        // read-only stays read-only). Enabled while doomed too: the write-back's state-
        // table UPDATE is XACT_STATE-guarded (self-skips at 3930), and the __dbg_state
        // read-back still carries the new value into Frame.Snapshot — so a doomed SET @x
        // persists to the authoritative doomed-mode source (§10.4/A8), no 3930.
        var persistVariables = _options.AllowConsoleWrites;
        // The trailing probe feeds §10.4 edge detection (a console EXEC can move the
        // transaction). A variable-only assignment never moves it, so it needs no probe.
        var includeTrailingProbe = classification.IsWrite && !classification.IsVariableOnlyWrite;
        ComposedBatch composedBatch;
        try
        {
            composedBatch = ComposedBatchBuilder.BuildForRepl(
                frame, _rewriteEngine!, _rewriteContext, statement, statementText, _shadows!,
                composition, includeTrailingProbe: includeTrailingProbe, includeStateWriteback: persistVariables);
        }
        finally
        {
            _rewriteContext.TempNames = savedTempNames;
            _tempNameScope.EvaluationFrameOrdinal = null;
        }

        // A59 (fact 34h): a console statement that passes a TVP argument materializes it the
        // same way an interpreted one does — and moves the identity chain the same way. The
        // REPL does not go through ComposeDebuggeeBatch (it is debugger-initiated), so it
        // poisons the chain here; without it, the NEXT debuggee statement's SCOPE_IDENTITY()
        // capture would silently read the console's bookkeeping INSERT.
        NoteIdentityChainMove(composedBatch);

        BatchResult batchResult;
        try
        {
            // ExecuteAndTraceAsync(ComposedBatch) dispatches on Parameters: a plain
            // language batch when healthy/detached (table-read seed), sp_executesql when
            // doomed (snapshot param seed) — same as every other composed batch.
            batchResult = await ExecuteAndTraceAsync(composedBatch, cancellationToken).ConfigureAwait(false);
        }
        catch (StatementExecutionException ex)
        {
            return new ReplResult(ReplOutcome.Refused, null,
                $"console statement faulted: {ex.Message} (error {ex.Number}).");
        }

        var (userSets, fault, probe, stateValues, rowCount) = ParseReplBatchResult(batchResult);

        // §12.3 "messages inline": surface the batch's own PRINT / low-severity RAISERROR
        // (sev ≤ 10 arrives as an InfoMessage, fact 18) output to the console. This stream
        // was previously dropped — a bare `PRINT 'OK'` rendered as "(no result sets)" with
        // the message swallowed. The oracle's TRY/CATCH swallows sev > 10 server-side (it is
        // reported via the __dbg_repl_err row below), so these are the user's info-stream
        // messages only, never a doubled fault. Ordered after any debugger meta-note (the
        // §10.4 resurrection line) but before the fault line, matching execution order.
        messages.AddRange(batchResult.Messages);

        // C5/§12.3: a DML write's "(N rows affected)" line — NOCOUNT is forced ON so the
        // engine emits no done-token; synthesize it from the @@ROWCOUNT captured right after
        // the statement, mirroring the stepped-statement note (AppendRowsAffectedNote). Only
        // the row-affecting DML family; a faulted statement never reached the capture, so
        // rowCount is null and nothing is added.
        if (rowCount is { } rc && statement is InsertStatement or UpdateStatement
                or DeleteStatement or MergeStatement or SelectStatement { Into: not null })
        {
            messages.Add(FormatRowsAffected(rc));
        }

        // A46: a write-mode statement's __dbg_state read-back refreshes the SELECTED
        // frame's binary snapshot (§8.1) — the same refresh the interpreter does every
        // step (RunFaultableBatchAsync). On a caught fault the batch emits no __dbg_state,
        // so the snapshot is left as-is (the statement rolled back; variables unchanged).
        var variablesChanged = false;
        if (stateValues is not null)
        {
            var newSnapshot = stateValues.ToArray();
            // Flag a change (→ the adapter invalidates the Variables panel) only when the
            // values actually moved — a write-mode READ writes back unchanged values, and
            // there is no point refetching the panel for that.
            variablesChanged = frame.Snapshot is null || !frame.Snapshot.SequenceEqual(newSnapshot);
            frame.Snapshot = newSnapshot;
        }

        if (probe is { } p)
        {
            // Design note §5 item 5: the trailing probe feeds the SAME edge-detection
            // code a control row would — not a parallel bookkeeping path.
            var probeControl = new ControlRow(
                true, null, null, p.Trancount, p.XactState, null, null, null, null, null, null,
                new Dictionary<int, DisplayValue>());
            await ObserveControlRowAsync(probeControl, frame.Cursor.Current!, messages).ConfigureAwait(false);
        }

        if (fault is { } f)
        {
            messages.Add(
                $"Msg {f.Number}, Level {f.Severity}, State {f.State}" +
                (f.Procedure is { } proc ? $", Procedure {proc}" : string.Empty) +
                (f.Line is { } line ? $", Line {line}" : string.Empty) + $"\n{f.Message}");
        }

        // A61: a write that touched table contents (any allowed write that is not a
        // variable-only `SET @x = …`) may have changed a temp/table-var/#temp rowcount.
        // Reaching here means classification allowed it, so allowConsoleWrites is on. Broad
        // by design — a real-table write sets it too; the adapter's re-read is lazy, so an
        // over-eager flag costs nothing (mirrors "a step re-reads everything").
        var tableContentChanged = classification.IsWrite && !classification.IsVariableOnlyWrite;

        return new ReplResult(ReplOutcome.Rendered, RenderReplOutput(userSets, messages, _options.MaxConsoleRows),
            null, variablesChanged, tableContentChanged);
    }

    private static (IReadOnlyList<ResultSet> UserSets,
        (int Number, int Severity, int State, int? Line, string? Procedure, string Message)? Fault,
        (int Trancount, int XactState)? Probe,
        IReadOnlyList<object?>? StateValues,
        int? RowCount) ParseReplBatchResult(BatchResult result)
    {
        var userSets = new List<ResultSet>();
        (int, int, int, int?, string?, string)? fault = null;
        (int, int)? probe = null;
        IReadOnlyList<object?>? stateValues = null;
        int? rowCount = null;

        foreach (var rs in result.ResultSets)
        {
            var columns = rs.Columns;
            if (columns.Contains("__dbg_state") && rs.Rows.Count > 0)
            {
                // A46: the write-mode read-back — column 0 is the __dbg_state marker; the
                // rest align with the frame's variable-catalog order (Variables.All), the
                // same layout ControlRowParser reads. Never rendered to the console.
                var row = rs.Rows[0];
                var values = new object?[row.Count - 1];
                for (var i = 1; i < row.Count; i++)
                {
                    values[i - 1] = row[i];
                }

                stateValues = values;
            }
            else if (columns.Contains("__dbg_repl_err") && rs.Rows.Count > 0)
            {
                var row = rs.Rows[0];
                int Col(string name) => columns.ToList().IndexOf(name);
                fault = (
                    Convert.ToInt32(row[Col("err_number")]),
                    Convert.ToInt32(row[Col("err_severity")]),
                    Convert.ToInt32(row[Col("err_state")]),
                    row[Col("err_line")] is null ? null : Convert.ToInt32(row[Col("err_line")]),
                    row[Col("err_procedure")] as string,
                    row[Col("err_message")] as string ?? "(no message)");
            }
            else if (columns.Contains("__dbg_repl_probe") && rs.Rows.Count > 0)
            {
                var row = rs.Rows[0];
                int Col(string name) => columns.ToList().IndexOf(name);
                probe = (Convert.ToInt32(row[Col("trancount")]), Convert.ToInt32(row[Col("xact_state")]));
            }
            else if (columns.Contains("__dbg_repl_rowcount") && rs.Rows.Count > 0)
            {
                // §12.3/C5: the @@ROWCOUNT captured right after a write statement — never
                // rendered as a user set; feeds the "(N rows affected)" line for DML.
                rowCount = Convert.ToInt32(rs.Rows[0][0]);
            }
            else
            {
                userSets.Add(rs);
            }
        }

        return (userSets, fault, probe, stateValues, rowCount);
    }

    // §12.3: "render: result sets as aligned text tables capped at maxConsoleRows
    // ('200 of N — refine your query'), messages inline."
    private static string RenderReplOutput(IReadOnlyList<ResultSet> userSets, IReadOnlyList<string> messages, int maxConsoleRows)
    {
        var sb = new StringBuilder();
        foreach (var message in messages)
        {
            sb.AppendLine(message);
        }

        var tables = RenderResultSetsAsText(userSets, maxConsoleRows);
        if (tables.Length > 0)
        {
            sb.AppendLine(tables);
        }

        // §12.3: a statement with no result sets and no message stream (a write under
        // forced NOCOUNT, C5; an empty/side-effect-only statement) renders as empty — the
        // console echoes the input and adds no noise. The old "(no result sets)" filler was
        // misleading for PRINT/SET/writes: the effect surfaces via the Variables / Temp
        // Tables refresh, not a console line.
        return sb.ToString().TrimEnd('\r', '\n');
    }

    /// <summary>
    /// DESIGN §12.3 result-set projection, shared by the REPL (<see cref="RenderReplOutput"/>)
    /// and the stepped-statement Debug Console output (A50): result sets as aligned text
    /// tables capped at <paramref name="maxConsoleRows"/> ("N of M — refine your query").
    /// Returns "" when there is nothing to show (no sets, or only column-less sets) — the
    /// caller decides whether an empty render is worth emitting.
    /// </summary>
    public static string RenderResultSetsAsText(IReadOnlyList<ResultSet> userSets, int maxConsoleRows)
    {
        var sb = new StringBuilder();
        foreach (var rs in userSets)
        {
            if (rs.Columns.Count == 0)
            {
                continue;
            }

            var shown = Math.Min(rs.Rows.Count, maxConsoleRows);
            var widths = new int[rs.Columns.Count];
            for (var c = 0; c < rs.Columns.Count; c++)
            {
                widths[c] = rs.Columns[c].Length;
                for (var r = 0; r < shown; r++)
                {
                    widths[c] = Math.Max(widths[c], ReplCellText(rs.Rows[r][c]).Length);
                }
            }

            sb.AppendLine(string.Join(" | ", rs.Columns.Select((c, i) => c.PadRight(widths[i]))));
            sb.AppendLine(string.Join("-+-", widths.Select(w => new string('-', w))));
            for (var r = 0; r < shown; r++)
            {
                sb.AppendLine(string.Join(" | ", Enumerable.Range(0, rs.Columns.Count).Select(c => ReplCellText(rs.Rows[r][c]).PadRight(widths[c]))));
            }

            if (rs.Rows.Count > maxConsoleRows)
            {
                sb.AppendLine($"{maxConsoleRows} of {rs.Rows.Count} — refine your query.");
            }
        }

        return sb.ToString().TrimEnd('\r', '\n');
    }

    private static string ReplCellText(object? value) => value is null ? "NULL" : value.ToString() ?? "NULL";

    // ---- M5 I7 (§12.4 watch) -----------------------------------------------------

    // §12.4: "must be scalar — compose SELECT (<expr>) AS v ... on failure show the
    // error as the watch value." Unlike REPL, watch DOES get frame-variable access
    // (BuildForScalarEval declares/seeds them, same as EvaluateConditionAsync) — a
    // watch like `@x + 1` is exactly the variable-level use case Watch/Hover/
    // setVariable own (REPL is database-level). State arms follow I6's read column:
    // doomed reuses the SAME DebuggerInitiated doomed shape (snapshot seed + redoom
    // prefix + healing DDL); broken renders "session terminated" (no probe, no
    // throw — this method never throws, matching "a fault renders as the watch's
    // value string"). The budget/click-to-evaluate stopwatch lives in the Adapter's
    // StopSnapshot (an epoch concept); this method evaluates exactly ONE expression.
    public async Task<string> EvaluateWatchAsync(
        Frame frame, string expressionText, CancellationToken cancellationToken = default)
    {
        if (!_initialized || _frames is null)
        {
            throw new InvalidOperationException("Session.InitializeAsync must complete before watch evaluation.");
        }

        if (_broken)
        {
            return "session terminated";
        }

        var expression = ScriptParser.ParseScalarExpression(expressionText, frame.SetEnv.QuotedIdentifier, _compatLevel, out var errors);
        if (expression is null || errors.Count > 0)
        {
            return $"could not parse '{expressionText}': {string.Join("; ", errors.Select(e => e.Message))}";
        }

        if (ReplWhitelist.ContainsNextValueFor(expression))
        {
            return "NEXT VALUE FOR is refused in a watch (read-only by construction, §12.4).";
        }

        var composition = new BatchComposition
        {
            DebuggerInitiated = true,
            XactAbortOn = frame.XactAbortOn,
            DoomedSeedValues = _doomed ? frame.Snapshot : null,
            RedoomTrancount = _doomed ? _doomTrancount : null,
            HealthyPrefixDdl = _doomed ? CollectHealingDdl() : null,
        };

        // §9/design note §5 item 7: resolve temp/table-var/cursor names (and, via R7,
        // the active error context) through the SELECTED frame — not necessarily top.
        var savedTempNames = _rewriteContext!.TempNames;
        _tempNameScope!.EvaluationFrameOrdinal = frame.Ordinal;
        Batches.ComposedBatch batch;
        try
        {
            batch = ComposedBatchBuilder.BuildForScalarEval(
                frame, _rewriteEngine!, _rewriteContext, expression, expressionText, _shadows!, composition);
        }
        finally
        {
            _rewriteContext.TempNames = savedTempNames;
            _tempNameScope.EvaluationFrameOrdinal = null;
        }

        BatchResult batchResult;
        try
        {
            batchResult = await ExecuteAndTraceAsync(batch, cancellationToken).ConfigureAwait(false);
        }
        catch (StatementExecutionException ex)
        {
            // §10 line review F5 precedent (EvaluateConditionAsync): reported, not
            // thrown, for every fault path here too.
            return $"faulted: {ex.Message} (error {ex.Number}).";
        }

        var (control, userSets, _) = ControlRowParser.Parse(batchResult, frame.Variables.Count);
        if (!control.Ok)
        {
            return $"faulted: {control.ErrMessage} (error {control.ErrNumber}).";
        }

        // A non-scalar expression (e.g. a multi-row subquery) is naturally caught by
        // the ENGINE itself as a real fault (error 512) — no separate client-side
        // "is this scalar" check needed; this shape assertion is only a builder-bug
        // guard, same as ExtractScalarColumn's.
        if (userSets is not [{ Rows: [var row, ..] }, ..])
        {
            throw new InvalidOperationException(
                $"Watch scalar-eval batch did not return the expected single-row 'p' result set (builder bug).\n{batch.Text}");
        }

        return row[0] is null ? "NULL" : row[0]!.ToString() ?? "NULL";
    }

    // M6 G2 (design note §4, A23): logpoint {expr} segments evaluate in ONE
    // debugger-initiated round trip against the CURRENT (top) frame — a logpoint
    // fires wherever execution just reached, never a user-selected frame like watch.
    // On a batch-level fault, falls back to evaluating each expression separately via
    // EvaluateWatchAsync (same DebuggerInitiated shape, same NEXT VALUE FOR refusal —
    // inspection is side-effect-free by construction, §12) so one bad expression
    // renders as an error string while the rest still log (fail-toward-logging, the
    // §13 fail-toward-stopping rule transposed). I9: never arms the §10.4/A14
    // pre-flight, never touches shadow state.
    public async Task<IReadOnlyList<string>> EvaluateLogExpressionsAsync(
        IReadOnlyList<string> expressionTexts, CancellationToken cancellationToken = default)
    {
        if (!_initialized || _frames is null)
        {
            throw new InvalidOperationException("Session.InitializeAsync must complete before logpoint evaluation.");
        }

        if (expressionTexts.Count == 0)
        {
            return Array.Empty<string>();
        }

        if (_broken)
        {
            return expressionTexts.Select(_ => "session terminated").ToList();
        }

        var frame = CurrentFrame;
        var parsed = new ScalarExpression?[expressionTexts.Count];
        var parseFaults = new string?[expressionTexts.Count];
        for (var i = 0; i < expressionTexts.Count; i++)
        {
            parsed[i] = ScriptParser.ParseScalarExpression(
                expressionTexts[i], frame.SetEnv.QuotedIdentifier, _compatLevel, out var errors);
            if (parsed[i] is null || errors.Count > 0)
            {
                parseFaults[i] = $"could not parse '{expressionTexts[i]}': {string.Join("; ", errors.Select(e => e.Message))}";
            }
            else if (ReplWhitelist.ContainsNextValueFor(parsed[i]!))
            {
                // §12/§13: inspection is side-effect-free by construction — refused
                // before it ever reaches the combined batch, not just the fallback
                // (mirrors the REPL read-only refusal and §12.4 watch's own check).
                parseFaults[i] = "NEXT VALUE FOR is refused in a logpoint (read-only by construction, §12).";
            }
        }

        if (Array.TrueForAll(parseFaults, f => f is null))
        {
            var combined = await TryEvaluateCombinedLogExpressionsAsync(frame, parsed!, expressionTexts, cancellationToken)
                .ConfigureAwait(false);
            if (combined is not null)
            {
                return combined;
            }
        }

        // Fallback: one round trip per expression (fail-toward-logging).
        var results = new List<string>(expressionTexts.Count);
        for (var i = 0; i < expressionTexts.Count; i++)
        {
            results.Add(parseFaults[i]
                ?? await EvaluateWatchAsync(frame, expressionTexts[i], cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

    // Returns null (never throws) when the combined batch itself faulted — the
    // caller's cue to fall back to per-expression evaluation. A builder-shape bug
    // (wrong column count) is still a hard throw, exactly like EvaluateWatchAsync's
    // single-column guard.
    private async Task<IReadOnlyList<string>?> TryEvaluateCombinedLogExpressionsAsync(
        Frame frame, IReadOnlyList<ScalarExpression> expressions, IReadOnlyList<string> expressionTexts,
        CancellationToken cancellationToken)
    {
        var composition = new BatchComposition
        {
            DebuggerInitiated = true,
            XactAbortOn = frame.XactAbortOn,
            DoomedSeedValues = _doomed ? frame.Snapshot : null,
            RedoomTrancount = _doomed ? _doomTrancount : null,
            HealthyPrefixDdl = _doomed ? CollectHealingDdl() : null,
        };

        var savedTempNames = _rewriteContext!.TempNames;
        _tempNameScope!.EvaluationFrameOrdinal = frame.Ordinal;
        Batches.ComposedBatch batch;
        try
        {
            var paired = new (ScalarExpression, string)[expressions.Count];
            for (var i = 0; i < expressions.Count; i++)
            {
                paired[i] = (expressions[i], expressionTexts[i]);
            }

            batch = ComposedBatchBuilder.BuildForMultiScalarEval(
                frame, _rewriteEngine!, _rewriteContext, paired, _shadows!, composition);
        }
        finally
        {
            _rewriteContext.TempNames = savedTempNames;
            _tempNameScope.EvaluationFrameOrdinal = null;
        }

        BatchResult batchResult;
        try
        {
            batchResult = await ExecuteAndTraceAsync(batch, cancellationToken).ConfigureAwait(false);
        }
        catch (StatementExecutionException)
        {
            return null;
        }

        var (control, userSets, _) = ControlRowParser.Parse(batchResult, frame.Variables.Count);
        if (!control.Ok)
        {
            return null;
        }

        if (userSets is not [{ Rows: [var row, ..] }, ..] || row.Count != expressions.Count)
        {
            throw new InvalidOperationException(
                $"Logpoint multi-scalar-eval batch did not return {expressions.Count} columns (builder bug).\n{batch.Text}");
        }

        return row.Select(v => v is null ? "NULL" : v.ToString() ?? "NULL").ToList();
    }

    // DESIGN §4 steps 1-5 (minus targets policy/connection, which are SessionHost's
    // job): SET options, resolve + parse frame 0, build the cursor (throws
    // MilestoneNotSupportedException for gated constructs — a clean launch refusal),
    // create + seed the state table, BEGIN TRAN, apply any defaulted-parameter
    // initializers. Leaves the session positioned at Current (stopOnEntry semantics).
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
        {
            throw new InvalidOperationException("Session is already initialized.");
        }

        _trace.Event("session.start", $"server={_options.Server} database={_options.Database} mode={_options.Mode} nonce={_nonce}");

        // DESIGN §2 (A57): resolve compatLevel:auto (0) to a concrete ScriptDom parser from the
        // server's product major version (captured at login, zero round trips) BEFORE the first
        // parse below. An explicit 150/160/170 passes through unchanged. Resolve is pure (no
        // round trip), so it never disturbs a FakeStatementExecutor test's scripted init sequence.
        _compatLevel = CompatLevelResolver.Resolve(_compatLevel, _serverMajorVersion, out var compatWarning);
        if (compatWarning is not null)
        {
            _launchWarnings.Add(compatWarning);
        }

        // C5 (§7.2/§21): probe NOCOUNT's PRE-EXISTING connection state in THE SAME
        // round trip as the SET below, never a separate one — every
        // FakeStatementExecutor-driven unit test scripts an exact response sequence
        // for Session's init calls (§20.2), and an extra round trip here would shift
        // every one of them. A one-time cosmetic heads-up fires iff a plain (non-
        // debugger) connection would have run with NOCOUNT OFF. @@OPTIONS bit 512 is
        // the documented NOCOUNT flag; SESSIONPROPERTY has no 'NOCOUNT' option at all
        // (verified: it only covers ANSI_NULLS/ANSI_PADDING/ANSI_WARNINGS/ARITHABORT/
        // CONCAT_NULL_YIELDS_NULL/NUMERIC_ROUNDABORT/QUOTED_IDENTIFIER).
        // A59 (§4 step 2a): the user-type catalog rides THE SAME round trip, for exactly the
        // reason above — a separate one would shift every scripted init sequence in §20.2. A
        // fake that scripts only the NOCOUNT set yields an empty catalog (no user types),
        // which is the correct answer for every pre-A59 test.
        var initResult = await ExecuteAndTraceAsync(
            "SELECT CASE WHEN (@@OPTIONS & 512) <> 0 THEN 1 ELSE 0 END AS nocount_on, " +
            "CONVERT(sysname, DATABASEPROPERTYEX(DB_NAME(), 'Collation')) AS db_collation; " +
            "SET XACT_ABORT OFF; SET NOCOUNT ON; " + UserTypeCatalog.Query, cancellationToken).ConfigureAwait(false);
        if (initResult.ResultSets is [{ Rows: [var noCountRow, ..] }, ..])
        {
            if (Convert.ToInt32(noCountRow[0]) == 0)
            {
                NoteNoCountOnce();   // A56: routed to the logLevel-gated diagnostic channel, not _launchWarnings
            }

            // C14 (§9): DB default collation, for the table-variable realization. Rides this same
            // round trip (A59 precedent). Absent (scripted fakes with a one-column row) → null.
            _databaseCollation = noCountRow.Count > 1 ? noCountRow[1]?.ToString() : null;
        }

        _userTypes = UserTypeCatalog.FromResultSet(
            initResult.ResultSets.Count > 1 ? initResult.ResultSets[1] : null);
        _trace.Event("usertypes.load", $"count={_userTypes.Count}");

        // DESIGN §16/C4: optional ownership-chaining impersonation, emitted right
        // after step 2 and BEFORE frame-0 resolution (step 3) — EXECUTE AS changes the
        // security context OBJECT_DEFINITION/VIEW DEFINITION visibility is evaluated
        // under, so an impersonated principal that cannot read the target module's
        // definition is refused naturally by step 3's own "no readable definition"
        // launch failure below — no separate refusal check is needed here.
        if (_options.ExecuteAs is { } executeAs)
        {
            await ExecuteAndTraceAsync($"EXECUTE AS {executeAs};", cancellationToken).ConfigureAwait(false);
        }

        // M8 (§5.4): parse the WHOLE script into one blueprint per non-empty GO batch
        // (procedure mode / single-batch script = one element). ScriptDom parse errors
        // surface here as a launch failure (§5.4 — refuses GO N, whose parse error 46010
        // would otherwise silently truncate the batch list).
        _batches = await ResolveBatchBlueprintsAsync(cancellationToken).ConfigureAwait(false);

        // M8 (§5.4 lifecycle): per-batch scope setup for batch 0 — run BEFORE BEGIN
        // TRANSACTION so batch 0's state table (created-at trancount 0) survives debuggee
        // rollbacks, exactly the M4 frame-0 property. In procedure mode / single-batch
        // scripts this is the ONLY EnterBatchAsync call and no GO boundary ever fires, so
        // launch is byte-identical to M4. Declaration hoisting (fact 14), variable
        // registration, the state table + seed, and table-variable realizations all live
        // in EnterBatchAsync now (shared with batches ≥ 1).
        await EnterBatchAsync(0, createdAtTrancount: 0, cancellationToken).ConfigureAwait(false);

        if (_options.Boost)
        {
            // M6 §14/A21 + the F1 ruling (docs/archive/reviews/m6-boost-core-fable.md §2):
            // create + seed #__dbg_boost HERE — before BEGIN TRAN (trancount 0 → no
            // later debuggee ROLLBACK can destroy it, fact 1) and on the fresh
            // connection (the seed INSERT's SCOPE_IDENTITY clobber, fact 26d, lands
            // on an already-NULL chain — a no-op). Every boosted batch's prologue
            // then takes only its intrinsic-neutral UPDATE branch (fact 26a).
            await ExecuteAndTraceAsync(Batches.ComposedBatchBuilder.BuildBoostSessionInit(), cancellationToken).ConfigureAwait(false);
        }

        // DESIGN §4 step 5: "BEGIN TRANSACTION; — record baseline @@TRANCOUNT = 1."
        await ExecuteAndTraceAsync("BEGIN TRANSACTION;", cancellationToken).ConfigureAwait(false);
        _lastObservedTrancount = 1;                    // §10.4 watchdog edge-detection baseline
        _lastObservedXactState = 1;                    // M5 I3: matches a freshly-opened transaction

        _rewriteEngine = RewriteEngine.CreateDefault();
        _tempNameScope = new FrameChainNameScope(this);
        _rewriteContext = new RewriteContext(_nonce) { TempNames = _tempNameScope };
        _shadows = ShadowValues.Initial();

        // Defaulted-but-omitted parameters (§11.3-style default application, scoped to
        // batch 0's own frame rather than a callee push — procedure mode only; scripts
        // carry no parameters): run their synthetic SET now, after the rewrite engine and
        // BEGIN TRAN, exactly like a mid-flow DECLARE initializer (§7.2/§8.2). The
        // COMMIT-in-body launch warnings (§10.4) are now scanned per batch inside
        // EnterBatchAsync, so batch 0's surface at launch exactly as before.
        foreach (var decl in _batches[0].Parameters)
        {
            if (decl.InitializerSql is not null)
            {
                await RunSyntheticAssignmentAsync(decl, cancellationToken).ConfigureAwait(false);
            }
        }

        _initialized = true;
    }

    // DESIGN §6 "next" + §10.3 routing: performs Current's action, advances (or
    // routes) the cursor, and records the disposition in LastStep. Fault handling per
    // §10 (M3): an ok=0 control row builds an ErrorContext and routes — to the
    // innermost eligible CATCH (M4: in ANY frame, §10.3 step 2's cross-frame walk), or
    // to native statement-level continuation (fact 18), or to a terminal frame fault;
    // executor-level failures with no control row are the §10.1 propagate class. Call
    // only while !IsCompleted && !IsBroken. The driver contract atop ExecutionCursor.cs
    // is the reference this follows.
    public Task<(IReadOnlyList<ResultSet> ResultSets, IReadOnlyList<string> Messages)> StepAsync(CancellationToken cancellationToken = default)
        => StepAsync(StepKind.Over, cancellationToken);

    // M4 (§6): stepIn differs from next only at an eligible EXEC (§11.1) — anywhere
    // else it IS next. The adapter maps DAP stepIn here; stepOut is the adapter's
    // continue-until-depth-shrinks loop (§6), no session verb needed.
    public async Task<(IReadOnlyList<ResultSet> ResultSets, IReadOnlyList<string> Messages)> StepAsync(
        StepKind kind, CancellationToken cancellationToken = default)
    {
        if (!_initialized || _frames is null)
        {
            throw new InvalidOperationException("Session.InitializeAsync must complete before stepping.");
        }

        if (_broken)
        {
            throw new InvalidOperationException(
                "The frame has faulted terminally (§10.3 unhandled) — only inspection and teardown remain.");
        }

        if (IsCompleted)
        {
            throw new InvalidOperationException("Cursor is completed; nothing to step.");
        }

        // M8 (§5.4 / §8.2): cross a GO boundary at the top of a step. Two shapes reach
        // here rather than through AdvanceAndSettle:
        //   • AtBatchBoundary — the batch's cursor was left exhausted by a path that did
        //     NOT settle it in the same step (a statement-level unhandled continuation on
        //     the last SU, fact 21). The common case advances inside AdvanceAndSettle, so
        //     this rarely fires for the NORMAL boundary.
        //   • _pendingBatchAdvance — a BATCH-TERMINAL fault (§8.2/A35) published its
        //     fault-site stop last step (FrameFaulted, NOT broken) and the client now
        //     continues to the next batch (sqlcmd/SSMS default). The cursor is NOT
        //     exhausted here (the terminal arm kept the stack for inspection); the advance
        //     runs the §8.1 reconciliation — including the doom force-rollback — and pops
        //     any callee frames the fault left behind.
        // Either way AdvanceToNextBatch positions the cursor on the next batch's first SU
        // (never a stop on an exhausted cursor). Never fires in procedure mode /
        // single-batch scripts (both predicates require more batches).
        if (AtBatchBoundary || _pendingBatchAdvance)
        {
            var boundaryMessages = new List<string>();
            await AdvanceToNextBatchAsync(boundaryMessages, CancellationToken.None).ConfigureAwait(false);
            LastStep = new StepOutcome(StepDisposition.BatchCompleted);
            return (Array.Empty<ResultSet>(), boundaryMessages);
        }

        // A54 (§6/§11.5): the previous step parked at a MODULE frame's implicit return
        // (StepOnceLockedAsync published that inspection stop; continue/stepOut/RunToEnd ran
        // through to here). Consume it — perform the deferred §11.5 pop, then re-park at the
        // caller's own implicit return (cascade), cross a GO boundary the pop exposed, or —
        // for the root proc — end the session. Mirrors the AtBatchBoundary cross above and
        // is exclusive with it (a script batch frame is never a module frame).
        if (_pendingReturnStop)
        {
            var returnMessages = new List<string>();
            LastStep = await ConsumeReturnStopAsync(returnMessages).ConfigureAwait(false);
            return (Array.Empty<ResultSet>(), returnMessages);
        }

        var frame = CurrentFrame;
        var cursor = frame.Cursor;

        // §10.6 'all' filter, second phase: the fault was already published AT the
        // fault site (FaultAtSite); this call performs the deferred route — no server
        // work. THROW faults stay batch-aborting when unhandled (verified).
        if (_pendingFault is { } pending)
        {
            _pendingFault = null;
            var pendingMessages = new List<string>();
            LastStep = await PerformRouteAsync(pending.Values, pending.Unit, pending.XactState,
                terminalWhenUnhandled: pending.Unit.SubKind == SuSubKind.Throw, pendingMessages).ConfigureAwait(false);
            return (Array.Empty<ResultSet>(), pendingMessages);
        }

        // M4 (§11.1/§11.3): step-into interception. TryStepIntoAsync pushes and returns
        // a disposition, or returns null = fall back to step-over (ineligible callee,
        // C8/C9/C10 shapes, doomed session) — the switch below then executes the EXEC
        // natively, which is itself the faithful surface for erroneous calls.
        if (kind == StepKind.Into && !IsCompleted
            && cursor.Peek() is InterpreterAction.ExecuteUnit execIntent
            && IsStepIntoCandidate(execIntent.Unit))
        {
            var intoMessages = new List<string>();
            var stepInOutcome = await TryStepIntoAsync(execIntent.Unit, intoMessages, cancellationToken).ConfigureAwait(false);
            if (stepInOutcome is not null)
            {
                LastStep = stepInOutcome;
                return (Array.Empty<ResultSet>(), intoMessages);
            }

            if (intoMessages.Count > 0)
            {
                // Fallback notes (e.g. C8/C9/C10) surface with the step-over's results.
                var (overSets, overMessages) = await StepAsync(StepKind.Over, cancellationToken).ConfigureAwait(false);
                return (overSets, intoMessages.Concat(overMessages).ToList());
            }
        }

        switch (cursor.Peek())
        {
            case InterpreterAction.DeclareVariables declareAction:
            {
                // Fact-14 hoisting: registration + state-table DDL happened at init;
                // performing a DECLARE SU means running its initializers only — every
                // time it is reached (a DECLARE in a WHILE body re-runs its initializer
                // per iteration, fact 14 case C). An initializer fault routes like any
                // executable fault (§10.3.1) and abandons the rest of the statement
                // (native statement-level abort covers the whole DECLARE).
                var resultSets = new List<ResultSet>();
                var messages = new List<string>();
                foreach (var decl in declareAction.Declarations)
                {
                    if (decl.InitializerSql is null)
                    {
                        continue;
                    }

                    var batch = ComposeDebuggeeBatch(() => ComposedBatchBuilder.BuildSyntheticAssignment(
                        frame, _rewriteEngine!, _rewriteContext!, decl, frame.Cursor.Index.FullScript, _shadows!, DebuggeeComposition(frame)));
                    // A14 refinement (review doc §3): a multi-initializer DECLARE runs
                    // one composed batch per initializer — earlier ones have already
                    // executed when a later composition reveals the doomed reference,
                    // so a stop-before-the-SU is no longer placeable. Initializers
                    // take A14's RunToEnd arm unconditionally: diagnostic + proceed,
                    // terminal-note annotation still armed.
                    NoteDoomedTempInitializer(decl.Name, messages);
                    var faultOutcome = await RunDebuggeeBatchAsync(batch, declareAction.Unit, resultSets, messages, cancellationToken).ConfigureAwait(false);
                    if (faultOutcome is not null)
                    {
                        LastStep = faultOutcome;               // cursor already placed by routing
                        return (resultSets, messages);
                    }
                }

                LastStep = await AdvanceAndSettleAsync(AdvanceSignal.Normal, messages).ConfigureAwait(false);
                return (resultSets, messages);
            }

            case InterpreterAction.TableVarDeclare:
            {
                // M4 (§9/R1, D7): a stoppable no-op — the realization was hoisted to
                // frame init/push, and DECLARE @t TABLE has no runtime action (a
                // re-reached DECLARE resets nothing natively).
                var messages = new List<string>();
                LastStep = await AdvanceAndSettleAsync(AdvanceSignal.Normal, messages).ConfigureAwait(false);
                return (Array.Empty<ResultSet>(), messages);
            }

            case InterpreterAction.ExecuteUnit executeAction:
            {
                // §5.4/A48: module-creating DDL (CREATE/ALTER PROCEDURE/FUNCTION/VIEW/TRIGGER)
                // runs BARE — its own raw batch, no §7.1 oracle wrapper — because it must be the
                // first statement of its batch and is illegal inside a TRY (a wrapped
                // `CREATE OR ALTER` even parse-errors, msg 156 near 'OR'). Handled entirely by
                // ExecuteModuleDdlAsync, never the composed-batch path below.
                if (executeAction.Unit.SubKind == SuSubKind.ModuleDdl)
                {
                    return await ExecuteModuleDdlAsync(executeAction.Unit, cancellationToken).ConfigureAwait(false);
                }

                var resultSets = new List<ResultSet>();
                var messages = new List<string>();
                // D5/A13 (§10.1, facts 23-H + 24): a stepped-over EXEC (including a
                // step-into that fell back to Over) with no armed TRY in ANY frame
                // composes oracle-free — the wrapper TRY would impose transfer
                // semantics native only has when a TRY is armed. Doomed sessions keep
                // the oracle (documented A13 residual).
                // C11 (A64): in a capture frame (stepped into `INSERT <target> EXEC proc`), a
                // result-returning statement is composed as `INSERT INTO <target> <statement>` so the
                // callee's result stream lands in the target instead of streaming to the client.
                var capturing = frame.CaptureTargetSql is not null
                    && ResultCaptureClassifier.IsResultReturning(executeAction.Unit.Fragment);
                var oracleFree = executeAction.Unit.SubKind == SuSubKind.Execute
                    && !_doomed && !AnyArmedTryExists() && !capturing;
                // C13: the debuggee's own SET ROWCOUNT sets a non-zero limit its own batch must
                // neutralize after capture, to keep the connection at rest (0) for later bookkeeping.
                var baseComposition = DebuggeeComposition(frame) with
                {
                    ResetRowCountAfterStatement = executeAction.Unit.Fragment is SetRowCountStatement,
                    CaptureTargetSql = capturing ? frame.CaptureTargetSql : null,
                };
                var composition = oracleFree ? baseComposition with { OracleFree = true } : baseComposition;
                // A63 (§9): a cursor-variable assignment `SET @c = CURSOR <def>` is composed as a
                // generated `DECLARE [phys] CURSOR GLOBAL <def>` (its `SET…CURSOR` prefix is not
                // span-patchable — §7.4 invariant 1), reifying @c as a frame-unique GLOBAL cursor.
                var batch = executeAction.Unit.Fragment is SetVariableStatement { CursorDefinition: not null }
                    ? ComposeDebuggeeBatch(() => ComposedBatchBuilder.BuildForCursorVariableAssign(
                        frame, _rewriteEngine!, _rewriteContext!, executeAction.Unit, frame.Cursor.Index.FullScript, _shadows!, composition))
                    : ComposeDebuggeeBatch(() => ComposedBatchBuilder.BuildForUnit(
                        frame, _rewriteEngine!, _rewriteContext!, executeAction.Unit, frame.Cursor.Index.FullScript, _shadows!, composition));
                if (PreflightDoomedTempStop(executeAction.Unit, messages) is { } preflight)
                {
                    LastStep = preflight;                  // A14: nothing executed; cursor unchanged
                    return (resultSets, messages);
                }

                var faultOutcome = oracleFree
                    ? await RunOracleFreeExecBatchAsync(batch, executeAction.Unit, resultSets, messages, cancellationToken).ConfigureAwait(false)
                    : await RunDebuggeeBatchAsync(batch, executeAction.Unit, resultSets, messages, cancellationToken).ConfigureAwait(false);
                if (faultOutcome is not null)
                {
                    LastStep = faultOutcome;
                    return (resultSets, messages);
                }

                // §7.2/§10.4: track the debuggee's runtime XACT_ABORT (fact-19
                // sandwich restore + System scope). Parse-time options (QI/ANSI_NULLS)
                // are pinned per frame — fact 16: a mid-MODULE SET of those is a runtime
                // no-op, and the batch preamble re-asserts the frame env anyway.
                if (executeAction.Unit.Fragment is PredicateSetStatement { Options: var setOptions, IsOn: var isOn })
                {
                    if ((setOptions & SetOptions.XactAbort) != 0)
                    {
                        frame.XactAbortOn = isOn;
                    }

                    // A49 (§5.4/§11.2): the exception to fact 16's "no-op" is the ad-hoc
                    // SCRIPT batch (script mode, a non-pushed frame — CallSite is null),
                    // which is NOT a module: there a runtime SET QUOTED_IDENTIFIER /
                    // ANSI_NULLS genuinely takes effect for the FOLLOWING statements (and
                    // carries across GO — fact 32d, via EnterBatchAsync's outgoing.SetEnv).
                    // Fold it into the frame env so the next composed batch re-pins from
                    // the new value: @@OPTIONS then reflects it and later statements
                    // compile under it. Module frames (a stepped-into proc, CallSite !=
                    // null; or procedure mode) stay pinned — fact 16 intact.
                    if (_options.Mode == LaunchMode.Script && frame.CallSite is null
                        && (setOptions & (SetOptions.QuotedIdentifier | SetOptions.AnsiNulls)) != 0)
                    {
                        frame.SetEnv = new SetOptionEnvironment(
                            (setOptions & SetOptions.QuotedIdentifier) != 0 ? isOn : frame.SetEnv.QuotedIdentifier,
                            (setOptions & SetOptions.AnsiNulls) != 0 ? isOn : frame.SetEnv.AnsiNulls);
                    }

                    // C5 (§7.2/§21): the debuggee's OWN SET NOCOUNT is cosmetically
                    // executed but has no observable effect — every composed batch's
                    // preamble re-forces NOCOUNT ON regardless (§7.1). One-time console
                    // note, whichever trigger (this, or the session-init @@OPTIONS
                    // probe) fires first.
                    if ((setOptions & SetOptions.NoCount) != 0)
                    {
                        NoteNoCountOnce();
                    }
                }

                // M4 (D6): fold every executed SET SU into the §11.2 restore tracker.
                if (executeAction.Unit.SubKind == SuSubKind.SetOption)
                {
                    _runtimeOptions.RecordExecuted(executeAction.Unit.Fragment);
                    NoteUntrackedOptionsOnce();

                    // C13 (F2): SET ROWCOUNT <non-literal> — RecordExecuted only reads a literal, so
                    // resolve the expression against the frame and record it; otherwise the debuggee's
                    // subsequent statements would run UNLIMITED where native limits them (the value is
                    // live on the connection but has no read-back intrinsic). A literal is already
                    // recorded above; a faulting/NULL eval leaves the tracked value unchanged.
                    if (executeAction.Unit.Fragment is SetRowCountStatement { NumberRows: { } rows and not Literal })
                    {
                        var resolved = await ResolveRowCountValueAsync(frame, rows, executeAction.Unit, cancellationToken)
                            .ConfigureAwait(false);
                        if (resolved is not null)
                        {
                            _runtimeOptions.SetRowCount(resolved);
                        }
                    }
                }

                // C2 (§21): DML against a table/view with triggers executes atomically
                // under the debugger (trigger-side effects are not independently
                // steppable) — a cached, lazy sys.triggers lookup; one console note
                // per object per session.
                if (DmlTargetClassifier.TryGetTargetTableName(executeAction.Unit.Fragment) is { } dmlTarget)
                {
                    await NoteDmlTriggersOnceAsync(dmlTarget, cancellationToken).ConfigureAwait(false);
                }

                // M4 (§9): registry effects of the executed statement (temp-table DDL,
                // cursor declares, drops/deallocates).
                RecordRegistryEffects(executeAction.Unit, frame);

                LastStep = await AdvanceAndSettleAsync(AdvanceSignal.Normal, messages).ConfigureAwait(false);
                return (resultSets, messages);
            }

            case InterpreterAction.EvaluatePredicate predicateAction:
            {
                // DESIGN §6 M2 / engine fact 12: predicate evaluation goes through the
                // normal pipeline (rewritten, error-wrapped, faultable) but its control
                // row is a wrapper artifact — never ObserveSuccess it — and evaluating a
                // debuggee predicate resets @@ROWCOUNT/@@ERROR to 0 natively, regardless
                // of the predicate's own truth value. A faulting predicate routes per
                // §10.3.1 (on the unhandled-continue path it takes the FALSE branch with
                // @@ERROR = the fault number — fact 21 P1/P6, NOT the fact-12 reset).
                var batch = ComposeDebuggeeBatch(() => ComposedBatchBuilder.BuildForPredicate(
                    frame, _rewriteEngine!, _rewriteContext!, predicateAction.Predicate, frame.Cursor.Index.FullScript, _shadows!, DebuggeeComposition(frame)));
                var preflightMessages = new List<string>();
                if (PreflightDoomedTempStop(predicateAction.Unit, preflightMessages) is { } preflight)
                {
                    LastStep = preflight;                  // A14: cursor stays ON the IF/WHILE
                    return (Array.Empty<ResultSet>(), preflightMessages);
                }

                var run = await RunFaultableBatchAsync(batch, predicateAction.Unit, cancellationToken).ConfigureAwait(false);
                if (run.Outcome is not null)
                {
                    LastStep = run.Outcome;
                    return (run.UserSets, run.Messages);
                }

                _shadows!.ObservePredicateEvaluation();
                var predicateValue = ExtractScalarColumn(run.UserSets, batch);
                var predicateMessages = new List<string>(run.Messages);
                LastStep = await AdvanceAndSettleAsync(
                    new AdvanceSignal.PredicateEvaluated(predicateValue == 1), predicateMessages).ConfigureAwait(false);
                // The "p" wrapper result set is not the debuggee's own output — never
                // forward it to the Debug Console (only messages, e.g. from a PRINT
                // inside a scalar function the predicate happens to call, can surface).
                return (Array.Empty<ResultSet>(), predicateMessages);
            }

            case InterpreterAction.Jump:
            {
                // GOTO/BREAK/CONTINUE: pure cursor bookkeeping, no server round trip —
                // the cursor performs the jump itself on Advance. Leaving a CATCH this
                // way pops its error context (§10.2) via the reconcile.
                var messages = new List<string>();
                LastStep = await AdvanceAndSettleAsync(AdvanceSignal.Normal, messages).ConfigureAwait(false);
                return (Array.Empty<ResultSet>(), messages);
            }

            case InterpreterAction.ReturnFromFrame returnAction:
            {
                var messages = new List<string>();
                if (returnAction.Expression is { } expression)
                {
                    var batch = ComposeDebuggeeBatch(() => ComposedBatchBuilder.BuildForScalarEval(
                        frame, _rewriteEngine!, _rewriteContext!, expression, frame.Cursor.Index.FullScript, _shadows!, DebuggeeComposition(frame)));
                    if (PreflightDoomedTempStop(returnAction.Unit, messages) is { } preflight)
                    {
                        LastStep = preflight;              // A14: cursor stays ON the RETURN
                        return (Array.Empty<ResultSet>(), messages);
                    }

                    var run = await RunFaultableBatchAsync(batch, returnAction.Unit, cancellationToken).ConfigureAwait(false);
                    if (run.Outcome is not null)
                    {
                        // A faulted RETURN expression routes like any statement
                        // (§10.3.1); on the unhandled-continue path PerformRoute has
                        // already completed the frame with status 0 + the native
                        // "return a status of NULL" info message (fact 21 P8 — the
                        // engine RETURNS anyway; it does not skip the RETURN) and, in a
                        // callee, popped it as a COMPLETED call (fact 23).
                        LastStep = run.Outcome;
                        messages.AddRange(run.Messages);
                        return (run.UserSets, messages);
                    }

                    // §6 M2 D9: shadows after a RETURN eval — no ObserveSuccess (the
                    // wrapper SELECT is not native truth); M4 keeps that rule for
                    // callee frames too (@@ROWCOUNT/@@ERROR carry the callee's LAST
                    // REAL statement across the pop, which is native module-exit).
                    frame.ReturnCode = ExtractScalarColumn(run.UserSets, batch);
                    messages.AddRange(run.Messages);
                }
                else
                {
                    // §11.5: bare RETURN returns 0.
                    frame.ReturnCode = 0;
                }

                // Completes the frame body; in a callee, SettleCompletion pops it as a
                // COMPLETED call (§11.5/fact 23: copy-back + @rc happen).
                LastStep = await AdvanceAndSettleAsync(AdvanceSignal.Normal, messages).ConfigureAwait(false);
                return (Array.Empty<ResultSet>(), messages);
            }

            case InterpreterAction.WaitFor waitForAction:
            {
                if (_options.WaitFor == WaitForMode.Skip)
                {
                    // Fact 17 (M3): a real WAITFOR resets @@ROWCOUNT/@@ERROR to 0 —
                    // skip mode must mirror it or the next shadow read diverges.
                    _shadows!.ObserveWaitFor();
                    var skipMessages = new List<string>
                    {
                        $"WAITFOR at line {waitForAction.Unit.Span.StartLine} skipped (launch config waitfor:\"skip\").",
                    };
                    LastStep = await AdvanceAndSettleAsync(AdvanceSignal.Normal, skipMessages).ConfigureAwait(false);
                    return (Array.Empty<ResultSet>(), skipMessages);
                }

                // "honor": treated exactly like any other executable statement — the
                // server genuinely blocks for the delay under the one-command-at-a-time
                // model (§3/C20); faults (incl. §10.5 timeout) take the standard paths.
                var resultSets = new List<ResultSet>();
                var messages = new List<string>();
                var batch = ComposeDebuggeeBatch(() => ComposedBatchBuilder.BuildForUnit(
                    frame, _rewriteEngine!, _rewriteContext!, waitForAction.Unit, frame.Cursor.Index.FullScript, _shadows!, DebuggeeComposition(frame)));
                if (PreflightDoomedTempStop(waitForAction.Unit, messages) is { } preflight)
                {
                    LastStep = preflight;                  // A14 (vacuous for WAITFOR; kept uniform)
                    return (resultSets, messages);
                }

                var faultOutcome = await RunDebuggeeBatchAsync(batch, waitForAction.Unit, resultSets, messages, cancellationToken).ConfigureAwait(false);
                if (faultOutcome is not null)
                {
                    LastStep = faultOutcome;
                    return (resultSets, messages);
                }

                LastStep = await AdvanceAndSettleAsync(AdvanceSignal.Normal, messages).ConfigureAwait(false);
                return (resultSets, messages);
            }

            case InterpreterAction.Rethrow rethrowAction:
            {
                // §10.2 bare THROW: interpreted client-side — the saved context IS the
                // exact original error (number/severity/state/message preserved better
                // than any server-side re-raise could). Routing starts from the
                // enclosing scope automatically (the rethrow's own CATCH entry is
                // consumed); unhandled = terminal (THROW is batch-aborting, verified).
                if (_errorContexts.Count == 0)
                {
                    throw new InvalidOperationException(
                        "Bare THROW with no active error context — validation (10704) should have refused. Internal bug.");
                }

                var messages = new List<string>();
                var top = _errorContexts[^1];
                LastStep = await PerformRouteAsync(top.Values, rethrowAction.Unit, _doomed ? -1 : 1,
                    terminalWhenUnhandled: true, messages).ConfigureAwait(false);
                return (Array.Empty<ResultSet>(), messages);
            }

            default:
                throw new NotSupportedException($"Unhandled interpreter action {cursor.Peek().GetType().Name} — internal bug.");
        }
    }

    // Predicate/scalar-eval batches (ComposedBatchBuilder.BuildForPredicate/
    // BuildForScalarEval) return exactly one user result set with one row, one column
    // aliased "p" — never forwarded to the console; the driver reads it directly.
    private static int ExtractScalarColumn(IReadOnlyList<ResultSet> userSets, Batches.ComposedBatch batch)
    {
        if (userSets is not [{ Rows: [var row, ..] } resultSet] || row.Count == 0)
        {
            throw new InvalidOperationException(
                $"Predicate/scalar-eval batch did not return the expected single-row 'p' result set (builder bug).\n{batch.Text}");
        }

        return Convert.ToInt32(row[0]);
    }

    // ---------------------------------------------------------------------------------
    // M3 §10 machinery (design: docs/archive/reviews/m3-error-model-design-notes-fable.md).
    // ---------------------------------------------------------------------------------

    // The per-batch §10 knobs, derived from session state: §10.7 re-materialization
    // while a context is active; the fact-19 XACT_ABORT sandwich needs the tracked
    // frame setting; §10.4 doomed mode seeds values from the EXECUTING FRAME's
    // snapshot (M4), never writes, and re-materializes the doomed transaction itself
    // (fact 22: doom cannot survive the previous batch's end — the engine 3998-rolled
    // it back). While doomed the healthy prefix also re-creates rollback-destroyed
    // table-variable realizations (D8/C25).
    private BatchComposition DebuggeeComposition(Frame frame) => new()
    {
        RowCount = _runtimeOptions.CurrentRowCount,   // C13: restore value for the TVP-copy wrap (§11.2)
        Rematerialize = _errorContexts.Count > 0 ? _errorContexts[^1].Values : null,
        XactAbortOn = frame.XactAbortOn,
        DoomedSeedValues = _doomed ? frame.Snapshot : null,
        IncludeStateWrite = !_doomed,
        RedoomTrancount = _doomed ? _doomTrancount : null,
        HealthyPrefixDdl = _doomed ? CollectHealingDdl() : null,
    };

    // A14 (§10.4): compose ONE debuggee batch with doomed-temp capture on — the name
    // scope records every live user-#temp registry entry the R2 rewrite resolves while
    // the session is doomed. The gate keeps debugger-initiated compositions (REPL,
    // watch, breakpoint conditions) from polluting the per-SU hit list.
    private Batches.ComposedBatch ComposeDebuggeeBatch(Func<Batches.ComposedBatch> build)
    {
        _doomedTempResolutions.Clear();
        _captureDoomedTemps = true;
        try
        {
            var batch = build();
            NoteIdentityChainMove(batch);
            return batch;
        }
        finally
        {
            _captureDoomedTemps = false;
        }
    }

    // A59 (§7.4/§9, fact 34h): the batch's §9 preamble INSERTed into a table variable that has
    // an IDENTITY column, which moves the connection's identity chain — SCOPE_IDENTITY()
    // included (probed: it overwrote a real table's value). The post-statement capture would
    // therefore read the DEBUGGER's insert, not the debuggee's, for any statement that did not
    // itself insert an identity — so poison the A26/D1 chain and let the R6 shadow keep serving
    // its client-modeled value. This is the same flag, and the same recovery, a frame pop uses:
    // ObserveDebuggeeSuccess re-synchronizes on the next completed insert-family statement,
    // whose own insert lands LAST and is therefore native truth. (@@IDENTITY is served live and
    // never rewritten — its perturbation by the same INSERT is caveat C26, not fixable here.)
    private void NoteIdentityChainMove(Batches.ComposedBatch batch)
    {
        if (!batch.MovesIdentityChain || _scopeChainPoisoned)
        {
            return;
        }

        _scopeChainPoisoned = true;
        _trace.Event("scopeid.poison", "tvp-materialization moved the identity chain (§9/A59, fact 34h)");
    }

    // §10.4 A14 (ratified 2026-07-06 — docs/archive/reviews/m4-c23-doom-temp-severity-fable.md
    // §4.3): the batch just composed resolved references through live §9 user-#temp
    // registry entries while doomed — objects the fact-22 forced rollback destroyed
    // with certainty (C23's object-existence face; the 208 it will raise is
    // same-scope-uncatchable, §10.1). First arrival at the SU publishes a diagnostic
    // stop BEFORE any server work (two-phase, the §10.6 FaultAtSite pend shape; the
    // RunToEnd loop reads it as a console note and simply steps again). The next
    // StepAsync executes the batch anyway — routing per §10.1/§10.3 unchanged — with
    // the original names riding along so a resulting TERMINAL fault carries the C23
    // citation. References that never resolved through the registry take none of this
    // path: a genuine, unrelated 208 cannot ambiguate by construction.
    private StepOutcome? PreflightDoomedTempStop(Interpreter.StatementUnit unit, List<string> messages)
    {
        _c23TerminalNote = null;
        if (!_doomed || _doomedTempResolutions.Count == 0)
        {
            _armedC23Unit = null;                  // stale arm (e.g. doom exited underneath it) never fires
            return null;
        }

        var originals = _doomedTempResolutions
            .Select(e => e.OriginalName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _doomedTempResolutions.Clear();

        if (ReferenceEquals(_armedC23Unit, unit))
        {
            _armedC23Unit = null;                  // second phase: execute anyway
            _c23TerminalNote = originals;
            return null;
        }

        _armedC23Unit = unit;
        var names = string.Join(", ", originals);
        var diagnostic =
            $"Caveat C23 (§10.4/§21): {names} no longer exists under the debugger — it was created inside the " +
            "now-doomed transaction, which the engine force-rolled back at the previous step's batch end " +
            "(fact 22); native code at this point still sees the table. Executing this statement will raise a " +
            "real error 208 that no same-scope TRY/CATCH can catch (§10.1): it routes to a caller frame's CATCH " +
            "if one is armed, otherwise the session terminates. Step/continue to execute anyway, or use " +
            "Jump to Cursor to skip the statement.";
        messages.Add(diagnostic);
        _trace.Event("txn.c23.preflight", $"line={unit.Span.StartLine} tables={names}");
        return new StepOutcome(StepDisposition.DoomedTempPreflight,
            new ErrorContextValues(208, 16, 1, unit.Span.StartLine, null, diagnostic));
    }

    // A14's initializer arm (see the DECLARE site's comment): no stop is placeable
    // mid-DECLARE, so this is diagnostic + proceed — the terminal-note annotation
    // still rides to PerformRouteAsync if the batch dies.
    private void NoteDoomedTempInitializer(string declarationName, List<string> messages)
    {
        _c23TerminalNote = null;
        if (!_doomed || _doomedTempResolutions.Count == 0)
        {
            return;
        }

        var originals = _doomedTempResolutions
            .Select(e => e.OriginalName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _doomedTempResolutions.Clear();
        _c23TerminalNote = originals;

        var names = string.Join(", ", originals);
        messages.Add(
            $"Caveat C23 (§10.4/§21): the initializer of {declarationName} references {names}, created inside " +
            "the now-doomed transaction and destroyed by the engine's forced rollback (fact 22) — native code " +
            "would still see it. The reference will raise a real, same-scope-uncatchable error 208 (§10.1).");
        _trace.Event("txn.c23.preflight", $"initializer={declarationName} tables={names} (note only)");
    }

    private sealed record FaultableRun(
        StepOutcome? Outcome, ControlRow? Control, IReadOnlyList<ResultSet> UserSets, IReadOnlyList<string> Messages);

    // Executes one composed batch with full §10 fault handling. Outcome == null means
    // success: the caller observes shadows and advances. Otherwise the cursor is
    // already placed (routed / skipped / unchanged for FaultAtSite, CommandTimeout and
    // terminal) and Outcome is the step's disposition. The watchdog (§10.4) observes
    // every control row either way; the __dbg_state set refreshes the binary snapshot.
    // deferRoute (A65/F2, Fable §10 review): when true, a fault does NOT route here — it is PENDED
    // (like the §10.6 'all' filter) so the NEXT StepAsync performs the route at top level. The
    // capture flush (§11.7) runs from INSIDE a pop, and routing re-entrantly there — its walk itself
    // abnormal-pops frames — corrupts the interpreter's frame/error-context state. Pending defers the
    // route to a clean top-level context and handles a doomed-write 3930 and a healthy 515/547 alike.
    private async Task<FaultableRun> RunFaultableBatchAsync(
        ComposedBatch batch, Interpreter.StatementUnit unit, CancellationToken cancellationToken,
        bool deferRoute = false)
    {
        BatchResult batchResult;
        try
        {
            batchResult = await ExecuteAndTraceAsync(batch, cancellationToken).ConfigureAwait(false);
        }
        catch (StatementExecutionException ex)
        {
            var failureMessages = new List<string>();
            var failureOutcome = await HandleExecutorFailureAsync(ex, unit, failureMessages, cancellationToken, deferRoute).ConfigureAwait(false);
            return new FaultableRun(failureOutcome, null, Array.Empty<ResultSet>(), failureMessages);
        }

        var executingFrame = CurrentFrame;
        var (control, userSets, stateValues) = ControlRowParser.Parse(batchResult, executingFrame.Variables.Count);
        if (stateValues is not null)
        {
            executingFrame.Snapshot = stateValues.ToArray();   // M4: snapshots are per frame (D1)
        }

        if (batchResult.TrailingErrors is { Count: > 0 } trailing && control.XactState != -1)
        {
            // Fact 22: trailing batch-end errors (3998) are legal ONLY as the doomed
            // batch's epilogue. A healthy control row followed by one is a state the
            // engine model says cannot happen — classify it like the executor failure
            // it would have been before the epilogue tolerance existed (§10.1).
            var first = trailing[0];
            var trailingMessages = new List<string>();
            var trailingOutcome = await HandleExecutorFailureAsync(
                new StatementExecutionException(first.Message, first.SeverityClass, first.Number),
                unit, trailingMessages, cancellationToken, deferRoute).ConfigureAwait(false);
            return new FaultableRun(trailingOutcome, null, Array.Empty<ResultSet>(), trailingMessages);
        }

        var messages = new List<string>(batchResult.Messages);
        await ObserveControlRowAsync(control, unit, messages).ConfigureAwait(false);

        if (control.Ok)
        {
            return new FaultableRun(null, control, userSets, messages);
        }

        var values = BuildErrorContextValues(control, batch, unit, executingFrame.Module);   // A27: origin = the faulting frame
        var outcome = BreakOnAllErrors || deferRoute
            ? PendFault(values, unit, control.XactState)
            : await PerformRouteAsync(values, unit, control.XactState,
                terminalWhenUnhandled: unit.SubKind == SuSubKind.Throw, messages).ConfigureAwait(false);
        // Partial result sets that streamed before the fault are genuine debuggee
        // output (native clients saw them too) — forward them.
        return new FaultableRun(outcome, control, userSets, messages);
    }

    // D5/A13 (§10.1, fact 24): the oracle-free stepped-over EXEC runner. Preconditions
    // (enforced by the caller + builder guards): healthy session, no armed TRY in any
    // frame, SuSubKind.Execute. The batch runs under client-side absorption — severity
    // ≤ 16 errors arrive as AbsorbedErrors/Messages, the callee continues natively past
    // statement-level errors (fact 23-H), and the control row streams unless a
    // batch-aborting error killed the physical batch (fact 24 shape b, incl. the
    // no-3998 refinement of shape c). Success → ObserveSuccess (err_after-aware) + null;
    // fault shapes → the disposition, mirroring RunFaultableBatchAsync's contract.
    private async Task<StepOutcome?> RunOracleFreeExecBatchAsync(
        ComposedBatch batch, Interpreter.StatementUnit unit,
        List<ResultSet> resultSets, List<string> messages, CancellationToken cancellationToken)
    {
        await EnsureTransactionProtectionAsync(unit, messages, cancellationToken).ConfigureAwait(false);

        _trace.Event("batch.send", batch.Text);
        BatchResult batchResult;
        try
        {
            batchResult = await _executor.ExecuteAbsorbingAsync(batch.Text, cancellationToken).ConfigureAwait(false);
        }
        catch (StatementExecutionException ex)
        {
            // Severity ≥ 17, attention/timeout, transport: the §10.1 classes are
            // unchanged by absorption — classify exactly like every other batch.
            var failureMessages = new List<string>();
            var failureOutcome = await HandleExecutorFailureAsync(ex, unit, failureMessages, cancellationToken).ConfigureAwait(false);
            messages.AddRange(failureMessages);
            return failureOutcome;
        }

        TraceBatchResult(batchResult);
        if (batchResult.AbsorbedErrors is { Count: > 0 } absorbed)
        {
            _trace.Event("batch.absorbed",
                string.Join("; ", absorbed.Select(a => $"{a.Number} sev{a.SeverityClass}: {a.Message}")));
        }

        var executingFrame = CurrentFrame;
        if (!ControlRowParser.TryParse(batchResult, executingFrame.Variables.Count, out var control, out var userSets, out var stateValues))
        {
            // Fact 24 shape (b): a batch-aborting error inside the callee killed the
            // physical batch under absorption — no exception, no control row, and (per
            // shape c) no 3998 epilogue: XACT_ABORT's abort-and-rollback already
            // resolved the transaction as part of the statement. Natively the whole
            // batch dies with every frame; the no-armed-TRY precondition means the
            // §10.3 walk could not route anyway — go terminal with the absorbed tail's
            // final error as the fault (the aborting error is the last one raised).
            messages.AddRange(batchResult.Messages);
            if (batchResult.AbsorbedErrors is { Count: > 0 } tail)
            {
                var last = tail[^1];
                var values = new ErrorContextValues(
                    last.Number, last.SeverityClass, 1, unit.Span.StartLine, null, last.Message);
                return await PerformRouteAsync(values, unit, xactState: 0,
                    terminalWhenUnhandled: true, messages).ConfigureAwait(false);
            }

            throw new SessionFaultException(
                "Oracle-free EXEC batch returned neither a control row nor absorbed errors — " +
                "not an engine shape fact 24 predicts (builder/transport bug, D5/A13).");
        }

        if (stateValues is not null)
        {
            executingFrame.Snapshot = stateValues.ToArray();
        }

        if (batchResult.TrailingErrors is { Count: > 0 } trailing && control!.XactState != -1)
        {
            // Same rule as RunFaultableBatchAsync: trailing batch-end errors are legal
            // only as the doomed epilogue (a callee's own caught-and-continued doom can
            // legitimately produce one here — the batch then continued to a natural end
            // still doomed, fact 22's original shape).
            var first = trailing[0];
            var trailingMessages = new List<string>();
            var trailingOutcome = await HandleExecutorFailureAsync(
                new StatementExecutionException(first.Message, first.SeverityClass, first.Number),
                unit, trailingMessages, cancellationToken).ConfigureAwait(false);
            messages.AddRange(trailingMessages);
            return trailingOutcome;
        }

        messages.AddRange(batchResult.Messages);
        await ObserveControlRowAsync(control!, unit, messages).ConfigureAwait(false);

        // No CATCH arm exists in this shell — a streamed control row IS the Ok row.
        // Absorbed statement-level errors (already forwarded above as native client
        // text) did not stop the callee (fact 23-H) and the EXEC's own caller-scope
        // @@ERROR rides err_after into the shadows (fact 24 d).
        resultSets.AddRange(userSets);
        // §7.4/A26 (D1): a stepped-over EXEC is never insert-family, so while the chain
        // is poisoned this skips the capture (a real EXEC proc runs in a true module
        // scope — it moves neither the caller's native chain nor, then, the shadow).
        ObserveDebuggeeSuccess(control!, unit);
        return null;
    }

    // §5.4/A48: execute a module-creating DDL statement (CREATE/ALTER PROCEDURE/FUNCTION/
    // VIEW/TRIGGER) BARE — its exact original source slice as its own batch, run WHOLE on
    // the server (what the user expects, and what SSMS does per GO batch). It must be the
    // first statement of its batch and cannot live inside a TRY block, so it can NEVER go
    // through the §7.1 oracle-composed batch: a `BEGIN TRY CREATE OR ALTER …` parse-errors
    // (msg 156 near 'OR'), a prologue before it would hit msg 111, and a trailing control-row
    // SELECT would be swallowed into a body-less proc's stored definition. Two invariants make
    // bare execution safe and simple:
    //   • Always first in its batch ⇒ (a doomed transaction cannot cross a GO boundary, fact
    //     22) it is NEVER reached while doomed — no redoom/param path is needed.
    //   • State-neutral: it stores a DEFINITION, touching no variable, #temp, or transaction —
    //     so no §7.3 control row is needed to settle. Keep the snapshot; reset only the
    //     intrinsics a DDL zeroes natively (@@ROWCOUNT/@@ERROR → 0, verified live; SCOPE_IDENTITY
    //     unchanged); advance. The slice is sent VERBATIM, never rewritten — a module body
    //     resolves its #temp/cursor references at call time, not against the debugger's session
    //     objects (§5.3 original-source contract; R-rules must not touch a stored definition).
    // A fault (a compile error in the body, 2714 duplicate, a permission error) surfaces
    // natively via the executor exception and routes per §10.1/§10.3 (batch-aborting ⇒ continue
    // at the next batch), exactly like any other executable leaf.
    private async Task<(IReadOnlyList<ResultSet>, IReadOnlyList<string>)> ExecuteModuleDdlAsync(
        Interpreter.StatementUnit unit, CancellationToken cancellationToken)
    {
        var resultSets = new List<ResultSet>();
        var messages = new List<string>();

        // A module DDL creates a persistent object — a database write — so if the safety
        // transaction was detached (§10.4), re-open it FIRST so teardown can still roll the
        // CREATE back (§16). Detached is the only non-healthy pre-state possible here.
        await EnsureTransactionProtectionAsync(unit, messages, cancellationToken).ConfigureAwait(false);

        _trace.Event("batch.send", unit.Text);
        BatchResult batchResult;
        try
        {
            batchResult = await _executor.ExecuteAsync(unit.Text, cancellationToken).ConfigureAwait(false);
        }
        catch (StatementExecutionException ex)
        {
            var failureMessages = new List<string>();
            LastStep = await HandleExecutorFailureAsync(ex, unit, failureMessages, cancellationToken).ConfigureAwait(false);
            messages.AddRange(failureMessages);
            return (resultSets, messages);
        }

        TraceBatchResult(batchResult);
        messages.AddRange(batchResult.Messages);
        _shadows!.ObserveModuleDdl();   // @@ROWCOUNT/@@ERROR → 0 (verified live); SCOPE_IDENTITY unchanged
        InvalidateModuleCaches();       // A55: the script just CREATE/ALTER/DROP'd a module — evict its stale cached definition

        LastStep = await AdvanceAndSettleAsync(AdvanceSignal.Normal, messages).ConfigureAwait(false);
        return (resultSets, messages);
    }

    // D5/A13: the session-level half of the oracle-free trigger — an armed TRY in ANY
    // frame means a fault would natively transfer (fact 23 C/D), so the oracle stays.
    private bool AnyArmedTryExists()
    {
        foreach (var frame in _frames!.All)
        {
            if (frame.Cursor.HasArmedTry)
            {
                return true;
            }
        }

        return false;
    }

    // ---------------------------------------------------------------------------------
    // M6 §14/A21 boost machinery (Fable lane — design docs/archive/reviews/
    // m6-boost-design-notes-fable.md §2 B1–B10 + the F1/F2 rulings in
    // docs/archive/reviews/m6-boost-core-fable.md §2).
    // ---------------------------------------------------------------------------------

    private int _boostSeq;                                     // B4: per-batch, stale-proofs the B7 recovery read

    /// <summary>
    /// DESIGN §14/A21 (B1): the continue-path-only boost dispatch. Callers (the
    /// adapter's RunUntilAsync under continue/stepOut, the harness's boost-mode
    /// RunToEndAsync — both Sonnet item 6) invoke this at every arrival; null means
    /// boost did not fire (disabled, wrong position, pending §10.6 fault, or a
    /// planner refusal — refusals trace <c>boost.refuse</c> with a reason code) and
    /// the caller falls back to <see cref="StepAsync(CancellationToken)"/>. Non-null
    /// = the whole node executed (or its fault/attention settled) as ONE step;
    /// <see cref="LastStep"/> carries the disposition exactly as StepAsync would.
    /// <paramref name="isBlocked"/> is the caller's breakpoint/logpoint predicate
    /// over member SUs (B1: Core stays ignorant of DAP breakpoint storage).
    /// </summary>
    public async Task<(IReadOnlyList<ResultSet> ResultSets, IReadOnlyList<string> Messages)?> TryStepBoostedAsync(
        Func<Interpreter.StatementUnit, bool>? isBlocked = null, CancellationToken cancellationToken = default)
    {
        if (!_initialized || _frames is null)
        {
            throw new InvalidOperationException("Session.InitializeAsync must complete before stepping.");
        }

        // M8 §8.2: a pending batch-terminal advance is not a boostable rest — the next
        // StepAsync crosses the GO boundary. Gate boost off it like _pendingFault so the
        // RunToEnd loop's TryStepBoosted-first call defers to StepAsync's boundary cross.
        // A54: same for a parked implicit return (_pendingReturnStop) — the next StepAsync
        // consumes it (the deferred §11.5 pop); a parked frame has no Current to boost.
        if (!_options.Boost || _broken || IsCompleted || _pendingFault is not null
            || _pendingBatchAdvance || _pendingReturnStop)
        {
            return null;
        }

        var frame = CurrentFrame;

        // C11 (A64/I3): a capture frame (stepped-into INSERT…EXEC) must NOT boost — a boosted
        // subtree runs its member statements raw (a bare SELECT is a whitelisted boost member), so
        // the callee's result rows would stream instead of being captured into the target. Refuse →
        // interpreted stepping, which wraps each result-returning statement (conservative-closed, A21).
        if (frame.CaptureTargetSql is not null)
        {
            _trace.Event("boost.refuse", $"line={frame.Cursor.Current?.Span.StartLine} reason=insert-exec-capture");
            return null;
        }

        var current = frame.Cursor.Current;
        if (current is null || current.SubKind is not (SuSubKind.If or SuSubKind.While))
        {
            return null;                                       // B1: triggers on IF/WHILE rests only
        }

        var gate = new BoostSessionGate(_doomed, _detached, _broken, _errorContexts.Count > 0, _scopeChainPoisoned);
        var planResult = BoostPlanner.TryPlan(
            current, frame.Cursor.Index, gate, isBlocked ?? (_ => false), frame.TableTypeVariables.Keys);
        if (!planResult.Eligible)
        {
            _trace.Event("boost.refuse",
                $"line={current.Span.StartLine} reason={planResult.Refusal!.ReasonCode} detail={planResult.Refusal.Detail}");
            return null;
        }

        var plan = planResult.Plan!;
        _trace.Event("boost.plan",
            $"lines={current.Span.StartLine}-{current.Span.EndLine} members={plan.MemberUnits.Count} " +
            $"markers={plan.Markers.Count(m => !m.Suppressed)}");

        // §8 checklist item 5 (record-time half; the whitelist is the plan-time half):
        // nothing RecordRegistryEffects acts on may reach a boosted batch.
        foreach (var member in plan.MemberUnits)
        {
            if (member.SubKind is SuSubKind.TempTableDdl or SuSubKind.CursorDeclare
                || member.Fragment is DeallocateCursorStatement or DropTableStatement or SelectStatement { Into: not null })
            {
                throw new InvalidOperationException(
                    $"Registry-affecting SU ({member.SubKind} at line {member.Span.StartLine}) reached a boosted plan — planner bug (§14/A21).");
            }
        }

        var resultSets = new List<ResultSet>();
        var messages = new List<string>();

        // B3: block-level protection = OR over members — a no-op given B2 (boost
        // never fires detached), kept as defense in depth mirroring RunDebuggeeBatchAsync.
        foreach (var member in plan.MemberUnits)
        {
            if (RequiresTransactionProtection(member))
            {
                await EnsureTransactionProtectionAsync(member, messages, cancellationToken).ConfigureAwait(false);
                break;
            }
        }

        var seq = ++_boostSeq;
        var batch = ComposeDebuggeeBatch(() => ComposedBatchBuilder.BuildForBoostedSubtree(
            frame, _rewriteEngine!, _rewriteContext!, current, frame.Cursor.Index.FullScript, _shadows!,
            seq, plan.Markers, frame.XactAbortOn, _runtimeOptions.CurrentRowCount));   // C13: apply the debuggee's limit to the boosted subtree

        _trace.Event("boost.fire", $"line={current.Span.StartLine} seq={seq}");
        LastStep = await RunBoostedBatchAsync(batch, plan, seq, resultSets, messages, cancellationToken).ConfigureAwait(false);
        return (resultSets, messages);
    }

    // The boosted mirror of RunFaultableBatchAsync: same watchdog, same trailing-3998
    // tolerance, same routing entry — plus B6's err_line → SU re-entry, B7's recovery,
    // and B8's settlement. Never reuses HandleExecutorFailureAsync directly: a boosted
    // batch that died without a control row has PERSISTED POSITION to recover first.
    private async Task<StepOutcome> RunBoostedBatchAsync(
        ComposedBatch batch, BoostPlan plan, int seq,
        List<ResultSet> resultSets, List<string> messages, CancellationToken cancellationToken)
    {
        var node = plan.ControlNode;
        var frame = CurrentFrame;

        // §10.5 pause cancels the in-flight DEBUGGEE batch — and only that. Everything
        // after the execute below (control-row watchdog, B6 routing, B7 recovery, B8
        // settlement) is the debugger's own bookkeeping for an outcome that already
        // happened server-side; running it under the caller's token would let a pause
        // race abort it half-way — worst case skipping B7's recovery entirely, leaving
        // the cursor ON the node while the subtree's completed statements persist in
        // the open transaction, so the next continue re-fires the subtree and
        // double-applies them. Bookkeeping always completes; the pause lands at the
        // next step arrival (fact 30 / m6-boosted-attention triage).
        BatchResult batchResult;
        try
        {
            batchResult = await ExecuteAndTraceAsync(batch, cancellationToken).ConfigureAwait(false);
        }
        catch (StatementExecutionException ex)
        {
            return await HandleBoostedBatchDeathAsync(ex, batch, plan, seq, messages).ConfigureAwait(false);
        }
        catch (OperationCanceledException oce)
        {
            // Fact 30: on the async driver path a pause's token cancellation usually
            // surfaces as a RAW OperationCanceledException (the token check wins the
            // race against the attention SqlException) — the SAME batch death as the
            // wrapped Number-0 shape, classified and recovered identically.
            return await HandleBoostedBatchDeathAsync(
                new StatementExecutionException("Operation cancelled by user.", 11, 0, oce),
                batch, plan, seq, messages).ConfigureAwait(false);
        }

        var (control, userSets, stateValues) = ControlRowParser.Parse(batchResult, frame.Variables.Count);
        if (stateValues is not null)
        {
            frame.Snapshot = stateValues.ToArray();
        }

        if (batchResult.TrailingErrors is { Count: > 0 } trailing && control.XactState != -1)
        {
            // Same rule as RunFaultableBatchAsync (fact 22): trailing batch-end errors
            // are legal only as the doomed epilogue. The boosted death handler
            // recovers position honestly before classifying.
            var first = trailing[0];
            return await HandleBoostedBatchDeathAsync(
                new StatementExecutionException(first.Message, first.SeverityClass, first.Number),
                batch, plan, seq, messages).ConfigureAwait(false);
        }

        resultSets.AddRange(userSets);
        messages.AddRange(batchResult.Messages);
        // §8 checklist item 1: the SAME watchdog path as every control row — doom and
        // detach edges (e.g. a 3609 trigger rollback arriving as trancount 0 on the
        // CATCH row) are control-row-observed, never skipped past.
        await ObserveControlRowAsync(control, node, messages).ConfigureAwait(false);

        if (control.Ok)
        {
            // B8 success settlement: the whole node ran natively to completion. The
            // postamble's live capture (V-invariant, fact 27) makes rc/err/si the
            // native post-block values; the cursor moves to after the node; a frame
            // this completes pops through the standard settle (a callee's boosted
            // tail is a COMPLETED call).
            // §7.4/A26 (D1): boost only dispatches with the chain in sync (§14 gate),
            // and nothing inside a slice poisons it (no EXEC/RETURN/pop, and a doom
            // routes to the B6 fault path, not here) — so the post-block capture is
            // always in sync.
            _shadows!.ObserveSuccess(control, scopeChainInSync: true);
            frame.Cursor.CompleteSubtree();
            ReconcileErrorContexts();
            var settled = await SettleCompletionAsync(messages).ConfigureAwait(false);
            _trace.Event("boost.complete", $"seq={seq} line={node.Span.StartLine} rc={control.Rc?.ToString() ?? "null"}");
            return settled;
        }

        // ---- B6 fault re-entry (CATCH control row). --------------------------------
        // F2 ruling: the boosted CATCH row's live scope_identity capture feeds the R6
        // shadow — completed in-slice identity inserts are native state at the fault.
        _shadows!.ObserveBoostFaultScopeIdentity(control.ScopeIdentity);

        var values = BuildErrorContextValues(control, batch, node, frame.Module);   // §10.2 formula + A27 (origin = the boosted frame)
        Interpreter.StatementUnit? mapped = null;
        if (values.Procedure is null && values.Line is { } realLine)
        {
            plan.LineMap.TryGetValue(realLine, out mapped);
        }

        if (mapped is not null)
        {
            _trace.Event("boost.fault",
                $"seq={seq} err={values.Number} line={values.Line} su=L{mapped.Span.StartLine}/{mapped.SubKind}");
            // Re-entry via the solved Jump-to-Cursor primitive: the subtree is
            // TRY-free and shares the node's outer nesting, so the §13 gate passes by
            // construction (never weakened — §8 checklist item 4). A faulted
            // predicate target re-creates its pending-predicate entry, which is what
            // makes the fact-21 FALSE-path disposition land exactly.
            frame.Cursor.JumpTo(mapped);
            ReconcileErrorContexts();
            var outcome = BreakOnAllErrors
                ? PendFault(values, mapped, control.XactState)
                : await PerformRouteAsync(values, mapped, control.XactState,
                    terminalWhenUnhandled: mapped.SubKind == SuSubKind.Throw, messages).ConfigureAwait(false);
            if (outcome.Disposition == StepDisposition.UnhandledContinued)
            {
                _trace.Event("boost.reenter",
                    $"seq={seq} resumed=L{frame.Cursor.Current?.Span.StartLine.ToString() ?? "frame-end"}");
            }

            return outcome;
        }

        // Unmappable origin: the fault was raised inside a real module (a trigger
        // fired by slice DML — err_procedure names it) or on a line with no SU.
        // Routed/terminal outcomes are origin-position-independent, but the
        // statement-level-continue disposition is not — rather than guess, resume at
        // the last persisted marker and let interpreted re-execution surface the
        // fault at its own SU with exact fact-21 semantics: the faulted statement
        // rolled back statement-level (re-running it is exact — invariant P puts at
        // most that ONE statement between the marker and the fault), and the oracle
        // CAUGHT the error, so no native message streamed — no duplication.
        if (control.XactState == -1)
        {
            // Doomed: continuation cannot happen (terminal-or-routed), so the origin
            // position is immaterial — route from the node.
            _trace.Event("boost.fault", $"seq={seq} err={values.Number} module={values.Procedure} mapped=none doomed");
            return await PerformRouteAsync(values, node, control.XactState,
                terminalWhenUnhandled: false, messages).ConfigureAwait(false);
        }

        var recoveredPos = await RecoverBoostPositionAsync(plan, seq).ConfigureAwait(false);
        if (recoveredPos >= 0)
        {
            frame.Cursor.ResumeAfter(plan.Markers[recoveredPos].Child);
            ReconcileErrorContexts();
        }

        _trace.Event("boost.fault",
            $"seq={seq} err={values.Number} module={values.Procedure ?? "?"} mapped=none pos={recoveredPos} → interpreted re-execution");
        return StepOutcome.Performed;
    }

    // B7: attention (pause/timeout) and the §10.1 propagate class — the batch died
    // without a control row. One debugger-initiated recovery read re-establishes the
    // last persisted position and variable state, then the standard classifications
    // apply from there. No cancellation token by design: the dominant caller IS the
    // §10.5 pause path, whose token is cancelled by definition when we get here —
    // recovery and routing are debugger-initiated and must always complete
    // (fact 30 / m6-boosted-attention triage).
    private async Task<StepOutcome> HandleBoostedBatchDeathAsync(
        StatementExecutionException ex, ComposedBatch batch, BoostPlan plan, int seq,
        List<string> messages)
    {
        var node = plan.ControlNode;
        if (ex.SeverityClass >= 20 || ex.ConnectionBroken)
        {
            // Connection-fatal (severity ≥ 20 or a broken connection, §8.3) — no recovery
            // read is possible; mirror HandleExecutorFailureAsync's first arm (checked
            // FIRST: such an error can also carry Number 0 and must not classify as a
            // pause). Boost only runs plain-healthy, so this is never a batch-terminal
            // advance — a connection-fatal death ends the session regardless of batches.
            _broken = true;
            throw new SessionFaultException(
                $"Connection-fatal error (severity {ex.SeverityClass}{(ex.ConnectionBroken ? ", broken connection" : "")}) " +
                $"at line {node.Span.StartLine}: {ex.Message} — session terminated (§10.1/§8.3).");
        }

        var frame = CurrentFrame;
        var recoveredPos = await RecoverBoostPositionAsync(plan, seq).ConfigureAwait(false);
        if (recoveredPos >= 0)
        {
            frame.Cursor.ResumeAfter(plan.Markers[recoveredPos].Child);
            ReconcileErrorContexts();
        }

        _trace.Event("boost.recovery",
            $"seq={seq} pos={recoveredPos} resumed=L{frame.Cursor.Current?.Span.StartLine.ToString() ?? "frame-end"} err={ex.Number}");

        if (ex.Number is -2 or 0)
        {
            // §10.5 attention: statement rolled back, session healthy, cursor at the
            // re-established position — the retry re-runs only the attention-rolled-
            // back statement (fact 28; the completed-statement-vs-marker window is
            // A21's recorded residual, same class as §7.1's existing one).
            messages.Add(recoveredPos >= 0
                ? $"Paused inside a boosted region (§14): position re-established after the statement at line " +
                  $"{plan.Markers[recoveredPos].Child.StartLine}; a retry re-runs only the rolled-back statement (§10.5)."
                : "Paused inside a boosted region (§14) before any statement completed; the cursor stays on the node (§10.5).");
            return new StepOutcome(StepDisposition.EngineAttention,
                new ErrorContextValues(ex.Number, 0, 1, node.Span.StartLine, null, ex.Message));
        }

        // §10.1 propagate class (compile/deferred-resolution etc.): route with the
        // origin mapped from the exception's batch line when it maps, else the node.
        // Variables reflect the last marker — the terminal/abnormal-pop outcomes make
        // the marker-to-fault gap inspection-cosmetic (native's dying scope loses its
        // variables too); console note when the gap is non-empty.
        var origin = node;
        if (ex.Procedure is null && ex.LineNumber > 0
            && plan.LineMap.TryGetValue(node.Span.StartLine + (ex.LineNumber - batch.B), out var mappedUnit))
        {
            origin = mappedUnit;
        }

        if (recoveredPos >= 0)
        {
            messages.Add(
                $"The boosted region died mid-flight (§14/§10.1): variable state reflects the last completed " +
                $"statement (line {plan.Markers[recoveredPos].Child.StartLine}).");
        }

        var values = new ErrorContextValues(
            ex.Number, ex.SeverityClass, 1,
            ex.Procedure is not null && ex.LineNumber > 0 ? ex.LineNumber : origin.Span.StartLine,
            SynthesizeProcedure(ex.Procedure, frame.Module), ex.Message);   // A27 (origin = the boosted frame)
        return await PerformRouteAsync(values, origin, xactState: 0,
            terminalWhenUnhandled: false, messages,
            sameScopeUncatchable: true).ConfigureAwait(false);
    }

    // B7's one debugger-initiated read: seq/pos from #__dbg_boost plus the state
    // table (marker state writes are transaction-local but visible to our own session
    // — facts 26c/28). Returns the recovered marker pos, or -1 when nothing of THIS
    // batch completed (stale/absent seq, pos still -1, or the read itself failed —
    // e.g. the table is gone; refusing recovery is always safe: the cursor stays ON
    // the node and §10.5 retry re-runs a batch whose only completed work was the
    // guarded prologue). On success the frame snapshot re-seeds from the state read
    // (values as of the last var-assigning marker ≤ pos — exact, by invariant P).
    private async Task<int> RecoverBoostPositionAsync(BoostPlan plan, int seq)
    {
        var frame = CurrentFrame;
        var readSql = $"SELECT seq, pos FROM {State.StateTableIdentifiers.BoostPositionTable};" +
                      (frame.Variables.Count > 0 ? "\n" + StateTableDdlBuilder.BuildSelectAll(frame.Ordinal) : "");
        BatchResult read;
        try
        {
            // CancellationToken.None deliberately (fact 30): under a §10.5 pause the
            // step token is already cancelled when this read runs — issuing it on
            // that token would throw before reaching the server and silently skip
            // recovery, the exact defect the m6-boosted-attention triage found.
            read = await ExecuteAndTraceAsync(readSql, CancellationToken.None).ConfigureAwait(false);
        }
        catch (StatementExecutionException recoveryFailure)
        {
            _trace.Event("boost.recovery", $"seq={seq} read-failed: {recoveryFailure.Message}");
            return -1;
        }

        if (read.ResultSets is not [{ Rows: [var posRow, ..] }, ..] || posRow.Count < 2)
        {
            return -1;                                         // empty table — nothing recorded
        }

        var readSeq = Convert.ToInt32(posRow[0]);
        var readPos = Convert.ToInt32(posRow[1]);
        if (readSeq != seq || readPos < 0 || readPos >= plan.Markers.Count)
        {
            return -1;                                         // stale seq (B4) or nothing completed
        }

        if (frame.Variables.Count > 0 && read.ResultSets is [_, { Rows: [var stateRow, ..] }, ..])
        {
            frame.Snapshot = stateRow.ToArray();
        }

        return readPos;
    }

    // The ExecuteUnit/Declare/WaitFor-honor wrapper: success → ObserveSuccess + null;
    // fault → the disposition (cursor already placed).
    private async Task<StepOutcome?> RunDebuggeeBatchAsync(
        ComposedBatch batch, Interpreter.StatementUnit unit,
        List<ResultSet> resultSets, List<string> messages, CancellationToken cancellationToken,
        bool deferRoute = false)
    {
        await EnsureTransactionProtectionAsync(unit, messages, cancellationToken).ConfigureAwait(false);
        var run = await RunFaultableBatchAsync(batch, unit, cancellationToken, deferRoute).ConfigureAwait(false);
        resultSets.AddRange(run.UserSets);
        messages.AddRange(run.Messages);
        if (run.Outcome is not null)
        {
            return run.Outcome;
        }

        ObserveDebuggeeSuccess(run.Control!, unit);   // §7.4/A26 (D1): R6 chain-sync gate
        AppendRowsAffectedNote(unit, run.Control!, messages);
        return null;
    }

    // C5 (§12.3): the debugger forces SET NOCOUNT ON, which suppresses the engine's
    // native "(N rows affected)" done-token messages. Re-synthesize that line for the
    // row-affecting DML family from the control row's captured @@ROWCOUNT (control.Rc —
    // the same value the R4 shadow reads, captured immediately after the statement, so it
    // is faithful). A plain SELECT is excluded: its row count is conveyed by the rendered
    // result-set table (A50), so the line there would just be noise. Kept on the always-
    // shown message stream (not a §12.3 verbose diagnostic note): it is debuggee output
    // the user explicitly asked to see, mirroring SSMS.
    private static void AppendRowsAffectedNote(Interpreter.StatementUnit unit, ControlRow control, List<string> messages)
    {
        if (control.Rc is not { } rowCount)
        {
            return;
        }

        var isRowAffectingDml = unit.Fragment is InsertStatement or UpdateStatement
            or DeleteStatement or MergeStatement
            || unit.Fragment is SelectStatement { Into: not null };   // SELECT … INTO is a bulk insert
        if (isRowAffectingDml)
        {
            messages.Add(FormatRowsAffected(rowCount));
        }
    }

    // Native wording: "(1 row affected)" / "(N rows affected)" — matches SSMS / ADO.NET.
    private static string FormatRowsAffected(int rowCount)
        => $"({rowCount} row{(rowCount == 1 ? string.Empty : "s")} affected)";

    // §10.3 routing, session half. M4 (D3): step 2's walk crosses frames. Cursor
    // placement per branch:
    //   routed             → first statement of the innermost eligible CATCH in ANY
    //                        frame; frames above the landing frame abnormal-pop first
    //                        (fact 23: no copy-back, no @rc);
    //   terminal           → unchanged, ALL frames kept for inspection (native batch
    //                        death; the red UI shows the full stack at the fault);
    //   statement-level    → native continuation IN THE FAULTING FRAME (facts 18/21,
    //                        fact 23-H): faulted predicates take the FALSE path,
    //                        faulted RETURNs complete the frame with 0 — which in a
    //                        callee is a COMPLETED pop — everything else resumes at
    //                        the next unit; a continuation that exhausts the body
    //                        settles as a completed pop too.
    // `sameScopeUncatchable` = the §10.1 compile/deferred class: ineligible in the
    // faulting frame's own scope (facts 1b/6, fact 23-F), the walk starts at the
    // caller, and "no route anywhere" is batch-aborting, never continuation.
    // O6 (§5.5, ratified): the routing walk is post-outcome bookkeeping under the
    // rider-(i) invariant — once an outcome exists server-side, a racing §10.5 pause must
    // never abort a half-popped frame stack (it lands at the next step arrival instead).
    // The step token is DELIBERATELY NOT a parameter here: every server-work path this
    // drives — the abnormal-pop cleanup + its §11.2 SET restores (PopFrameAsync),
    // SettleCompletionAsync's completed pops, ContinueAfterUnhandledFault (cursor-only) —
    // runs on CancellationToken.None unconditionally. Dropping the parameter is
    // compile-time enforcement of the never-on-step-token invariant (the M6 B7 ruling),
    // beating convention; callers keep their own tokens for their PRE-route work.
    private async Task<StepOutcome> PerformRouteAsync(
        ErrorContextValues values, Interpreter.StatementUnit originUnit, int xactState,
        bool terminalWhenUnhandled, List<string> messages,
        bool sameScopeUncatchable = false)
    {
        var frames = _frames!;
        var originFrameOrdinal = frames.Current.Ordinal;
        // A14: the pre-flight's proceed path armed the original #temp names for THIS
        // batch. Read-and-clear here so no other PerformRoute caller (e.g. a later
        // bare-THROW rethrow, which composes no batch) can pick up a stale note.
        var c23Note = _c23TerminalNote;
        _c23TerminalNote = null;
        var start = frames.Depth - 1 - (sameScopeUncatchable ? 1 : 0);
        for (var i = start; i >= 0; i--)
        {
            // RouteError mutates its cursor ONLY when it routes (load-bearing for this
            // probe-in-order walk — M4 design notes §5.1). Its RouteOutcome tells the walk
            // apart: no eligible CATCH here (keep walking), a stop INSIDE the CATCH, or a
            // transit THROUGH an empty CATCH (handled vacuously).
            var routeOutcome = frames.All[i].Cursor.RouteError();
            if (routeOutcome == Interpreter.RouteOutcome.NoEligibleCatch)
            {
                continue;
            }

            // Landing frame found: abnormal-pop every frame above it (§11.5-abnormal,
            // fact 23 C–G), then push the context. Trim-to-Σ-minus-one THEN push: a
            // same-depth re-route (bare THROW out of one CATCH into an outer one)
            // replaces the consumed context instead of stacking on it; popped frames'
            // CATCH occupancies left Σ already.
            while (frames.Depth - 1 > i)
            {
                await PopFrameAsync(completed: false, messages, CancellationToken.None).ConfigureAwait(false);
            }

            // §10.3/§11.5 empty-CATCH transit (Fable §10 re-review 2026-07-17, findings 1+2). RouteError
            // routed into an EMPTY CATCH — no statement to stop on — so it ran the cursor STRAIGHT THROUGH
            // END CATCH (the cursor reports this directly via RouteOutcome; a frame-level CatchDepth delta
            // is ambiguous when routing truncated intervening CATCH occupancies, e.g. a bare THROW out of
            // one CATCH into an outer empty one). The error was HANDLED vacuously; the cursor now either
            // COMPLETED the body (empty CATCH last → the frame returns normally: OUTPUT copy-back, caller
            // continues past the EXEC) or sits on the statement AFTER END CATCH (continuation). BOTH must:
            //   • push NO fault context and NOT ObserveFault — the CATCH is already exited, so there is no
            //     live context to represent, and native reads @@ERROR = 0 / @@ROWCOUNT = 0 after the empty
            //     transit (fact 18 + live probes X1/X4/X6). ObserveHandledCatchReturn zeroes the shadow so
            //     the caller (completed) / the next statement (continuation) reads those zeros;
            //   • RECONCILE (trim-only) — the entered-and-exited CATCH nets zero on this frame's CatchDepth
            //     and any abnormal-popped frames above vacated theirs, so trimming to TotalCatchDepth is
            //     exact and NEVER over-trims a still-live OUTER CATCH context (the pre-fix fall-through did
            //     `TrimContextsTo(Σ-1)` + push here, which corrupted a caller's live CATCH — finding 2a).
            // Completed → settle the COMPLETED pop (copy-back + @rc, cascade) via SettleCompletionAsync —
            // NOT a phantom RoutedToCatch on a completed cursor (the crash: an unsettled depth-≥2 frame whose
            // next cursor.Peek() threw). Frame 0 (depth 1) is a harmless no-op settle → the run ends
            // (IsCompleted), which is why frame 0 never crashed. Continuation → the fault is handled and the
            // cursor already sits on the resume statement; return Performed (a normal stop there). Both
            // settle WITHOUT parking, mirroring the §10.3-step-4 unhandled-continuation settle below. Doomed
            // empty-CATCH completion pops via PopFrameAsync's doomed branch (bookkeeping + SET restores, no
            // copy-back), leaving the caller doomed — a doomed callee that caught its own error and returned.
            if (routeOutcome == Interpreter.RouteOutcome.TransitedEmptyCatch)
            {
                _shadows!.ObserveHandledCatchReturn();         // @@ERROR = 0, @@ROWCOUNT = 0 (empty-CATCH transit)
                ReconcileErrorContexts();
                _trace.Event("route.emptycatch",
                    $"frame={frames.All[i].Ordinal} completed={frames.All[i].Cursor.IsCompleted} line={originUnit.Span.StartLine}");
                return frames.All[i].Cursor.IsCompleted
                    ? await SettleCompletionAsync(messages).ConfigureAwait(false)
                    : StepOutcome.Performed;
            }

            TrimContextsTo(TotalCatchDepth() - 1);
            _errorContexts.Add(new ErrorContext(values, originUnit, originFrameOrdinal));
            SyncErrorContextState();
            _shadows!.ObserveFault(values.Number);             // fact 18: @@ERROR = n, @@ROWCOUNT = 0 at CATCH entry
            return new StepOutcome(StepDisposition.RoutedToCatch, values);
        }

        if (terminalWhenUnhandled || xactState == -1 || sameScopeUncatchable)
        {
            // §10.3 step 4, batch-aborting classes: a doomed transaction (native
            // XACT_ABORT abort-and-rollback), THROW (batch-aborting, verified), or the
            // §10.1 no-control-row propagate class with no eligible caller CATCH.
            // Natively the whole batch dies — every frame with it; the stack is kept
            // intact for inspection (no pops: while doomed a pop is a no-op anyway,
            // and the DAP red UI wants the frames).
            // A14: a terminal fault on a pre-flighted doomed-#temp reference carries
            // the C23 citation and the ORIGINAL names alongside the engine 208 —
            // enriching values here is safe (the outcome is terminal; no CATCH will
            // ever re-materialize this message). Routed faults stay engine-pure.
            if (c23Note is { Count: > 0 } deadTables)
            {
                values = values with
                {
                    Message = values.Message +
                        $" [Caveat C23: {string.Join(", ", deadTables)} was destroyed by the doomed " +
                        "transaction's forced rollback (fact 22, §10.4/§21); native code would still see it.]",
                };
            }

            // M8 §8.2/§8.3 (A35, §10-gated): classify the batch-aborting fault. This arm
            // is ONLY ever reached for batch-aborting (NOT connection-fatal) classes —
            // severity ≥ 20 / a broken connection throw session-fatal in
            // HandleExecutorFailureAsync BEFORE any routing. So in a multi-batch script
            // with more batches remaining the current BATCH is terminated but the client
            // CONTINUES to the next (sqlcmd/SSMS default, fact 32a): publish the fault-site
            // stop (FrameFaulted, honored by the §10.6 filters) WITHOUT breaking the
            // session, and arm _pendingBatchAdvance so the next step crosses the boundary
            // via the §8.1 reconciliation (doom force-rollback + callee pops there). The
            // last batch, a single-batch script, and procedure mode keep the M4
            // session-fatal behavior exactly (MoreBatchesRemain is always false).
            if (MoreBatchesRemain)
            {
                _pendingBatchAdvance = true;
                messages.Add(
                    $"Unhandled error {values.Number} (severity {values.Severity}): {values.Message} — " +
                    $"batch {_currentBatchIndex + 1} of {_batches!.Count} is terminated (§10.3 batch-aborting " +
                    "class); execution continues at the next batch (§5.4/§8.2). Continue to advance.");
            }
            else
            {
                _broken = true;
                messages.Add(
                    $"Unhandled error {values.Number} (severity {values.Severity}): {values.Message} — " +
                    "the frame is terminated (§10.3, batch-aborting class). Only inspection and teardown remain.");
            }

            return new StepOutcome(StepDisposition.FrameFaulted, values);
        }

        // Facts 18/21 (verified, incl. inside CATCH blocks) + fact 23-H (proc-scoped):
        // an unhandled statement-level error does not end the batch natively — report
        // like the native client and continue IN THE FAULTING FRAME. Continuation
        // semantics per fact 21 (probed live at the §10 line review): the next
        // statement reads @@ERROR = the fault number and @@ROWCOUNT = 0 (P5/P7); a
        // faulted IF/WHILE predicate takes the FALSE path (P1/P6); a faulted RETURN
        // expression still RETURNS — status 0 plus the engine's info message (P8) —
        // and a callee that returns or exhausts its body this way COMPLETED its call:
        // the settle below pops it with copy-back (fact 23-H). Known residual: C22.
        _shadows!.ObserveFault(values.Number);
        messages.Add(NativeErrorText(values));
        var faultingFrame = frames.Current;
        if (originUnit.SubKind == SuSubKind.Return)
        {
            messages.Add(
                $"The '{faultingFrame.Module.Name}' procedure attempted to return a status of NULL, " +
                "which is not allowed. A status of 0 will be returned instead.");
            faultingFrame.ReturnCode = 0;
            faultingFrame.Cursor.Advance(AdvanceSignal.Normal);   // RETURN completes the frame
        }
        else
        {
            faultingFrame.Cursor.ContinueAfterUnhandledFault();
        }

        ReconcileErrorContexts();
        // The disposition stays UnhandledContinued even when the continuation
        // completed a callee (the fault is the step's story; the adapter re-reads
        // Frames for the stack shape at every stop).
        var settled = await SettleCompletionAsync(messages).ConfigureAwait(false);
        // §11.7 (A65/F3, Fable §10 review): if that settle completed a CAPTURE callee whose flush
        // FAULTED, SettleCompletionAsync returns the routed/terminal outcome — the cursor is now at
        // the caller's CATCH (RoutedToCatch), pended (FaultAtSite), or the session is broken
        // (FrameFaulted). Surface THAT, not the original statement-level fault whose site the cursor
        // no longer occupies. A normal settle (Performed/FrameCompleted) keeps UnhandledContinued.
        if (settled.Disposition is StepDisposition.RoutedToCatch
            or StepDisposition.FaultAtSite or StepDisposition.FrameFaulted)
        {
            return settled;
        }

        return new StepOutcome(StepDisposition.UnhandledContinued, values);
    }

    private StepOutcome PendFault(ErrorContextValues values, Interpreter.StatementUnit unit, int xactState)
    {
        _pendingFault = (values, unit, xactState);
        return new StepOutcome(StepDisposition.FaultAtSite, values);
    }

    // §10.2 line mapping: `real = SU.OriginalStartLine + (err_line − B)` when
    // err_procedure is NULL (the fault is in our ad-hoc batch); when it names a real
    // module (a nested proc stepped OVER), keep (procedure, line) verbatim — genuine
    // engine data.
    private static ErrorContextValues BuildErrorContextValues(
        ControlRow control, ComposedBatch batch, Interpreter.StatementUnit unit,
        Interpreter.ModuleIdentity originModule)
    {
        int? line;
        if (control.ErrProcedure is null)
        {
            line = control.ErrLine is { } errLine ? unit.Span.StartLine + (errLine - batch.B) : unit.Span.StartLine;
        }
        else
        {
            line = control.ErrLine;
        }

        return new ErrorContextValues(
            control.ErrNumber ?? 0, control.ErrSeverity ?? 16, control.ErrState ?? 1,
            line, SynthesizeProcedure(control.ErrProcedure, originModule), control.ErrMessage ?? "(no message)");
    }

    // A27 (§10.2, ratified 2026-07-07; sharpened schema-qualified per fact 31c —
    // orchestrator ruling 2026-07-08, docs/archive/reviews/m7-hardening-core-opus.md §2/§7): when
    // the built error context has NO server-named module (Procedure NULL — the fault is
    // in our ad-hoc batch) but the ORIGIN frame IS a module frame (procedure-mode frame 0
    // or any stepped-into callee), native ERROR_PROCEDURE() names the module —
    // SCHEMA-QUALIFIED (schema.name, matching fact 31c). Synthesize frame.Module.Display
    // (already unbracketed schema.name, and consistent with the verbatim route path's
    // literal ERROR_PROCEDURE() capture). Script frames keep NULL — native ad-hoc batches
    // read NULL. The verbatim rule for server-named modules (a stepped-OVER nested proc)
    // is unchanged (builtProcedure is non-null there); C21 (indirect re-materialized
    // consumers read wrapper values) is unaffected — it never reaches a NULL procedure.
    // A58 (§11.6, fact 33c): a DYNAMIC frame is not a catalog module — native
    // ERROR_PROCEDURE() reads NULL inside an sp_executesql/EXEC() batch, whether the fault
    // is caught inside it or by the caller. So it joins script frames on the NULL side of
    // this synthesis; only a real module (procedure-mode frame 0, a stepped-into proc) is
    // named. Getting this wrong would make the debugger name a module the engine does not.
    private static string? SynthesizeProcedure(string? builtProcedure, Interpreter.ModuleIdentity originModule)
        => builtProcedure ?? (originModule.IsScript || originModule.IsDynamic ? null : originModule.Display);

    // The shape the native client prints for an error it received (fact 18's
    // continuation surface) — RunToEnd/fidelity and the Debug Console both show this.
    private static string NativeErrorText(ErrorContextValues values)
        => $"Msg {values.Number}, Level {values.Severity}, State {values.State}" +
           (values.Procedure is { } procedure ? $", Procedure {procedure}" : string.Empty) +
           (values.Line is { } line ? $", Line {line}" : string.Empty) +
           $"\n{values.Message}";

    // §7.4/A26 (D1): a completed debuggee statement's success settlement, gated for R6
    // chain-sync. The live SCOPE_IDENTITY() capture is native truth only while the
    // server chain equals the current frame's native chain (facts 26d/26e). While the
    // chain is poisoned (post-pop or doomed), the capture is skipped and the shadow
    // serves the client-modeled value — exact, because natively only insert-family
    // statements move the chain (fact 26d/31b). A completed insert-family SU (fact 31b:
    // InsertStatement / SELECT…INTO / MERGE-with-insert) re-synchronizes both chains to
    // its own result, so the capture is taken and the flag clears. rc/err are always
    // captured (per-statement live truth in every regime).
    private void ObserveDebuggeeSuccess(ControlRow control, Interpreter.StatementUnit unit)
    {
        // A63/N1 (verified live): a cursor declaration preserves @@ROWCOUNT (it retains the prior
        // statement's value), where the composed batch's control row reports 0 (its guard predicate +
        // preamble SETs zero it). Preserve the shadow instead of adopting the captured 0 — the inverse
        // of the ObservePredicateEvaluation idiom. Covers both `SET @c = CURSOR FOR` (N1) and the named
        // `DECLARE c CURSOR` (N2, pre-existing). A cursor declare is never insert-family, so the R6
        // scope-chain resync below never applies.
        if (unit.SubKind == SuSubKind.CursorDeclare)
        {
            _shadows!.ObserveCursorDeclare();
            return;
        }

        // §11.7 (A65/F1, Fable §10 review): a capture-wrapped statement runs as `INSERT INTO <stage>
        // (…) <SELECT|EXEC>`, and the stage's `seq bigint IDENTITY` makes that insert set
        // `SCOPE_IDENTITY()` to a debugger artifact (the seq value). Its @@ROWCOUNT/@@ERROR are still
        // native-faithful (the stage insert's rowcount = the captured SELECT's row count), but the
        // scope chain must NOT adopt the seq — preserve the frame's modelled SCOPE_IDENTITY (pass
        // scopeChainInSync:false) and POISON the chain (the server chain now holds the seq; a later
        // real insert-family SU in the callee re-syncs, and R6 reads serve the shadow meanwhile).
        if (CurrentFrame.CaptureTargetSql is not null
            && ResultCaptureClassifier.IsResultReturning(unit.Fragment))
        {
            _shadows!.ObserveSuccess(control, scopeChainInSync: false);
            _scopeChainPoisoned = true;
            return;
        }

        var isInsertFamily = InsertFamilyClassifier.IsInsertFamily(unit.Fragment);
        _shadows!.ObserveSuccess(control, scopeChainInSync: !_scopeChainPoisoned || isInsertFamily);
        if (_scopeChainPoisoned && isInsertFamily)
        {
            _scopeChainPoisoned = false;
            _trace.Event("scopeid.resync", $"line={unit.Span.StartLine} su={unit.SubKind}");
        }
    }

    // §10.4 transaction watchdog — observes EVERY control row (success and fault),
    // amended per engine fact 22 (docs/archive/reviews/m3-p05-doom-batch-boundary-fable.md):
    // doom cannot survive a batch boundary, and resurrection is DEFERRED.
    // No cancellation token by design (ruling rider (i), m6-boosted-attention triage
    // §3 invariant): once a control row exists the outcome already happened
    // server-side — the watchdog's edge handling (detach reseed spans multiple
    // batches across every frame) must complete even under a racing §10.5 pause,
    // which lands at the next step arrival instead.
    private async Task ObserveControlRowAsync(
        ControlRow control, Interpreter.StatementUnit unit, List<string> messages)
    {
        if (control.XactState == -1)
        {
            // §7.4/A26 (D1): while doomed the debuggee batches are parameterized (A6/A8)
            // and run in an sp_executesql child scope (fact 26e) — the SCOPE_IDENTITY()
            // capture reads a scope that is not the frame's. Poison the chain so the
            // shadow serves the client-modeled value; while doomed nothing clears it
            // (insert-family faults 3930), so every doomed child-scope NULL capture is
            // skipped, and on the A9 resurrection edge the flag simply stays set (the
            // shadow's kept value IS the native post-rollback value — 26d rollback-neutral).
            _scopeChainPoisoned = true;
            // Trancount as the doomed transaction holds it — control rows report from
            // MID-batch, before the engine's end-of-batch 3998 forced rollback (fact
            // 22). The redoom preamble re-establishes exactly this many levels on
            // every subsequent batch while doomed.
            _doomTrancount = Math.Max(control.Trancount, 1);
            if (!_doomed)
            {
                _doomed = true;
                _trace.Event("txn.doom", $"line={unit.Span.StartLine} trancount={_doomTrancount}");
                messages.Add(
                    $"The transaction became uncommittable (doomed) at line {unit.Span.StartLine} (§10.4): reads still " +
                    "work; any debuggee write faults with 3930 (faithful); variable values now ride the session " +
                    "snapshot. The debuggee's exits are ROLLBACK (IF XACT_STATE() = -1 ROLLBACK) or session end. " +
                    "(Between steps the engine force-rolls a doomed transaction back at each batch end — fact 22; " +
                    "the debugger re-establishes the doomed state every step and absorbs the engine's 3998 notices.)");
            }
        }

        if (control.Trancount == 0 && _lastObservedTrancount > 0)
        {
            // §10.4 trancount 1+ → 0 edge: the debuggee ended the transaction THIS
            // batch (ROLLBACK, COMMIT of our outer tran, or the doomed-mode ROLLBACK
            // exit — which ends its batch cleanly, no 3998). Amended semantics
            // (fact 22 / A9): re-seed state tables NOW (their content reverted with
            // the rollback while native variables are non-transactional — the writes
            // autocommit at trancount 0, into our session-scoped #tables), but DEFER
            // the safety BEGIN TRANSACTION until a statement actually needs rollback
            // protection: until then trancount/XACT_STATE() observables stay faithful
            // to native (0), which is what the rest of a typical
            // rollback-in-CATCH procedure (p05) observes after its rollback.
            // M4 (A9 steps 2-3 in full): EVERY frame reseeds — dead #__dbg_s{n>0}
            // tables are re-created first — registries reconcile (§9), and destroyed
            // table-variable realizations are healed empty (C25).
            _doomed = false;
            _detached = true;
            _trace.Event("txn.detached", $"line={unit.Span.StartLine} via={unit.SubKind}");
            await ReseedAllFramesAfterDetachAsync(survivingTrancount: 0, CancellationToken.None).ConfigureAwait(false);

            messages.Add(unit.SubKind == SuSubKind.Commit
                ? $"Debuggee COMMITTED at line {unit.Span.StartLine} — rollback-mode policy violation (§10.4): that " +
                  "work is now permanent. Variable state re-seeded from the snapshot; the safety transaction " +
                  "re-opens before the next statement that requires rollback protection."
                : $"Debuggee ended the transaction at line {unit.Span.StartLine}; variable state re-seeded from the " +
                  "snapshot (§10.4). The safety transaction re-opens before the next statement that requires " +
                  "rollback protection — until then transaction observables stay faithful to native (trancount 0).");
        }

        _lastObservedTrancount = control.Trancount;
        _lastObservedXactState = control.XactState;    // M5 I3: System scope's XACT_STATE() source
    }

    // §10.4 deferred resurrection (fact 22 / A9): called before executing a debuggee
    // statement while detached. Pure-read and variable-only statements run at the
    // native post-rollback trancount (0); anything that could write data re-opens the
    // safety net FIRST (§16: rollback teardown must stay able to undo it).
    private async Task EnsureTransactionProtectionAsync(
        Interpreter.StatementUnit unit, List<string> messages, CancellationToken cancellationToken)
    {
        if (!_detached || !RequiresTransactionProtection(unit))
        {
            return;
        }

        _trace.Event("txn.resurrect", $"line={unit.Span.StartLine} kind={unit.Kind}/{unit.SubKind}");
        await ExecuteAndTraceAsync("BEGIN TRANSACTION;", cancellationToken).ConfigureAwait(false);
        _detached = false;
        _lastObservedTrancount = 1;
        messages.Add(
            $"Safety transaction re-opened before line {unit.Span.StartLine} (§10.4 deferred resurrection: the " +
            "statement can modify data and must remain rollback-able; @@TRANCOUNT reads 1 from here where a " +
            "native post-rollback run would read 0).");
    }

    // Conservative-closed classification: protection is skipped only for statements
    // that provably cannot write user data. Transaction-control SUs MUST stay
    // unprotected — at trancount 0 they need their native semantics (a detached COMMIT
    // is native error 3902; resurrecting first would make it commit the SAFETY
    // transaction instead), and a debuggee BEGIN TRAN becomes its own rollback cover
    // (teardown's `IF @@TRANCOUNT > 0 ROLLBACK` undoes it) — resurrecting under it
    // would read @@TRANCOUNT one higher than native for the rest of the frame.
    private static bool RequiresTransactionProtection(Interpreter.StatementUnit unit)
    {
        if (unit.Kind != SuKind.Executable)
        {
            return false;                              // predicates/initializers/jumps: reads and variable writes only
        }

        return unit.SubKind switch
        {
            SuSubKind.SetVariable or SuSubKind.SetOption or SuSubKind.Print
                or SuSubKind.RaiseError or SuSubKind.Throw => false,
            SuSubKind.BeginTran or SuSubKind.Commit or SuSubKind.Rollback or SuSubKind.SaveTran => false,
            // SELECT without INTO is a read (assignment SELECTs write only variables);
            // everything else (DML, DDL, EXEC, unknown Other) defaults to protected.
            _ => unit.Fragment is not SelectStatement { Into: null },
        };
    }

    // §10.1's no-control-row classes: the batch never began (or died mid-flight) — the
    // preamble/state read/write did not happen; snapshot and shadows keep their last
    // values, which is exactly the fact-1b contract.
    private async Task<StepOutcome> HandleExecutorFailureAsync(
        StatementExecutionException ex, Interpreter.StatementUnit unit,
        List<string> messages, CancellationToken cancellationToken, bool deferRoute = false)
    {
        // M8 §8.3 (A35): connection-fatal — severity ≥ 20 OR a genuinely broken
        // connection (SqlConnection.State != Open after the command). Session-fatal even
        // in multi-batch script mode: a dead connection cannot run the next batch, so this
        // ends the session rather than advancing (§8.3). Checked FIRST so a connection-
        // fatal error carrying Number 0 is not misclassified as a §10.5 pause below.
        if (ex.SeverityClass >= 20 || ex.ConnectionBroken)
        {
            _broken = true;
            throw new SessionFaultException(
                $"Connection-fatal error (severity {ex.SeverityClass}{(ex.ConnectionBroken ? ", broken connection" : "")}) " +
                $"at line {unit.Span.StartLine}: {ex.Message} — session terminated (§10.1/§8.3).");
        }

        if (ex.Number is -2 or 0)
        {
            // §10.5: engine attention — statement rolled back, session healthy, NOT
            // T-SQL-catchable. Cursor stays ON the unit: retry (step again), skip
            // (Jump to Cursor), or terminate. Number -2 = command timeout; Number 0 =
            // attention-by-SqlCommand.Cancel() (the §10.5 pause mechanism — verified
            // review finding F3: this used to fall through to the compile-class arm
            // below and brick the session on a pause). Severity ≥ 20 is checked FIRST
            // above: a connection-fatal error can also carry Number 0 and must not be
            // misclassified as a mere pause.
            return new StepOutcome(StepDisposition.EngineAttention,
                new ErrorContextValues(ex.Number, 0, 1, unit.Span.StartLine, null, ex.Message));
        }

        // Compile / deferred-name-resolution class (facts 1b/6): natively the batch
        // never begins and the error is uncatchable in its own scope — but an ENCLOSING
        // frame's CATCH legitimately catches it (fact 23-F: the caller's CATCH read 208
        // live). M4: PerformRouteAsync walks from the CALLER down
        // (sameScopeUncatchable), abnormal-popping the faulting frame on a hit; no
        // route anywhere = batch-aborting → terminal, as it was for frame 0 in M3.
        // §10.2 line rule: when the server named a real module, Procedure/LineNumber
        // are genuine engine data and ride verbatim; otherwise the SU's own line.
        var values = new ErrorContextValues(
            ex.Number, ex.SeverityClass, 1,
            ex.Procedure is not null && ex.LineNumber > 0 ? ex.LineNumber : unit.Span.StartLine,
            SynthesizeProcedure(ex.Procedure, CurrentFrame.Module), ex.Message);   // A27 (origin = the faulting callee)
        // A65/F2: defer the route (pend) when the caller asked — a capture flush must not route from
        // inside its pop. The compile-class fault is sameScopeUncatchable, which the pending route
        // reproduces via the stored unit + xactState on the next step.
        return deferRoute
            ? PendFault(values, unit, xactState: 0)
            : await PerformRouteAsync(values, unit, xactState: 0,
                terminalWhenUnhandled: false, messages,
                sameScopeUncatchable: true).ConfigureAwait(false);
    }

    // §10.2 bookkeeping: contexts and CATCH occupancies are LIFO over the same DYNAMIC
    // extents, so count-reconciling after any NON-route cursor move — and, M4, after
    // any frame push/pop — implements every pop rule at once (END CATCH, GOTO/BREAK/
    // CONTINUE/RETURN/JumpTo departures, frame pops out of a CATCH). Routes push
    // explicitly in PerformRouteAsync. M4: the depth is Σ CatchDepth over ALL frames —
    // a frame pushed from within a CATCH adds no context and no depth, which is
    // exactly §10.2's dynamic-extent rule (the callee reads the caller's top context).
    private void ReconcileErrorContexts()
    {
        var depth = TotalCatchDepth();
        if (_errorContexts.Count > depth)
        {
            TrimContextsTo(depth);
            SyncErrorContextState();
        }
    }

    private int TotalCatchDepth()
    {
        var depth = 0;
        foreach (var frame in _frames!.All)
        {
            depth += frame.Cursor.CatchDepth;
        }

        return depth;
    }

    private void TrimContextsTo(int count)
    {
        // A65/F2 (Fable §10 review): `TotalCatchDepth() - 1` is negative when the route lands with
        // no open CATCH occupancy left to replace (a capture materialization fault routed at the call
        // site while the callee's own contexts were already reconciled at its pop) — that means "trim
        // to empty", so floor at 0 rather than underflowing RemoveAt(-1).
        var floor = Math.Max(0, count);
        while (_errorContexts.Count > floor)
        {
            _errorContexts.RemoveAt(_errorContexts.Count - 1);
        }
    }

    private void SyncErrorContextState()
    {
        _shadows!.SetErrorContext(_errorContexts.Count > 0 ? _errorContexts[^1].Values : null);
        _rewriteContext!.ErrorContextActive = _errorContexts.Count > 0;
    }

    private async Task<BatchResult> ExecuteAndTraceAsync(ComposedBatch batch, CancellationToken cancellationToken)
    {
        _trace.Event("batch.send", batch.Text);
        var result = batch.Parameters is { Count: > 0 }
            ? await _executor.ExecuteAsync(batch.Text, batch.Parameters, cancellationToken).ConfigureAwait(false)
            : await _executor.ExecuteAsync(batch.Text, cancellationToken).ConfigureAwait(false);
        TraceBatchResult(result);
        return result;
    }

    // Trailing errors (fact 22: the doomed batch-end 3998 epilogue) are absorbed, not
    // surfaced to the console — natively the doom persists INSIDE the one real batch,
    // so per-step 3998 notices are a debugger artifact. The trace keeps them honest.
    private void TraceBatchResult(BatchResult result)
    {
        _trace.Event("batch.result", $"resultSets={result.ResultSets.Count} messages={result.Messages.Count}");
        if (result.TrailingErrors is { Count: > 0 } trailing)
        {
            _trace.Event("batch.trailing",
                string.Join("; ", trailing.Select(t => $"{t.Number} sev{t.SeverityClass}: {t.Message}")));
        }
    }

    // DESIGN §4 teardown: "always, in finally: ... IF @@TRANCOUNT > 0 ROLLBACK
    // (unless commit path §16) ... ". Connection disposal is the caller's job
    // (LiveSession/SessionHost). Safe to call even if InitializeAsync never
    // completed (the IF guards a no-op ROLLBACK/COMMIT).
    //
    // M7 (§16 commit-modal): <paramref name="commitDecision"/> is the ONE gated
    // branch in this whole method — CLAUDE.md safety rule 7 ("rollback teardown is
    // UNCONDITIONAL except the explicit, ratified commit path"). Only the adapter's
    // EXPLICIT terminate handler ever supplies a non-null callback, and only when
    // launch configured commitMode=Commit (itself refused at launch unless the
    // target's allowWrites:true) — disconnect, error, and lost-adapter paths all
    // call this with NO callback and therefore always roll back, exactly as before.
    // The callback itself talks to the extension (DAP custom event + reply) and
    // returns true only on an explicit, timely "yes".
    public async Task TeardownAsync(Func<Task<bool>>? commitDecision = null)
    {
        // C4 (§16): REVERT the EXECUTE AS impersonation FIRST — ownership context
        // must not outlive the rollback/commit decision below. Always attempted
        // whenever launch configured one (never gated on whether the init-time
        // EXECUTE AS is known to have actually succeeded — same best-effort
        // discipline as the rollback: a REVERT with nothing to revert just errors
        // harmlessly here, e.g. when init failed before EXECUTE AS ever ran).
        if (_options.ExecuteAs is not null)
        {
            try
            {
                await _executor.ExecuteAsync("REVERT;", CancellationToken.None).ConfigureAwait(false);
                _trace.Event("session.revert", "ok");
            }
            catch (Exception ex)
            {
                _trace.Event("session.revert.error", ex.Message);
            }
        }

        var armed = commitDecision is not null && _options.CommitMode == CommitMode.Commit;
        var shouldCommit = false;
        if (armed)
        {
            try
            {
                shouldCommit = await commitDecision!().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _trace.Event("session.commitdecision.error", ex.Message);
            }

            if (!shouldCommit)
            {
                _trace.Event("session.commit.declined", "rolling back instead (§16)");
            }
        }

        try
        {
            if (shouldCommit)
            {
                await _executor.ExecuteAsync("IF @@TRANCOUNT > 0 COMMIT;", CancellationToken.None).ConfigureAwait(false);
                _trace.Event("session.commit", "ok");
            }
            else
            {
                await _executor.ExecuteAsync("IF @@TRANCOUNT > 0 ROLLBACK;", CancellationToken.None).ConfigureAwait(false);
                _trace.Event("session.rollback", "ok");
            }
        }
        catch (Exception ex)
        {
            _trace.Event(shouldCommit ? "session.commit.error" : "session.rollback.error", ex.Message);
        }

        _trace.Event("session.end", string.Empty);
    }

    // The "continue with no stops" degenerate case: initializes (if not already) and
    // steps to completion, then tears down. Used directly by the fidelity harness and
    // by launches that never publish an intermediate stop. M4 (§20.3): the harness's
    // pass 2 is stepKind = Into — step INTO everything that is step-into-eligible.
    // M6 item 6 (B9): boost mode rides the SAME reference driver loop as
    // BoostTestKit.DriveAsync/BoostSessionLiveTests — TryStepBoostedAsync() ??
    // StepAsync(stepKind) at every arrival. TryStepBoostedAsync self-gates on
    // _options.Boost (returns null immediately when Boost is false), so this is
    // unconditional and does not change behavior for non-boosted callers.
    public Task<SessionResult> RunToEndAsync(CancellationToken cancellationToken = default)
        => RunToEndAsync(StepKind.Over, cancellationToken);

    public async Task<SessionResult> RunToEndAsync(StepKind stepKind, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!_initialized)
            {
                await InitializeAsync(cancellationToken).ConfigureAwait(false);
            }

            var resultSets = new List<ResultSet>();
            var messages = new List<string>();
            while (!IsCompleted)
            {
                var boosted = await TryStepBoostedAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                var (stepSets, stepMessages) = boosted ?? await StepAsync(stepKind, cancellationToken).ConfigureAwait(false);
                resultSets.AddRange(stepSets);
                messages.AddRange(stepMessages);
                // A56: fold this step's diagnostic annotations back into the offline
                // record so Execution.Messages stays complete (the adapter drains the same
                // channel live and gates it on logLevel; fidelity reads result sets, not
                // Messages). Init-time notes (NOCOUNT probe) drain on the first iteration.
                messages.AddRange(DrainDiagnosticNotes());

                // §10.3 dispositions in the run-to-end (fidelity) mode: routed faults
                // and statement-level continuations proceed exactly like native
                // execution (the native error text is already in the messages);
                // batch-aborting classes end the run as native execution would. A
                // BatchCompleted (a NORMAL GO boundary, §5.4) just continues the loop —
                // the cursor already sits on the next batch's first SU.
                switch (LastStep.Disposition)
                {
                    case StepDisposition.FrameFaulted:
                        // M8 §8.2/A35 (§10-gated): a BATCH-TERMINAL fault (this disposition
                        // is only ever a batch-aborting class — connection-fatal severity ≥
                        // 20 / broken connections throw in HandleExecutorFailureAsync before
                        // routing) terminates the current batch but the client CONTINUES to
                        // the next (sqlcmd/SSMS default, fact 32a). Classification armed
                        // _pendingBatchAdvance iff more batches remain; the next loop
                        // iteration crosses the boundary (StepAsync → BatchCompleted) via
                        // the §8.1 reconciliation. The native error text is already in
                        // `messages` (streamed like native). Only a SESSION-fatal terminal
                        // fault (last batch / single-batch / procedure mode — not pending)
                        // ends the run as native execution would.
                        if (!_pendingBatchAdvance)
                        {
                            throw new SessionFaultException(
                                $"Unhandled error {LastStep.Error!.Number} (severity {LastStep.Error.Severity}): " +
                                $"{LastStep.Error.Message} (§10.3 batch-aborting class).");
                        }

                        break;
                    case StepDisposition.EngineAttention:
                        throw new SessionFaultException(
                            $"Statement hit an engine attention (§10.5): {LastStep.Error!.Message}");
                }
            }

            // A56: final fold — catches any note produced with no step to trail (e.g. an
            // init note on a script that is IsCompleted before the first iteration).
            messages.AddRange(DrainDiagnosticNotes());

            // M4: the session's return code is frame 0's (§20.3.1.2's returnCode).
            return new SessionResult(RootFrame.Cursor.Index.All, new BatchResult(resultSets, messages), RootFrame.ReturnCode);
        }
        finally
        {
            await TeardownAsync().ConfigureAwait(false);
        }
    }

    // Launch-time only (defaulted-parameter initializers, §11.3-style): runs before
    // the step loop, where §10 routing has no meaning yet — a fault here is a clean
    // launch failure. Step-time DECLARE initializers go through RunDebuggeeBatchAsync
    // instead and route per §10.3.
    private async Task<(IReadOnlyList<ResultSet> ResultSets, IReadOnlyList<string> Messages)> RunSyntheticAssignmentAsync(
        VariableDeclaration decl, CancellationToken cancellationToken)
    {
        var frame = CurrentFrame;
        var batch = ComposedBatchBuilder.BuildSyntheticAssignment(
            frame, _rewriteEngine!, _rewriteContext!, decl, frame.Cursor.Index.FullScript, _shadows!);
        var batchResult = await ExecuteAndTraceAsync(batch, cancellationToken).ConfigureAwait(false);
        var (control, userSets, stateValues) = ControlRowParser.Parse(batchResult, frame.Variables.Count);
        if (stateValues is not null)
        {
            frame.Snapshot = stateValues.ToArray();
        }

        if (!control.Ok)
        {
            throw new SessionFaultException(
                $"Synthetic initializer for {decl.Name} faulted: {control.ErrMessage} (error {control.ErrNumber}).");
        }

        // §7.4/A26 (D1): launch-time defaulted-parameter initializers run before any
        // frame push/pop/doom — the chain is trivially in sync.
        _shadows!.ObserveSuccess(control, scopeChainInSync: true);
        return (userSets, batchResult.Messages);
    }

    // ---------------------------------------------------------------------------------
    // M4 frames machinery (design: docs/archive/reviews/m4-frames-design-notes-fable.md).
    // ---------------------------------------------------------------------------------

    /// <summary>Advance the top frame's cursor, reconcile contexts, then settle any
    /// completed frames (D2: a callee that just performed its last unit pops as a
    /// COMPLETED call — copy-back + @rc, fact 23 — and the pop cascades).</summary>
    private async Task<StepOutcome> AdvanceAndSettleAsync(
        AdvanceSignal signal, List<string> messages)
    {
        CurrentFrame.Cursor.Advance(signal);
        ReconcileErrorContexts();

        // A54 (§6/§11.5): this advance ran a MODULE frame off the end of its body (cursor
        // completed, NOT via an explicit RETURN). PARK at the implicit-return stop instead
        // of popping — one inspection stop at the module's closing line before the pop
        // copies OUTPUT params / applies @rc. The NEXT step consumes _pendingReturnStop
        // (ConsumeReturnStopAsync) and performs the deferred §11.5 pop. continue/stepOut/
        // boost/RunToEnd run THROUGH it (adapter/RunToEnd loop), exactly as they do a GO
        // boundary (A44). SettleCompletionAsync stays an unconditional pop — its boost and
        // fault-routing callers must NOT park (boost runs through; a fault unwind is not a
        // clean return), so the decision lives here, on the normal stepping path only.
        if (ShouldParkAtImplicitReturn())
        {
            _pendingReturnStop = true;
            return new StepOutcome(StepDisposition.AtImplicitReturn);
        }

        var settled = await SettleCompletionAsync(messages).ConfigureAwait(false);

        // M8 (§5.4): a NORMAL GO boundary — this advance exhausted the batch frame's
        // cursor at depth 1 (every callee popped in SettleCompletion) and more batches
        // remain. Cross it now, in the same step that exhausted the batch, so the cursor
        // lands on the next batch's first SU (never a stop on an exhausted cursor). Same
        // no-cancellation invariant as SettleCompletion: a boundary must not be left
        // half-torn-down by a §10.5 pause.
        if (AtBatchBoundary)
        {
            await AdvanceToNextBatchAsync(messages, CancellationToken.None).ConfigureAwait(false);
            return new StepOutcome(StepDisposition.BatchCompleted);
        }

        return settled;
    }

    // A54 (§6/§11.5): consume the parked implicit-return stop at the top of the next step
    // (mirrors StepAsync's AtBatchBoundary cross). The parked frame's body is complete —
    // perform its deferred §11.5 pop (a COMPLETED pop: OUTPUT copy-back + @rc + teardown,
    // fact 23), which also advances the caller past its EXEC — then CASCADE: if the caller
    // is now itself a completed MODULE frame that ran off its end, re-park at ITS implicit
    // return (one stop per unwinding frame); if the pop exposed a GO boundary, cross it. The
    // ROOT proc (frame 0, procedure mode) is never popped: clearing the flag is enough —
    // IsCompleted turns true and the caller (StepAsync/adapter) ends the session. No
    // cancellation token on the pop, same invariant as SettleCompletionAsync.
    private async Task<StepOutcome> ConsumeReturnStopAsync(List<string> messages)
    {
        _pendingReturnStop = false;

        // The root proc's implicit return: nothing to pop (frame 0 never pops — §6). The
        // flag is cleared; IsCompleted is now true, so the caller ends the session.
        if (_frames!.Depth == 1)
        {
            return new StepOutcome(StepDisposition.FrameCompleted);
        }

        var flushOutcome = await PopFrameAsync(completed: true, messages, CancellationToken.None).ConfigureAwait(false);
        if (flushOutcome is not null)
        {
            // §11.7 (A65): the popped frame was a capture callee whose materialization faulted —
            // §10.3 placed the cursor (caller CATCH / terminal). Surface that as the step's outcome
            // instead of the normal implicit-return cascade (the call site did not complete).
            return flushOutcome;
        }

        // The pop advanced the caller past its EXEC. If that ran the caller off its own end
        // and it is a module frame, re-park at the caller's implicit return (the cascade).
        if (ShouldParkAtImplicitReturn())
        {
            _pendingReturnStop = true;
            return new StepOutcome(StepDisposition.AtImplicitReturn);
        }

        // Otherwise settle any remaining completed frames (a script frame 0 completing at
        // depth 1 does not pop here) and cross a GO boundary the pop may have exposed.
        var settled = await SettleCompletionAsync(messages).ConfigureAwait(false);
        if (AtBatchBoundary)
        {
            await AdvanceToNextBatchAsync(messages, CancellationToken.None).ConfigureAwait(false);
            return new StepOutcome(StepDisposition.BatchCompleted);
        }

        return settled.Disposition == StepDisposition.FrameCompleted ? settled : new StepOutcome(StepDisposition.FrameCompleted);
    }

    // ---------------------------------------------------------------------------------
    // M8 (§5.4) multi-batch (GO) lifecycle (docs/archive/reviews/multibatch-script-design-notes-opus.md §3).
    // ---------------------------------------------------------------------------------

    // Per-batch scope setup (§5.4): build the cursor over batch `index`'s statements
    // (fullScript = the WHOLE file, so lines/offsets stay file-absolute — fact 32e),
    // create the batch frame (CreateRoot for batch 0, else EnterBatch replacing the
    // root; a FRESH monotonic ordinal so #__dbg_s{ordinal} never collides — §11.4),
    // register + hoist its own variables, create + seed its state table, and hoist its
    // table-variable realizations at `createdAtTrancount` (batch 0: 0, before BEGIN TRAN,
    // survives rollback like native frame 0; batches ≥ 1: the live trancount,
    // mid-transaction, healed by the §10.4 watchdog on rollback exactly as frame > 0).
    // SET env carries from the outgoing batch's frame (SET options + runtime XACT_ABORT
    // persist across GO — fact 32d); batch 0 takes the blueprint defaults. Procedure mode
    // / single-batch scripts call this exactly once (index 0), reproducing the M4
    // single-frame init byte-for-byte.
    private async Task EnterBatchAsync(int index, int createdAtTrancount, CancellationToken cancellationToken)
    {
        var blueprint = _batches![index];
        var frameKind = _options.Mode == LaunchMode.Script ? FrameKind.Script : FrameKind.Procedure;
        var cursor = ExecutionCursor.Create(
            blueprint.Statements, blueprint.SourceText, frameKind,
            blueprint.Parameters.Select(p => p.Name));

        Frame frame;
        if (_frames is null)
        {
            frame = new Frame(0, blueprint.Module, cursor, blueprint.SetEnv);
            _frames = FrameStack.CreateRoot(frame);
            _trace.Event("frame0.parsed", $"statementUnitCount={cursor.Index.Count}");
            foreach (var su in cursor.Index.All)
            {
                _trace.Event("frame0.su", $"kind={su.Kind}/{su.SubKind} line={su.Span.StartLine}");
            }
        }
        else
        {
            // Depth is 1 at a GO boundary (every EXEC callee has popped). The next batch
            // inherits the outgoing batch's SET env + runtime XACT_ABORT (fact 32d).
            var outgoing = _frames.Current;
            frame = new Frame(_frames.NextOrdinal(), blueprint.Module, cursor, outgoing.SetEnv)
            {
                XactAbortOn = outgoing.XactAbortOn,
            };
            _frames.EnterBatch(frame);
            _trace.Event("batch.enter",
                $"index={index}/{_batches.Count} ordinal={frame.Ordinal} statementUnitCount={cursor.Index.Count}");
            foreach (var su in cursor.Index.All)
            {
                _trace.Event("batch.su", $"kind={su.Kind}/{su.SubKind} line={su.Span.StartLine}");
            }
        }

        _currentBatchIndex = index;

        // A59 (§4 step 2a): re-read the type catalog iff this batch names a type. A previous
        // batch may have created it — including via EXEC('CREATE TYPE …'), the conditional-
        // create idiom, which is invisible to any DDL-triggered refresh.
        await RefreshUserTypesIfFrameDeclaresThemAsync(blueprint.Parameters, cursor.Index, cancellationToken)
            .ConfigureAwait(false);

        // Declaration hoisting (fact 14) — each batch declares ONLY its own catalog, so a
        // later batch re-DECLAREing a name (even at a different type) just works, and a
        // reference to a prior batch's variable composes a batch whose preamble omits it
        // → native error 137, reproducing the GO scope reset structurally (§5.4).
        await RegisterFrameVariablesAsync(frame, blueprint.Parameters, cancellationToken).ConfigureAwait(false);

        await EnsureCursorDefaultIsGlobalAsync(cursor.Index, cancellationToken).ConfigureAwait(false);

        // §4 step 4 / §8.1 / §5.4: create + seed the state table, then hoist table-var
        // realizations at the given trancount.
        await ExecuteAndTraceAsync(StateTableDdlBuilder.BuildCreateTable(frame.Ordinal, frame.Variables.All), cancellationToken).ConfigureAwait(false);
        await ExecuteAndTraceAsync(
            StateTableDdlBuilder.BuildSeedInsert(frame.Ordinal, frame.Variables.All, blueprint.SeedLiterals), cancellationToken).ConfigureAwait(false);
        await HoistTableVariableRealizationsAsync(frame, createdAtTrancount, cancellationToken).ConfigureAwait(false);

        // §10.4 policy: warn when a batch body contains COMMIT (rollback-mode session).
        // Batch 0's warnings surface at launch exactly as before M8; later batches' (rare)
        // surface when that batch is entered.
        foreach (var unit in cursor.Index.All)
        {
            if (unit.SubKind == SuSubKind.Commit)
            {
                _launchWarnings.Add(
                    $"line {unit.Span.StartLine}: COMMIT on a rollback-mode session — if it executes, the safety " +
                    "transaction is committed and that work is permanent (§10.4 policy violation; the watchdog " +
                    "re-opens a fresh transaction and stepping continues).");
            }
        }
    }

    // Per-batch teardown at a GO boundary (§5.4): drop the batch's state table; DROP
    // batch-local table-variable realizations and DEALLOCATE batch-local LOCAL cursors
    // (both die at GO — facts 2/3); PROMOTE surviving connection-scoped objects (user
    // #temp/##global, GLOBAL cursors) into the session-persistent registry so the next
    // batch resolves + inspects them (they physically persist across GO — facts 1/3).
    // Keeps the connection, the safety transaction, session temps, and the tracked SET
    // env. A frame pop's cleanup minus OUTPUT copy-back / @rc (a GO boundary carries no
    // return values). No cancellation: a boundary must not be left half-torn-down.
    private async Task ExitBatchAsync(CancellationToken cancellationToken)
    {
        var outgoing = _frames!.Current;
        _trace.Event("batch.exit", $"index={_currentBatchIndex}/{_batches!.Count} ordinal={outgoing.Ordinal}");

        var cleanup = new List<string> { StateTableDdlBuilder.BuildDropTable(outgoing.Ordinal) };
        foreach (var entry in outgoing.TempObjects.All)
        {
            if (entry.IsDead)
            {
                continue;
            }

            if (entry.SurvivesBatchBoundary)
            {
                // Connection-scoped — carried across GO. Promote the registry entry (the
                // physical object already persists; promotion keeps name resolution,
                // inspection, and A20 collision detection correct in later batches).
                _sessionTempObjects.Add(entry);
                continue;
            }

            // Batch-local — torn down at the boundary so a next-batch reference fails like
            // native (a LOCAL cursor / a table variable does not cross GO).
            var bracketed = RewriteContext.BracketIdentifier(entry.PhysicalName);
            cleanup.Add(entry.Kind switch
            {
                TempObjectKind.Cursor =>
                    $"IF CURSOR_STATUS('global', N'{entry.PhysicalName.Replace("'", "''")}') = 1 CLOSE {bracketed}; " +
                    $"IF CURSOR_STATUS('global', N'{entry.PhysicalName.Replace("'", "''")}') >= -1 DEALLOCATE {bracketed};",
                _ => $"IF OBJECT_ID('tempdb..{entry.PhysicalName}') IS NOT NULL DROP TABLE {bracketed};",
            });
        }

        await ExecuteAndTraceAsync(string.Join("\n", cleanup), cancellationToken).ConfigureAwait(false);
    }

    // Cross a GO boundary (§5.4 / §8.1): tear down the outgoing batch scope and enter the
    // next batch's fresh scope. Entered from AtBatchBoundary (a batch completed normally,
    // depth 1) OR from _pendingBatchAdvance (a §8.2 batch-terminal fault, where the §10.3
    // terminal arm kept the whole stack for the fault-site red UI, so callees may remain).
    // Runs the §8.1 boundary reconciliation FIRST, then ExitBatch + EnterBatch(k+1). This
    // is the M8 lane 1b core — the §10-gated doom-boundary path (was a NotSupported guard
    // in lane 1a).
    private async Task AdvanceToNextBatchAsync(List<string> messages, CancellationToken cancellationToken)
    {
        // §8.1 boundary reconciliation, in order.
        //
        // (1) Pop any callee frames a batch-terminal fault left on the stack (the §10.3
        //     terminal arm keeps the whole stack intact for inspection). A GO boundary
        //     aborts the WHOLE batch — every frame with it — so these are ABORTED pops (no
        //     OUTPUT copy-back / @rc; a boundary carries no return values, fact 23). A
        //     normal boundary is already at depth 1, so this is a no-op there; it also
        //     re-establishes FrameStack.EnterBatch's depth-1 precondition.
        while (_frames!.Depth > 1)
        {
            await PopFrameAsync(completed: false, messages, CancellationToken.None).ConfigureAwait(false);
        }

        // (2) Doom reconciliation (fact 22 / fact 32b at the separator): a doomed
        //     transaction cannot cross a GO boundary — the engine force-rolls it back
        //     there. Emulate it, reusing the EXISTING §10.4 doom→detached transition and
        //     the registry mark-dead-above-0 (no new watchdog state). A session that is
        //     merely DETACHED (a debuggee ROLLBACK earlier in the batch) carries across
        //     UNCHANGED: its C24 skew and the deferred safety-tran re-open — fired by the
        //     next batch's first protected SU via the existing EnsureTransactionProtection
        //     — continue exactly as within a batch. A HEALTHY transaction persists (fact
        //     32c). A _broken session never reaches here (the terminal arm arms
        //     _pendingBatchAdvance instead of _broken, and the driver ends on _broken).
        if (_doomed)
        {
            await ReconcileDoomAtBoundaryAsync(messages, cancellationToken).ConfigureAwait(false);
        }

        // (3) Replace the batch scope. EnterBatch hoists at the CURRENT trancount — 0 when
        //     detached (this boundary's doom reconciliation, or a carried-in detach), ≥ 1
        //     when healthy — so the next batch's table-var realizations are created at the
        //     native post-boundary level. A detached next batch re-opens the safety net on
        //     its first write (the per-SU deferred resurrection), not here.
        //
        //     M8 FIX (§10.1/§8.3, §10-gated): the next batch may fail the debugger's
        //     CLIENT-SIDE compile validation — EnterBatchAsync's ExecutionCursor.Create runs
        //     ControlFlowMap.BuildAndValidate / MilestoneValidator, which throw
        //     ParseTimeDiagnosticException / MilestoneNotSupportedException (e.g. an
        //     undeclared-variable reference, engine 137 — p26's batch 3). Natively that is
        //     the SAME event as a SERVER-reported compile fault (208): the engine refuses the
        //     batch WHOLESALE (no statement runs) and the client CONTINUES to the next batch
        //     (sqlcmd/SSMS default, fact 32a; §8.3 batch-aborting class). Unlike a
        //     server-reported fault this one is caught HERE, not in PerformRouteAsync's
        //     terminal arm, because it fires while BUILDING the batch's cursor, before the
        //     batch is ever live. ExecutionCursor.Create is the FIRST thing EnterBatchAsync
        //     does — it throws BEFORE any session mutation or any SQL — so a refusal leaves
        //     the session in the clean post-ExitBatch state; skip the batch and try the next.
        //     A compile refusal has no cursor/SU/frame to anchor a stopped:exception at (the
        //     batch never entered), so it surfaces as a locatable DIAGNOSTIC message plus the
        //     ordinary batch advance — NOT a synthetic FrameFaulted (the interactive-fault-site
        //     decision; docs/archive/reviews/m8-multibatch-fix-opus.md). Batch 0's identical throw
        //     stays a LAUNCH failure (InitializeAsync, not here) — the documented batch-0
        //     residual (a multi-batch script whose FIRST batch won't compile is refused at
        //     launch; native would run the later batches).
        var fromIndex = _currentBatchIndex;
        await ExitBatchAsync(cancellationToken).ConfigureAwait(false);
        _pendingBatchAdvance = false;         // the boundary is consumed regardless of how many compile-refused batches are skipped

        for (var target = fromIndex + 1; target < _batches!.Count; target++)
        {
            try
            {
                await EnterBatchAsync(target, _lastObservedTrancount, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is ParseTimeDiagnosticException or MilestoneNotSupportedException)
            {
                RecordCompileRefusedBatch(target, ex, messages);
                continue;                     // batch `target` aborts at compile (fact 32a); try the next
            }

            // Landed on a runnable batch — its fresh scope is set up and the cursor is on its
            // first SU.
            _trace.Event("batch.advance",
                $"from={fromIndex} to={_currentBatchIndex} count={_batches.Count} detached={_detached}");
            var pos = _batchPositions![_currentBatchIndex];
            var iterationSuffix = pos.Repeat > 1 ? $", GO {pos.Repeat} iteration {pos.Iteration}/{pos.Repeat}" : "";
            // A56: navigational annotation — routed to the logLevel-gated diagnostic channel.
            _diagnosticNotes.Add(
                $"-- GO: entering batch {pos.PhysicalIndex + 1} of {pos.PhysicalCount}{iterationSuffix} (§5.4: fresh " +
                "variable scope; #temp/SET options/transaction persist across the boundary).");
            return;
        }

        // Every batch after `fromIndex` was compile-refused — no runnable batch remains, so
        // the script is exhausted. This is a COMPLETION, not a fault: a compile-refused batch
        // never executed, so the session state is exactly the clean post-boundary state and
        // the result sets prior batches already streamed stand (the sqlcmd-continue oracle
        // produces them; §20.3/A37). IsCompleted keys off _batchListDone (the live batch
        // frame is still fromIndex's, whose scope was torn down above).
        _batchListDone = true;
        _trace.Event("batch.advance.exhausted",
            $"from={fromIndex} count={_batches.Count} — every later batch was compile-refused; script complete");
        messages.Add(
            "-- GO: no runnable batch remains after the compile-refused batch(es); the script is complete " +
            "(prior batches' output stands; §5.4/§8.3).");
    }

    // M8 FIX (§10.1/§8.3): a batch the CLIENT-SIDE compile validator refuses
    // (ParseTimeDiagnosticException / MilestoneNotSupportedException, thrown while building
    // the batch's cursor in ExecutionCursor.Create) is native's wholesale compile refusal of
    // THAT batch — the engine runs nothing in it (engine 137 for an undeclared variable,
    // etc.) and continues to the next batch (fact 32a). Surface a faithful, locatable
    // diagnostic into the message stream: the exception's own message carries the offending
    // file line(s). Compile errors are severity 15/16, so this text is NOT part of the
    // §20.3.1.3 fidelity `messages` comparison — it is UX / --trace only and cannot perturb a
    // projection.
    private void RecordCompileRefusedBatch(int batchIndex, Exception compileFault, List<string> messages)
    {
        _trace.Event("batch.compile-refused",
            $"index={batchIndex}/{_batches!.Count} {compileFault.GetType().Name}");
        messages.Add(
            $"-- GO: batch {batchIndex + 1} of {_batches.Count} fails T-SQL compile validation and is aborted at " +
            "compile — native refuses it before running any statement and continues to the next batch " +
            $"(§10.1 no-control-row compile class; §8.3; fact 32a). Detail: {compileFault.Message}");
    }

    // §8.1 / §10.4 (A35, §10-gated): fact 22 at the GO separator. A doomed transaction
    // cannot cross a batch boundary — natively the engine raises 3998 at batch end and
    // force-rolls it back, so the next batch starts at @@TRANCOUNT = 0 with everything
    // created inside the doomed transaction destroyed. Emulate that WITHOUT inventing new
    // watchdog state: force the transaction to trancount 0, then take the EXISTING
    // doom→detached transition (as ObserveControlRowAsync's trancount-1→0 edge does) — but
    // SKIP the frame reseed, because the outgoing batch scope is torn down immediately
    // after (ExitBatch) and the next batch gets a fresh one. Registries reconcile via
    // MarkDeadAbove(0): every temp created above trancount 0 — i.e. inside the transaction,
    // and every debuggee #temp is created at trancount ≥ 1 under §4 step 5 — is marked
    // dead, matching native (a #temp created in the doomed transaction does not survive GO;
    // fact 1 + fact 22 compose). Between doomed steps the server already holds trancount 0
    // (each redoom composed batch is force-rolled at its own end, fact 22), so the guarded
    // ROLLBACK is typically a no-op; it is issued for robustness and to make the fact-22
    // intent explicit in the trace.
    private async Task ReconcileDoomAtBoundaryAsync(List<string> messages, CancellationToken cancellationToken)
    {
        await ExecuteAndTraceAsync("IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;", cancellationToken).ConfigureAwait(false);
        _doomed = false;
        _detached = true;
        _lastObservedTrancount = 0;
        _lastObservedXactState = 0;                 // native post-force-rollback: committable/none
        _sessionTempObjects.MarkDeadAbove(0);
        foreach (var frame in _frames!.All)
        {
            frame.TempObjects.MarkDeadAbove(0);
        }

        _trace.Event("txn.doom.boundary",
            $"batch={_currentBatchIndex} force-rollback → detached (fact 22 at the GO separator)");
        messages.Add(
            "-- GO: the doomed transaction was force-rolled back at the batch boundary (§10.4/fact 22): the next " +
            "batch starts at @@TRANCOUNT = 0 and every #temp created inside the doomed transaction is gone " +
            "(native = debugger).");
    }

    // No cancellation token by design (ruling rider (i), same invariant as
    // ObserveControlRowAsync): a completed frame's settle (§11.5 copy-back + @rc +
    // cleanup, multiple batches) is post-outcome bookkeeping — a §10.5 pause must
    // not abort it half-popped. (Abnormal pops inside the routing walk still ride
    // the walk's own token — see the triage build record's residual note.)
    private async Task<StepOutcome> SettleCompletionAsync(List<string> messages)
    {
        var popped = false;
        while (_frames!.Depth > 1 && _frames.Current.Cursor.IsCompleted)
        {
            var flushOutcome = await PopFrameAsync(completed: true, messages, CancellationToken.None).ConfigureAwait(false);
            popped = true;
            if (flushOutcome is not null)
            {
                // §11.7 (A65): a capture materialization faulted — §10.3 already placed the cursor
                // (caller CATCH, or kept for a terminal). Stop settling; that outcome IS the step's.
                return flushOutcome;
            }
        }

        return popped ? new StepOutcome(StepDisposition.FrameCompleted) : StepOutcome.Performed;
    }

    // §11.5 pop sequence, both flavors (fact 23):
    //   completed → OUTPUT copy-back + `EXEC @rc =` server-side, caller snapshot
    //               refreshed, cleanup, SET restores, pop, caller advances past the
    //               EXEC (the cascade caller re-checks completion);
    //   aborted   → NO copy-back/@rc (caller vars keep pre-call values); cleanup +
    //               SET restores only; the caller's cursor was already placed by the
    //               routing walk — never advanced here.
    // While doomed, no DDL/DML runs (3930; the fact-22 forced rollback reaped every
    // mid-transaction object anyway) — SET restores still run (SETs are legal in a
    // doomed transaction, and native module exit reverts them even then, fact 9).
    // §11.5 pop. Returns null normally; for an INSERT…EXEC capture frame (§11.7/A65) a COMPLETED
    // pop MATERIALIZES the stage into the real target as the caller's call-site statement, and a
    // fault there returns the routed outcome (the caller must then NOT advance past the EXEC —
    // the routing walk already placed the cursor).
    private async Task<StepOutcome?> PopFrameAsync(bool completed, List<string> messages, CancellationToken cancellationToken)
    {
        var frames = _frames!;
        var callee = frames.Current;
        var callSite = callee.CallSite
            ?? throw new InvalidOperationException("Pop of a frame with no call site — root pops are the session-end path (§6).");
        var caller = frames.All[frames.Depth - 2];
        var captureFlushSql = callee.CaptureFlushSql;   // §11.7: non-null iff this is a capture callee

        if (!_doomed)
        {
            if (completed && (callSite.OutputPairs.Count > 0 || callSite.ReturnCodeVariable is not null))
            {
                var rcParam = $"@__dbg{_nonce}_ret";
                var copyBack = StateTableDdlBuilder.BuildOutputCopyBack(
                    caller.Ordinal, callee.Ordinal, callSite.OutputPairs, callSite.ReturnCodeVariable, rcParam);
                _trace.Event("batch.send", copyBack);
                var copyResult = callSite.ReturnCodeVariable is not null
                    ? await _executor.ExecuteAsync(copyBack, new[] { new BatchParameter(rcParam, callee.ReturnCode) }, cancellationToken).ConfigureAwait(false)
                    : await _executor.ExecuteAsync(copyBack, cancellationToken).ConfigureAwait(false);
                TraceBatchResult(copyResult);

                // The copy-back changed caller variables server-side — refresh the
                // caller's snapshot so doomed seeding/resurrection stay exact (D1).
                var readBack = await ExecuteAndTraceAsync(StateTableDdlBuilder.BuildSelectAll(caller.Ordinal), cancellationToken).ConfigureAwait(false);
                if (caller.Variables.Count > 0 && readBack.ResultSets is [{ Rows: [var callerRow, ..] }, ..])
                {
                    caller.Snapshot = callerRow.ToArray();
                }
            }

            var cleanup = new List<string> { StateTableDdlBuilder.BuildDropTable(callee.Ordinal) };
            foreach (var entry in callee.TempObjects.All)
            {
                if (entry.IsDead)
                {
                    continue;
                }

                // Real T-SQL drops callee-scoped objects at module exit (§9) — for
                // BOTH pop flavors (an aborted callee's #temps die natively too).
                var bracketed = RewriteContext.BracketIdentifier(entry.PhysicalName);
                cleanup.Add(entry.Kind switch
                {
                    TempObjectKind.Cursor =>
                        $"IF CURSOR_STATUS('global', N'{entry.PhysicalName.Replace("'", "''")}') = 1 CLOSE {bracketed}; " +
                        $"IF CURSOR_STATUS('global', N'{entry.PhysicalName.Replace("'", "''")}') >= -1 DEALLOCATE {bracketed};",
                    _ => $"IF OBJECT_ID('tempdb..{entry.PhysicalName}') IS NOT NULL DROP TABLE {bracketed};",
                });
            }

            cleanup.AddRange(_runtimeOptions.RestoreStatements(callSite.RuntimeOptionsAtEntry));
            await ExecuteAndTraceAsync(string.Join("\n", cleanup), cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // Doomed pop: bookkeeping only, plus the §11.2 restores (fact 9 applies to
            // native module exits in doomed transactions too; SETs are not writes).
            var restores = _runtimeOptions.RestoreStatements(callSite.RuntimeOptionsAtEntry);
            if (restores.Count > 0)
            {
                await ExecuteAndTraceAsync(string.Join("\n", restores), cancellationToken).ConfigureAwait(false);
            }
        }

        frames.Pop();
        _trace.Event("frame.pop",
            $"ordinal={callee.Ordinal} module={callee.Module.Display} completed={completed} returnCode={callee.ReturnCode}");

        // SCOPE_IDENTITY() is per-scope natively: the caller resumes reading its own
        // pre-call value. @@ROWCOUNT/@@ERROR deliberately keep the callee's last
        // observation — natively they carry across module exit.
        _shadows!.RestoreScopeIdentity(callSite.CallerScopeIdentityAtEntry);
        // §7.4/A26 (D1): the shadow is restored, but the SERVER chain still holds
        // whatever the flattened callee last did (its own identity value, or NULL from
        // the push seed) — either is wrong for the caller. Poison until a completed
        // insert-family SU re-syncs. BOTH pop flavors (completed + abnormal) leak
        // identically, and this runs before the return so the caller's next capture is gated.
        _scopeChainPoisoned = true;

        // §11.7 (A65): an INSERT…EXEC capture frame's stage. On a COMPLETED pop it is FIRST
        // materialized into the real target, run as the caller's call-site statement so the flush's
        // @@ROWCOUNT/SCOPE_IDENTITY reach the R6 shadow (native's caller post-INSERT…EXEC intrinsics)
        // and a materialization fault (515/547/2627) routes through §10.3 as a fault of that call
        // site. An ABNORMAL pop discards the stage un-materialized — the target is left EMPTY,
        // matching native's buffer-then-materialize atomicity (I7).
        //
        // F2 (Fable §10 review, re-reviewed 2026-07-17): a capture callee can DOOM (XACT_ABORT ON + a
        // caught error) and still reach a completed pop. Native `INSERT … EXEC` into a doomed
        // transaction raises 3930 (routed to a caller CATCH, target empty). The debugger does NOT route
        // that — it TERMINATES honestly at the call site — for THREE load-bearing reasons (not merely
        // "re-entrant routing is awkward"; the re-review confirmed the deferRoute pipeline could
        // MECHANICALLY pend a 3930, so the real justification is fidelity, not plumbing):
        //   1. The fact-22 forced rollback already DESTROYED the stage #temp (and any caller #temp /
        //      table-var target realization) the instant the callee doomed (§10.4/D8; doomed
        //      re-creation is forbidden). A real flush attempt therefore raises 208 (missing object) —
        //      wrong number AND wrong class (208 is §10.1 same-scope-UNcatchable; native 3930 IS
        //      same-scope-catchable). No honest engine statement yields a real 3930 here.
        //   2. Routing would thus require SIMULATING a 3930 into debuggee CATCH control flow — the
        //      caller's R7-rewritten ERROR_NUMBER()/ERROR_MESSAGE() would serve values no server
        //      produced. That violates the §10.4/A14 "no engine errors are simulated" doctrine (the
        //      same doctrine C23 honors by surfacing a real-but-different error over a simulated one).
        //   3. The doomed pop skips OUTPUT copy-back (the `if (!_doomed)` gate above); a routed CATCH
        //      would read UNVERIFIED pre-call OUTPUT state (native's doomed-INSERT…EXEC OUTPUT
        //      semantics are not live-verified).
        // So terminal is honest (NEVER the pre-fix silent success) and, on this doctrine, strictly
        // better than routing — at the cost of not delivering 3930 into a caller CATCH (the C11
        // doomed-capture residual, §11.7). §10.3/A35 STILL applies: doomed-unhandled is BATCH-terminal,
        // so in multi-batch script mode with more batches remaining this arms _pendingBatchAdvance (the
        // next step force-rolls at the GO boundary and runs the next batch, fact 32a) rather than
        // bricking the session; only the last batch / single-batch script / procedure mode is
        // connection-fatal. A HEALTHY flush materializes and any fault (515/547/2627) is DEFERRED-routed
        // (FlushCaptureAsync, deferRoute).
        StepOutcome? flushOutcome = null;
        if (captureFlushSql is not null)
        {
            if (completed && _doomed)
            {
                flushOutcome = new StepOutcome(StepDisposition.FrameFaulted,
                    new ErrorContextValues(3930, 16, 1, callSite.CallUnit.Span.StartLine, null,
                        "The current transaction cannot be committed and the INSERT … EXEC cannot materialize its " +
                        "captured rows. Roll back the transaction. (§11.7 doomed capture.)"));

                // §10.3/A35: doomed-unhandled is BATCH-terminal, not connection-fatal. With more
                // batches remaining, native force-rolls at the GO boundary and runs the next batch
                // (fact 32a): publish the fault-site stop (FrameFaulted) and arm _pendingBatchAdvance
                // so the next step crosses via AdvanceToNextBatch (which runs the §8.1 doom rollback +
                // aborted callee pops). The last batch / single-batch script / procedure mode
                // (MoreBatchesRemain always false there) stays connection-fatal.
                if (MoreBatchesRemain)
                {
                    _pendingBatchAdvance = true;
                    messages.Add(
                        "The INSERT … EXEC could not materialize its captured rows: the transaction is doomed " +
                        "(uncommittable) — natively this is error 3930 and the target stays empty. Batch " +
                        $"{_currentBatchIndex + 1} of {_batches!.Count} is terminated (§10.3/§11.7 batch-aborting " +
                        "class); execution continues at the next batch. Step OVER the INSERT … EXEC for native's " +
                        "caller-CATCH behavior.");
                }
                else
                {
                    _broken = true;
                    messages.Add(
                        "The INSERT … EXEC could not materialize its captured rows: the transaction is doomed " +
                        "(uncommittable) — natively this is error 3930 and the target stays empty. The debugger " +
                        "terminates the run here (the doomed session's exits are ROLLBACK or teardown, §10.4/§11.7); " +
                        "step OVER the INSERT … EXEC for native's caller-CATCH behavior.");
                }
            }
            else if (completed)
            {
                flushOutcome = await FlushCaptureAsync(
                    frames.Current, callSite.CallUnit, captureFlushSql, callSite.CallerScopeIdentityAtEntry,
                    callee.CaptureTargetHasIdentity, messages, cancellationToken).ConfigureAwait(false);
            }

            // F7 (Fable §10 review): a command timeout DURING the flush (EngineAttention) advertises a
            // retry — keep the stage so a re-flush is possible rather than dropping it and forcing the
            // whole callee to re-run (documented residual). Otherwise drop once the flush is done, but
            // only when healthy (a DROP while doomed faults 3930; the fact-22 rollback / teardown reaps
            // the stage anyway).
            if (!_doomed && flushOutcome?.Disposition != StepDisposition.EngineAttention)
            {
                var stageBracket = RewriteContext.BracketIdentifier(CaptureStageName(callee.Ordinal));
                await ExecuteAndTraceAsync(
                    $"IF OBJECT_ID('tempdb..{CaptureStageName(callee.Ordinal)}') IS NOT NULL DROP TABLE {stageBracket};",
                    cancellationToken).ConfigureAwait(false);
            }
        }

        // The call site is performed; the caller resumes AFTER it. Abnormal pops never advance — the
        // routing walk placed the cursor. A capture flush that FAULTED did not perform the call site
        // (§10.3 placed the cursor at the caller's CATCH / kept it for a terminal), so it too skips.
        if (completed && flushOutcome is null)
        {
            frames.Current.Cursor.Advance(AdvanceSignal.Normal);
        }

        ReconcileErrorContexts();
        return flushOutcome;
    }

    // §11.7 (A65): materialize a completed capture callee's stage into the real target. Composed in
    // — and run as — the CALLER frame's call-site statement (the caller is already current; the
    // callee was popped), so this reuses the whole §10 debuggee-batch path: faults route via §10.3
    // (a caller TRY catches, else terminal/continuation), the flush's @@ROWCOUNT/SCOPE_IDENTITY sync
    // through ObserveDebuggeeSuccess (native's caller post-INSERT…EXEC intrinsics — closes most of
    // I9), and the "(N rows affected)" note fires once with the total. Returns the routed outcome on
    // a fault (caller must not advance past the EXEC), null on success.
    private async Task<StepOutcome?> FlushCaptureAsync(
        Frame caller, Interpreter.StatementUnit callSiteUnit, string flushSql,
        decimal? callerScopeIdentityPreCall, bool targetHasIdentity,
        List<string> messages, CancellationToken cancellationToken)
    {
        var batch = ComposeDebuggeeBatch(() => ComposedBatchBuilder.BuildForCaptureFlush(
            caller, _rewriteContext!, flushSql, _shadows!, DebuggeeComposition(caller)));
        var flushSets = new List<ResultSet>();
        var outcome = await RunDebuggeeBatchAsync(batch, callSiteUnit, flushSets, messages, cancellationToken, deferRoute: true)
            .ConfigureAwait(false);

        // F6 (Fable §10 review): a ZERO-row flush into an IDENTITY target must not move the caller's
        // SCOPE_IDENTITY — native leaves it at the pre-call value (a 0-row insert generates no
        // identity), but the flush's control row reports SCOPE_IDENTITY() = NULL, which
        // ObserveDebuggeeSuccess (insert-family) would adopt. Restore the pre-call value and re-poison.
        // A non-identity target already reads NULL faithfully (Fable P1), so it is left alone.
        if (outcome is null && targetHasIdentity && _shadows!.RowCount == 0)
        {
            _shadows.RestoreScopeIdentity(callerScopeIdentityPreCall);
            _scopeChainPoisoned = true;
        }

        _trace.Event("capture.flush",
            $"callSite=line {callSiteUnit.Span.StartLine} outcome={(outcome is null ? "ok" : outcome.Disposition.ToString())}");
        return outcome;
    }

    // A58 (§11.6): everything a callee frame needs, however it was sourced — a catalog module
    // (OBJECT_DEFINITION + sys.sql_modules) or a dynamic-SQL string evaluated at the call site.
    // From actual-to-formal matching onward the two take exactly the same code path
    // (PushCalleeFrameAsync), which is the whole point of A58: a dynamic frame is an ordinary
    // §11 frame whose text happened to be computed a moment ago rather than read from the catalog.
    private sealed record CalleePlan(
        string DisplayName,                                  // how console notes name this callee
        string Text,                                         // the frame's FullScript (§5.3 slice source)
        ModuleIdentity Identity,
        SetOptionEnvironment SetEnv,
        FrameKind FrameKind,
        IList<TSqlStatement> Body,
        IReadOnlyList<CalleeParameter> Formals,
        IList<ExecuteParameter> Actuals,
        CaptureStagePlan? CaptureStage = null);              // C11 (A65): INSERT…EXEC capture staging plan, or null

    // §11.1 (A58/A64): a step-into candidate is a plain `EXEC proc` (SubKind.Execute) or an
    // `INSERT <target> EXEC proc` (an InsertStatement whose source is an ExecuteInsertSource — C11).
    // Both route through TryStepIntoAsync; the latter carries the capture target.
    private static bool IsStepIntoCandidate(Interpreter.StatementUnit unit) =>
        unit.SubKind == SuSubKind.Execute
        || unit.Fragment is InsertStatement { InsertSpecification.InsertSource: ExecuteInsertSource };

    // C11 (A64): the INSERT…EXEC target as a ready-to-splice SQL prefix — the target reference
    // rewritten (R1/R2) to its physical name in the CALLER's scope (a #temp / table-variable
    // realization the callee's composed batch references by name, §9), plus the optional column list
    // byte-exact from source. Splices as `INSERT INTO <this> <callee result statement>`.
    // §11.7 (C11/A65): resolve the staging plan for an `INSERT <target> EXEC` capture, or refuse
    // (→ step-over, where native performs the whole capture as one batch — faithful). The target's
    // physical reference and (optional) explicit column list are resolved ONCE in the caller's scope
    // (R1/R2), and the stage's schema is derived from the target by a describe round-trip (the A59
    // server-as-oracle move — no client-side type mapping). `ordinal` is the callee frame's, peeked.
    private async Task<CaptureStageResolution> ResolveCaptureStageAsync(
        InsertStatement insert, Frame caller, int ordinal, CancellationToken cancellationToken)
    {
        var spec = insert.InsertSpecification;
        var callerScript = caller.Cursor.Index.FullScript;
        var targetPhysicalSql = _rewriteEngine!.Rewrite(spec.Target, callerScript, _rewriteContext!).PatchedText;

        // F4 (Fable §10 review): a VIEW target diverges — `describe_first_result_set` marks a view's
        // derived columns `is_computed`, which the no-list planner would silently FILTER, so the flush
        // succeeds where native `INSERT <view> EXEC` hard-refuses (msg 4406, uncatchable, the callee
        // never runs). Refuse capture into a view (→ step-over, native-faithful). A `#temp`/`##global`/
        // table-variable realization (all contain `#`) is a base table by construction — skip the probe.
        if (!targetPhysicalSql.Contains('#'))
        {
            var isViewProbe = await ExecuteAndTraceAsync(
                $"SELECT OBJECTPROPERTY(OBJECT_ID(N'{targetPhysicalSql.Replace("'", "''")}'), 'IsView');",
                cancellationToken).ConfigureAwait(false);
            var isView = isViewProbe.ResultSets is [{ Rows: [[var flag, ..], ..] }, ..]
                && flag is not null && Convert.ToInt32(flag) == 1;
            if (isView)
            {
                return CaptureStageResolution.Refuse("the INSERT target is a view (native msg 4406)");
            }
        }

        var hasExplicitColumnList = spec.Columns is { Count: > 0 };
        string projectionSelect;
        string targetColumnListSql;
        if (hasExplicitColumnList)
        {
            var cols = spec.Columns.Select(c => callerScript.Substring(c.StartOffset, c.FragmentLength)).ToList();
            targetColumnListSql = " (" + string.Join(", ", cols) + ")";
            projectionSelect = $"SELECT {string.Join(", ", cols)} FROM {targetPhysicalSql}";
        }
        else
        {
            targetColumnListSql = string.Empty;
            projectionSelect = $"SELECT * FROM {targetPhysicalSql}";
        }

        var describe = await ExecuteAndTraceAsync(
            CaptureStagePlanner.BuildDescribeQuery(projectionSelect), cancellationToken).ConfigureAwait(false);
        var described = CaptureStagePlanner.ParseDescribe(
            describe.ResultSets.Count > 0 ? describe.ResultSets[0] : null);

        var seqColumnName = $"__dbg{_nonce}_seq";
        return CaptureStagePlanner.BuildPlan(
            CaptureStageName(ordinal), seqColumnName, targetPhysicalSql, targetColumnListSql,
            hasExplicitColumnList, described);
    }

    // §11.7 (A65): a capture stage's frame-unique #temp name — nonce-namespaced (CLAUDE.md rule 4)
    // and ordinal-keyed, so the pop can reconstruct it without threading a field through the frame.
    private string CaptureStageName(int ordinal) => $"#__dbgcap{_nonce}_{ordinal}";

    // §11.1/§11.3 step-into. Returns null to fall back to step-over — for callee
    // shapes the debugger can't push (C8 encrypted/unreadable, C9 TVP, §11.6's ineligible
    // dynamic shapes), for erroneous calls (arg-shape mismatches: executing the EXEC natively
    // surfaces the engine's own error through the §10.3 pipeline, which is more
    // faithful than any synthetic), and while doomed (state tables can't be created
    // under 3930). Non-null = the disposition: SteppedIn, or the routed/terminal
    // outcome of a faulting argument evaluation (§11.3 step 1), or synthetic 217.
    private async Task<StepOutcome?> TryStepIntoAsync(
        Interpreter.StatementUnit unit, List<string> messages, CancellationToken cancellationToken)
    {
        var frames = _frames!;
        var caller = frames.Current;

        if (_doomed)
        {
            messages.Add("Step-into is unavailable while the transaction is doomed (the callee's state table " +
                         "cannot be created under error 3930) — stepping over instead (§10.4).");
            return null;
        }

        // C11 (A64): a step-into candidate is a plain `EXEC proc` (ExecuteStatement) OR the EXEC source
        // of an `INSERT <target> EXEC proc` (ExecuteInsertSource); both carry an ExecuteSpecification.
        // For the latter, captureInsert names the target the callee's result stream must land in.
        ExecuteSpecification specification;
        InsertStatement? captureInsert = null;
        switch (unit.Fragment)
        {
            case ExecuteStatement executeStatement:
                specification = executeStatement.ExecuteSpecification;
                break;
            case InsertStatement { InsertSpecification.InsertSource: ExecuteInsertSource { Execute: { } insertExec } } insert:
                specification = insertExec;
                captureInsert = insert;
                break;
            default:
                return null;
        }

        // C11 (A64) MVP: a nested step-into INSIDE an INSERT…EXEC capture is step-over — the nested
        // call's result stream is captured by the wrapping INSERT…EXEC composition of THIS frame
        // instead (no capture propagation into child frames in this milestone).
        if (caller.CaptureTargetSql is not null)
        {
            messages.Add("Stepping into a nested call inside an INSERT … EXEC capture is step-over in this milestone — stepping over (C11).");
            return null;
        }

        // C11 (A64): the OUTER INSERT must be a plain `INSERT <target> EXEC` — a TOP filter caps the
        // captured rows, and a streaming OUTPUT clause on the INSERT is a native error (msg 483). The
        // per-statement capture models neither, so step over and let the engine apply/refuse them.
        if (captureInsert is not null)
        {
            if (captureInsert.InsertSpecification.TopRowFilter is not null)
            {
                messages.Add("INSERT TOP (n) … EXEC is step-over in this milestone — stepping over (C11).");
                return null;
            }

            if (captureInsert.InsertSpecification.OutputClause is not null)
            {
                messages.Add("INSERT … OUTPUT … EXEC is step-over (native msg 483 forbids it) — stepping over (C11).");
                return null;
            }
        }

        // §11.6: shapes with no local frame to push. A linked-server EXEC runs remotely; an
        // EXECUTE AS clause switches the callee's security principal (C4 territory) — neither is
        // modelled, so both step over and execute natively.
        if (specification.LinkedServer is not null || specification.ExecuteContext is not null)
        {
            messages.Add("EXEC AT <linked server> / WITH EXECUTE AS is step-over — stepping over (§11.6).");
            return null;
        }

        // A58 (§11.6): EXEC(@str). ScriptDom models the `+`-concatenated operand list as
        // ExecutableStringList.Strings (verified: IList<ValueExpression>), so the dynamic text is
        // the concatenation of the evaluated elements. Unlike sp_executesql, EXEC() accepts
        // varchar as well as nvarchar, so no provably-nvarchar gate applies here.
        if (specification.ExecutableEntity is ExecutableStringList stringList)
        {
            if (captureInsert is not null)
            {
                messages.Add("INSERT … EXEC(@sql) (dynamic source) is step-over in this milestone — stepping over (C11).");
                return null;
            }

            if (DepthLimitReached(frames, ModuleIdentity.Dynamic(null, "x").NestCost))
            {
                return await RouteNestingLimitAsync(unit, messages).ConfigureAwait(false);
            }

            return await StepIntoDynamicAsync(
                caller, unit, specification, stringList.Strings, requireNVarchar: false,
                paramsExpression: null, actuals: new List<ExecuteParameter>(), displayName: "EXEC(…)",
                messages, cancellationToken).ConfigureAwait(false);
        }

        if (specification.ExecutableEntity is not ExecutableProcedureReference procedureEntity)
        {
            messages.Add("This EXEC shape is step-over — stepping over (§11.6).");
            return null;
        }

        if (procedureEntity.ProcedureReference?.ProcedureVariable is not null
            || procedureEntity.ProcedureReference?.ProcedureReference?.Name is not { } procedureName)
        {
            messages.Add("EXEC via a procedure-name variable is step-over in this milestone — stepping over (C10).");
            return null;
        }

        var nameText = caller.Cursor.Index.FullScript.Substring(
            procedureName.StartOffset, procedureName.FragmentLength);

        // §11.3 step 3: the debugger's virtual frames must mirror the engine's 32-level limit —
        // a stepped-over EXEC here would run at server nesting 1 and NOT reproduce it, so the 217
        // is synthesized through §10.3. A58: the cost is read from the call SHAPE (a dynamic frame
        // costs 2 levels, fact 33e), because the plan — and its identity — isn't built yet.
        if (DepthLimitReached(frames, IsSpExecuteSqlName(nameText) ? 2 : 1))
        {
            return await RouteNestingLimitAsync(unit, messages).ConfigureAwait(false);
        }

        var blueprint = await FetchModuleBlueprintAsync(nameText, cancellationToken).ConfigureAwait(false);

        // A58 (§11.6): sp_executesql is a NATIVE module — it has no sys.sql_modules row, which is
        // why it used to land in the C8 "no readable definition" refusal, a mislabel. Recognise it
        // only once the catalog fetch has come back empty, so that a user procedure legitimately
        // shadowing the name (an `sp_`-prefixed name resolves in the current database first) is
        // still stepped into as itself — which is what the engine would execute.
        if (blueprint is null && IsSpExecuteSqlName(nameText))
        {
            if (captureInsert is not null)
            {
                messages.Add("INSERT … EXEC sp_executesql (dynamic source) is step-over in this milestone — stepping over (C11).");
                return null;
            }

            return await StepIntoSpExecuteSqlAsync(
                caller, unit, specification, procedureEntity, messages, cancellationToken).ConfigureAwait(false);
        }

        if (blueprint is null)
        {
            messages.Add($"Cannot step into '{nameText}': no readable definition (missing, encrypted, natively " +
                         "compiled, or VIEW DEFINITION denied) — stepping over (C8, §11.1).");
            return null;
        }

        // M7 (§5.1/§5.2): a multi-match sourceMap warning discovered by THIS fetch
        // (first need for this callee's identity) — drained into the step's own
        // messages channel since that's what's in hand here.
        if (_sourceMapWarnings.Remove(nameText, out var sourceMapWarning))
        {
            messages.Add(sourceMapWarning);
        }

        var parsed = ScriptParser.Parse(blueprint.Definition, blueprint.QuotedIdentifier, _compatLevel, out var parseErrors);
        if (parseErrors.Count > 0)
        {
            messages.Add($"Cannot step into '{nameText}': its definition did not parse — stepping over.");
            return null;
        }

        var procedureBody = FrameBodyResolver.ResolveProcedureBody(parsed);

        // C11 (A64): capture works by prefixing each result-returning callee statement with
        // `INSERT INTO <target> `. A few statement shapes cannot be captured faithfully that way (a
        // CTE-headed SELECT can't be prefixed; a callee ROLLBACK/nested INSERT…EXEC/streaming-OUTPUT
        // DML/bare FETCH would lose rows or corrupt state). Their presence anywhere in the body refuses
        // step-into → faithful native step-over (the engine performs the capture as one batch).
        if (captureInsert is not null
            && CaptureSafetyScanner.FindUncapturableStatement(procedureBody) is { } uncapturable)
        {
            messages.Add($"Cannot step into this INSERT … EXEC: '{nameText}' contains {uncapturable} — stepping over (C11, §11.7).");
            return null;
        }

        CaptureStagePlan? captureStage = null;
        if (captureInsert is not null)
        {
            // §11.7 (A65): derive the capture stage from the target (describe round-trip + gates).
            // A refusal steps the capture OVER — native performs it as one batch, faithful.
            var resolution = await ResolveCaptureStageAsync(
                captureInsert, caller, frames.PeekNextOrdinal(), cancellationToken).ConfigureAwait(false);
            if (resolution.RefusalReason is { } reason)
            {
                messages.Add($"Cannot step into this INSERT … EXEC: {reason} — stepping over (C11, §11.7).");
                return null;
            }

            captureStage = resolution.Plan;
        }

        var plan = new CalleePlan(
            nameText,
            blueprint.Definition,
            new ModuleIdentity(_options.Database, blueprint.Schema, blueprint.Name, IsScript: false),
            new SetOptionEnvironment(blueprint.QuotedIdentifier, blueprint.AnsiNulls),
            FrameKind.Procedure,
            procedureBody,
            ExtractCalleeParameters(parsed, blueprint.Definition),
            procedureEntity.Parameters,
            captureStage);

        return await PushCalleeFrameAsync(plan, caller, unit, specification, messages, cancellationToken)
            .ConfigureAwait(false);
    }

    // §11.3 step 3 (A58): the virtual stack's engine-nesting mirror. The frame-count bound is the
    // incumbent check (unchanged for procedure callees); the weighted bound is what makes a
    // dynamic frame's 2-level cost (fact 33e) count. Either tripping = the engine's 217.
    private static bool DepthLimitReached(FrameStack frames, int nestCost)
        => frames.Depth >= FrameStack.MaxDepth
           || frames.EngineNestLevel + nestCost > FrameStack.MaxDepth;

    private Task<StepOutcome?> RouteNestingLimitAsync(Interpreter.StatementUnit unit, List<string> messages)
    {
        var values = new ErrorContextValues(217, 16, 2, unit.Span.StartLine, null,
            $"Maximum stored procedure, function, trigger, or view nesting level exceeded (limit {FrameStack.MaxDepth}).");
        return PerformRouteAsync(values, unit, xactState: 1, terminalWhenUnhandled: false, messages)!;
    }

    // A58 (§11.6): `sp_executesql`, in any of the forms the engine resolves — bare, [bracketed],
    // sys.-qualified, master.sys.-qualified (all verified live). A one- or two-part name whose
    // qualifier is anything else is somebody else's procedure, not the native one.
    private static bool IsSpExecuteSqlName(string nameText)
    {
        var parts = nameText.Split('.');
        for (var i = 0; i < parts.Length; i++)
        {
            parts[i] = parts[i].Trim().Trim('[', ']', '"');
        }

        if (parts.Length == 0 || !string.Equals(parts[^1], "sp_executesql", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return parts.Length switch
        {
            1 => true,
            2 => IsAnyOf(parts[0], "sys", "dbo"),
            3 => IsAnyOf(parts[0], "master") && IsAnyOf(parts[1], "sys", "dbo"),
            _ => false,
        };

        static bool IsAnyOf(string value, params string[] candidates)
            => candidates.Any(c => string.Equals(value, c, StringComparison.OrdinalIgnoreCase));
    }

    // A58 (§11.6): sp_executesql's own formals are @statement and @parameters — the engine also
    // accepts the prefix abbreviations @stmt / @params (verified live). A named actual binds to one
    // of them only if that slot is still empty and no user argument has been seen yet, so a USER
    // parameter that happens to be called @params still binds as a user parameter, as it does
    // natively. Everything after the two is a user argument, positional or named.
    private async Task<StepOutcome?> StepIntoSpExecuteSqlAsync(
        Frame caller, Interpreter.StatementUnit unit, ExecuteSpecification specification,
        ExecutableProcedureReference procedureEntity, List<string> messages,
        CancellationToken cancellationToken)
    {
        ExecuteParameter? statementArg = null;
        ExecuteParameter? paramsArg = null;
        var userArgs = new List<ExecuteParameter>();

        foreach (var actual in procedureEntity.Parameters)
        {
            if (actual.Variable?.Name is { } named)
            {
                if (statementArg is null && userArgs.Count == 0 && IsPrefixOf(named, "@statement"))
                {
                    statementArg = actual;
                }
                else if (paramsArg is null && userArgs.Count == 0 && IsPrefixOf(named, "@parameters"))
                {
                    paramsArg = actual;
                }
                else
                {
                    userArgs.Add(actual);
                }
            }
            else if (statementArg is null)
            {
                statementArg = actual;
            }
            else if (paramsArg is null)
            {
                paramsArg = actual;
            }
            else
            {
                userArgs.Add(actual);
            }
        }

        if (statementArg?.ParameterValue is not ScalarExpression statementExpression)
        {
            return null;                                     // engine 201: @statement not supplied
        }

        return await StepIntoDynamicAsync(
            caller, unit, specification, new[] { statementExpression }, requireNVarchar: true,
            paramsArg?.ParameterValue, userArgs, "sp_executesql", messages, cancellationToken)
            .ConfigureAwait(false);

        static bool IsPrefixOf(string candidate, string formal)
            => candidate.Length > 1 && formal.StartsWith(candidate, StringComparison.OrdinalIgnoreCase);
    }

    // A58 (§11.6): build and push a DYNAMIC frame. The statement text and the parameter definition
    // are ordinary argument expressions — they evaluate at the call site through the §11.3 step-1
    // scalar-eval pipeline, so `N'SELECT * FROM ' + @tbl` is supported and a fault in them routes
    // through §10.3 with the EXEC SU as the location. The evaluated string becomes the frame's
    // FullScript, parsed with the CALLER's QUOTED_IDENTIFIER/ANSI_NULLS (fact 33a: a dynamic batch
    // inherits the session's; there is no sys.sql_modules row to read).
    private async Task<StepOutcome?> StepIntoDynamicAsync(
        Frame caller, Interpreter.StatementUnit unit, ExecuteSpecification specification,
        IEnumerable<ScalarExpression> statementParts, bool requireNVarchar,
        ScalarExpression? paramsExpression, IList<ExecuteParameter> actuals, string displayName,
        List<string> messages, CancellationToken cancellationToken)
    {
        // The provably-nvarchar gate (sp_executesql only): a varchar @statement is engine msg 214.
        // Stepping into it would let the DEBUGGER succeed where production fails — the debugger
        // would be hiding the bug — so refuse and let the engine raise its own 214.
        var text = new StringBuilder();
        foreach (var part in statementParts)
        {
            if (requireNVarchar && !IsProvablyNVarchar(part, caller))
            {
                messages.Add("Cannot step into sp_executesql: its statement argument is not provably nvarchar " +
                             "(the engine requires ntext/nchar/nvarchar) — stepping over (§11.6).");
                return null;
            }

            var (value, outcome) = await EvaluateStringArgumentAsync(caller, part, unit, messages, cancellationToken)
                .ConfigureAwait(false);
            if (outcome is not null)
            {
                return outcome;                              // the expression itself faulted (§10.3)
            }

            text.Append(value);
        }

        var dynamicText = text.ToString();
        if (string.IsNullOrWhiteSpace(dynamicText))
        {
            messages.Add($"Cannot step into {displayName}: the dynamic statement is empty — stepping over (§11.6).");
            return null;
        }

        IReadOnlyList<CalleeParameter> formals = Array.Empty<CalleeParameter>();
        if (paramsExpression is not null)
        {
            if (requireNVarchar && !IsProvablyNVarchar(paramsExpression, caller))
            {
                messages.Add("Cannot step into sp_executesql: its parameter-definition argument is not provably " +
                             "nvarchar (the engine requires ntext/nchar/nvarchar) — stepping over (§11.6).");
                return null;
            }

            var (paramsText, paramsOutcome) = await EvaluateStringArgumentAsync(
                caller, paramsExpression, unit, messages, cancellationToken).ConfigureAwait(false);
            if (paramsOutcome is not null)
            {
                return paramsOutcome;
            }

            if (!TryParseDynamicFormals(paramsText, out formals, out var formalsProblem))
            {
                messages.Add($"Cannot step into sp_executesql: {formalsProblem} — stepping over (§11.6).");
                return null;
            }
        }

        // Parse with the caller's parse settings (fact 33a). A parse failure, or a `GO` (natively
        // msg 102 — not a T-SQL statement, though ScriptDom would happily split it into batches),
        // is the A47 server-as-oracle case: step over and let the engine report it.
        var parsed = ScriptParser.Parse(dynamicText, caller.SetEnv.QuotedIdentifier, _compatLevel, out var parseErrors);
        if (parseErrors.Count > 0)
        {
            messages.Add($"Cannot step into {displayName}: the dynamic statement did not parse — stepping over, " +
                         "so the server reports its own error (§11.6).");
            return null;
        }

        IReadOnlyList<IList<TSqlStatement>> batches;
        try
        {
            batches = FrameBodyResolver.ResolveScriptBatches(parsed);
        }
        catch (NotSupportedException)
        {
            messages.Add($"Cannot step into {displayName}: the dynamic statement has no executable statements — stepping over.");
            return null;
        }

        if (batches.Count != 1)
        {
            messages.Add($"Cannot step into {displayName}: the dynamic statement contains a GO batch separator, " +
                         "which is a syntax error inside dynamic SQL — stepping over (§11.6).");
            return null;
        }

        var plan = new CalleePlan(
            displayName,
            dynamicText,
            ModuleIdentity.Dynamic(_options.Database, DynamicTextHash(dynamicText)),
            // A dynamic batch inherits the session's parse options (fact 33a), which for the
            // debugger means the caller frame's tracked SetEnv.
            new SetOptionEnvironment(caller.SetEnv.QuotedIdentifier, caller.SetEnv.AnsiNulls),
            // FrameKind.Script: `RETURN <value>` is illegal inside a dynamic batch (fact 33d, msg
            // 178) and FrameKind already encodes exactly that (fact 13). A body carrying one trips
            // the parse-time diagnostic in the shared push → step-over → the engine raises its 178.
            FrameKind.Script,
            batches[0],
            formals,
            actuals);

        return await PushCalleeFrameAsync(plan, caller, unit, specification, messages, cancellationToken)
            .ConfigureAwait(false);
    }

    // A58 (§11.6): is this argument provably nvarchar — i.e. would the engine accept it as
    // sp_executesql's @statement (msg 214 otherwise)?
    //
    // Only two shapes can ever reach here, and that is a T-SQL grammar rule, not an assumption:
    // an EXECUTE argument must be a **constant or a variable** — an expression is a syntax error
    // ("Incorrect syntax near '+'", verified live), so `EXEC sp_executesql N'…' + @tbl` never
    // runs natively at all. Anything else therefore falls through to `false` → step-over → the
    // engine raises its own syntax error. That fall-through is load-bearing, not laziness: were
    // this to accept a concatenation ScriptDom happened to parse, the debugger would cheerfully
    // execute a statement the server rejects. (Concatenation IS legal in `EXEC(@a + @b)` — the
    // engine's own `+ ...n` grammar — and arrives there as ExecutableStringList.Strings, which
    // §11.6 concatenates; EXEC() accepts varchar too, so this gate does not apply to it.)
    private static bool IsProvablyNVarchar(ScalarExpression expression, Frame frame) => expression switch
    {
        StringLiteral literal => literal.IsNational,
        // A59 (§8.1): the STORAGE type, not the declared one — `CREATE TYPE dbo.SqlText FROM
        // nvarchar(max)` IS national, and reading the declared name ('dbo.SqlText') would fail
        // the test and silently demote A58 step-into to step-over.
        VariableReference variable => frame.Variables.TryGet(variable.Name, out var slot)
                                      && IsNationalTypeSql(slot.Declaration.StorageType),
        _ => false,
    };

    // sysname IS nvarchar(128) — a very common way to hold a dynamic object name.
    private static bool IsNationalTypeSql(string typeSql)
    {
        var name = typeSql.TrimStart().TrimStart('[', '"');
        return name.StartsWith("nvarchar", StringComparison.OrdinalIgnoreCase)
               || name.StartsWith("nchar", StringComparison.OrdinalIgnoreCase)
               || name.StartsWith("ntext", StringComparison.OrdinalIgnoreCase)
               || name.StartsWith("sysname", StringComparison.OrdinalIgnoreCase);
    }

    // A58 (§11.6): evaluate one string-valued argument expression at the call site, through the
    // same rewritten/error-wrapped scalar-eval pipeline §11.3 step 1 uses for ordinary arguments.
    // A NULL evaluates to the empty string — natively sp_executesql treats a NULL statement as a
    // no-op, and the caller's empty-text guard turns that into a step-over.
    private async Task<(string Value, StepOutcome? Outcome)> EvaluateStringArgumentAsync(
        Frame caller, ScalarExpression expression, Interpreter.StatementUnit unit,
        List<string> messages, CancellationToken cancellationToken)
    {
        var batch = ComposedBatchBuilder.BuildForScalarEval(
            caller, _rewriteEngine!, _rewriteContext!, expression,
            caller.Cursor.Index.FullScript, _shadows!, DebuggeeComposition(caller));
        var run = await RunFaultableBatchAsync(batch, unit, cancellationToken).ConfigureAwait(false);
        if (run.Outcome is not null)
        {
            messages.AddRange(run.Messages);
            return (string.Empty, run.Outcome);
        }

        var value = ExtractScalarObject(run.UserSets, batch);
        return (value as string ?? string.Empty, null);
    }

    // C13 (F2): resolve a non-literal SET ROWCOUNT <expr> (a variable or expression) to the integer
    // it set the connection to, so the §11.2 tracker can re-apply the debuggee's real limit around
    // later statements. Same rewritten/error-wrapped scalar-eval pipeline as an argument; a fault or
    // NULL returns null, leaving the tracked value unchanged (best-effort, never crashes the step).
    private async Task<string?> ResolveRowCountValueAsync(
        Frame frame, ScalarExpression expression, Interpreter.StatementUnit unit, CancellationToken cancellationToken)
    {
        var batch = ComposedBatchBuilder.BuildForScalarEval(
            frame, _rewriteEngine!, _rewriteContext!, expression,
            frame.Cursor.Index.FullScript, _shadows!, DebuggeeComposition(frame));
        var run = await RunFaultableBatchAsync(batch, unit, cancellationToken).ConfigureAwait(false);
        if (run.Outcome is not null)
        {
            return null;
        }

        var value = ExtractScalarObject(run.UserSets, batch);
        return value is null or DBNull ? null : Convert.ToInt64(value).ToString();
    }

    // A58 (§11.6): parse sp_executesql's @parameters string (`N'@a int, @b varchar(10) OUTPUT'`)
    // into formals by wrapping it in a synthetic CREATE PROCEDURE shell and reading
    // ProcedureStatementBodyBase.Parameters — the exact shape ExtractCalleeParameters already
    // consumes, so §11.3's matching/seeding/OUTPUT machinery is reused verbatim. This is a
    // PARSE-only use of a generated string: §5.3's "never regenerate statements" rule governs
    // statements sent for EXECUTION, and the shell is never executed. The type/default source
    // slices are taken from the shell text, which is why it is passed as the fullScript.
    private bool TryParseDynamicFormals(
        string parametersText, out IReadOnlyList<CalleeParameter> formals, out string problem)
    {
        formals = Array.Empty<CalleeParameter>();
        problem = string.Empty;
        if (string.IsNullOrWhiteSpace(parametersText))
        {
            return true;                                     // no parameter definition = no formals
        }

        const string prefix = "CREATE PROCEDURE __dbg_dynamic_params (";
        const string suffix = ") AS BEGIN SET NOCOUNT ON; END";
        var shell = prefix + parametersText + suffix;

        var parsed = ScriptParser.Parse(shell, initialQuotedIdentifiers: true, _compatLevel, out var errors);
        if (errors.Count > 0)
        {
            problem = "its parameter definition did not parse";
            return false;
        }

        try
        {
            formals = ExtractCalleeParameters(parsed, shell);
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidCastException)
        {
            problem = "its parameter definition is not a plain parameter list";
            return false;
        }

        return true;
    }

    // A58 (§11.6): a dynamic frame's identity is a CONTENT hash of its text, so re-executing the
    // same string — a loop, a repeated call — re-binds to the same virtual document and keeps the
    // breakpoints the user set in it.
    private static string DynamicTextHash(string text)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes, 0, 8).ToLowerInvariant();
    }

    // §11.3 push, shared by procedure and dynamic (§11.6) callees: match the actuals to the
    // formals, evaluate the argument expressions left-to-right, build the frame, create and seed
    // its state table, and push. Returns null to fall back to step-over.
    private async Task<StepOutcome?> PushCalleeFrameAsync(
        CalleePlan plan, Frame caller, Interpreter.StatementUnit unit, ExecuteSpecification specification,
        List<string> messages, CancellationToken cancellationToken)
    {
        var frames = _frames!;
        var calleeParameters = plan.Formals;

        // A62 (§11.3 step 2 / C9): step-into a callee with a table-valued parameter is
        // SUPPORTED — the TVP formal is realized as a #temp like any table variable (A59) and
        // seeded from the caller's own table-type-variable realization (a #temp → #temp copy,
        // below). The pre-A62 blanket refusal keyed off the READONLY formal (READONLY is only
        // ever valid on a TVP) and stepped the whole callee over; it is gone.

        // ---- match actuals to formals (positional then named; ScriptDom preserves
        // ---- order). Any shape the engine itself would refuse (too many args, unknown
        // ---- named param, named-before-positional, OUTPUT to a non-OUTPUT formal,
        // ---- OUTPUT arg that isn't a plain variable, DEFAULT with no default, missing
        // ---- required param) falls back to step-over: the native error surfaces
        // ---- through the oracle exactly as the engine raises it.
        var actualByFormal = new Dictionary<string, ExecuteParameter>(StringComparer.OrdinalIgnoreCase);
        var sawNamed = false;
        for (var position = 0; position < plan.Actuals.Count; position++)
        {
            var actual = plan.Actuals[position];
            if (actual.Variable is { } formalName)
            {
                sawNamed = true;
                if (!calleeParameters.Any(p => string.Equals(p.Declaration.Name, formalName.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    return null;                             // engine 8145: not a parameter of the proc
                }

                actualByFormal[formalName.Name] = actual;
            }
            else
            {
                if (sawNamed || position >= calleeParameters.Count)
                {
                    return null;                             // engine 119 / 8144
                }

                actualByFormal[calleeParameters[position].Declaration.Name] = actual;
            }
        }

        var outputPairs = new List<OutputPair>();
        var evaluated = new List<(CalleeParameter Formal, object? Value)>();
        var defaultSeeds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // A62 (§11.3 step 2 / §9): TVP formals to seed AFTER the callee's own (empty)
        // realization is hoisted below — (caller's source table-type variable, callee formal name).
        var tvpSeeds = new List<(TableTypeVariable Source, string CalleeFormalName)>();
        foreach (var formal in calleeParameters)
        {
            var hasActual = actualByFormal.TryGetValue(formal.Declaration.Name, out var actual);

            // A62 (§11.3 step 2): a table-valued parameter. READONLY is only ever valid on a TVP,
            // so IsReadOnly is an exact discriminator. RegisterFrameVariablesAsync has already
            // classified this formal as a table type and will realize it as an empty #temp; here
            // we record where its rows come from. An OMITTED TVP is native-legal and means an
            // empty table — the realization is already empty, so there is nothing to copy.
            // READONLY is only ever valid on a table-valued parameter, BUT a malformed
            // `@x int READONLY` (which the engine itself rejects at the call, before the body
            // runs) would parse with IsReadOnly=true — so confirm the formal's type actually
            // resolves to a TABLE type before treating it as a TVP. If it does not, step over and
            // let the engine raise its own error rather than mis-seed (or crash on the missing
            // TableTypeVariables entry that RegisterFrameVariablesAsync would never create).
            if (formal.IsReadOnly
                && _userTypes.TryResolve(formal.Declaration.Fragment.DataType, out var formalType)
                && formalType.Kind == UserTypeKind.Table)
            {
                if (!hasActual || actual!.ParameterValue is DefaultLiteral)
                {
                    continue;                                // empty table variable (native-legal)
                }

                // Native accepts only a variable OF THE TYPE as a TVP actual — never OUTPUT, never
                // an expression. Anything else → step-over so the engine raises its own error.
                if (actual.IsOutput
                    || actual.ParameterValue is not VariableReference callerTableRef
                    || !caller.TableTypeVariables.TryGetValue(callerTableRef.Name, out var callerTvp))
                {
                    return null;
                }

                // A62 (F1/F4): the actual must be a variable of the SAME table type as the formal.
                // A DIFFERENT table type — even one whose columns happen to coincide — is a native
                // operand-type clash (msg 206) raised at the call, before the body runs. The seed
                // copies the FORMAL type's columns out of the caller realization, so a mismatch
                // either crashes the session (columns differ → "invalid column name") or silently
                // runs the callee over coerced rows (columns coincide → a hidden compile error).
                // Step over and let the engine report its own 206.
                if (!string.Equals(callerTvp.Type.Schema, formalType.Schema, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(callerTvp.Type.Name, formalType.Name, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                tvpSeeds.Add((callerTvp, formal.Declaration.Name));
                continue;
            }

            // A READONLY formal whose type does NOT resolve to a table type is a shape the engine
            // rejects at the call — fall through to a step-over via the "not supplied / bad shape"
            // handling below (or, if an actual was somehow supplied, the scalar path's own guards).
            if (formal.IsReadOnly)
            {
                return null;
            }

            // A62 (F2): a caller table-type variable can be consumed ONLY by a READONLY TVP formal
            // of its own type (handled by the branch above). Reaching here — a NON-READONLY formal
            // (a scalar, or a table-type formal that omits READONLY, itself native msg 352) supplied
            // a table-type variable — is a native error at the call (msg 206 / 352). Step over so the
            // engine raises it, rather than feeding a table variable into the scalar-eval pipeline
            // (BuildForScalarEval), which aborts with an unhandled error 137 and kills the session.
            if (hasActual
                && actual!.ParameterValue is VariableReference tableTypeActual
                && caller.TableTypeVariables.ContainsKey(tableTypeActual.Name))
            {
                return null;
            }

            if (!hasActual || actual!.ParameterValue is DefaultLiteral)
            {
                if (formal.Declaration.InitializerSql is not { } defaultLiteral)
                {
                    return null;                             // engine 201: parameter not supplied
                }

                defaultSeeds[formal.Declaration.Name] = defaultLiteral;   // defaults are constants (engine rule)
                continue;
            }

            if (actual.IsOutput)
            {
                if (!formal.IsOutput || actual.ParameterValue is not VariableReference callerVariable)
                {
                    return null;                             // engine 8162 / 179
                }

                if (!caller.Variables.TryGet(callerVariable.Name, out _))
                {
                    return null;                             // engine 137 natively
                }

                outputPairs.Add(new OutputPair(callerVariable.Name, formal.Declaration.Name));
            }

            // §11.3 step 1: argument expressions evaluate left-to-right through the
            // caller's scalar-eval pipeline (rewritten — R1–R7 apply — error-wrapped,
            // faultable with the EXEC SU as the location). OUTPUT args evaluate too:
            // OUTPUT is copy-in/copy-out (fact 23), so the callee starts from the
            // caller variable's current value. No shadow observation (same rule as
            // RETURN evals — the wrapper SELECT is not native truth).
            if (actual.ParameterValue is not ScalarExpression argumentExpression)
            {
                return null;
            }

            var batch = ComposedBatchBuilder.BuildForScalarEval(
                caller, _rewriteEngine!, _rewriteContext!, argumentExpression,
                caller.Cursor.Index.FullScript, _shadows!, DebuggeeComposition(caller));
            var run = await RunFaultableBatchAsync(batch, unit, cancellationToken).ConfigureAwait(false);
            if (run.Outcome is not null)
            {
                messages.AddRange(run.Messages);
                return run.Outcome;                          // arg eval faulted → §10.3 with the EXEC SU as location
            }

            evaluated.Add((formal, ExtractScalarObject(run.UserSets, batch)));
        }

        // ---- build the callee frame.
        ExecutionCursor calleeCursor;
        var calleeDeclarations = calleeParameters.Select(p => p.Declaration).ToList();
        try
        {
            calleeCursor = ExecutionCursor.Create(
                plan.Body, plan.Text, plan.FrameKind, calleeDeclarations.Select(d => d.Name));
        }
        catch (Exception ex) when (ex is MilestoneNotSupportedException or ParseTimeDiagnosticException)
        {
            // A58: this is also where a dynamic body carrying `RETURN <value>` lands — illegal
            // inside a dynamic batch (fact 33d) and caught by FrameKind.Script's control-flow
            // validation. Stepping over lets the engine raise its own msg 178.
            messages.Add($"Cannot step into '{plan.DisplayName}': {ex.Message} — stepping over.");
            return null;
        }

        var cursorSupport = await CheckCursorDefaultAsync(calleeCursor.Index, cancellationToken).ConfigureAwait(false);
        if (cursorSupport is not null)
        {
            messages.Add($"Cannot step into '{plan.DisplayName}': {cursorSupport} — stepping over.");
            return null;
        }

        // A62 (§11.3 step 2 / C9): a READONLY table-valued parameter cannot be written. Native
        // rejects a body that writes one with msg 10700 — a COMPILE error that aborts the whole
        // batch, so nothing runs. Stepping in would let the callee's earlier statements run before
        // the write failed — a divergence. Refuse (→ step-over) so the engine compile-fails the
        // batch as a whole. (The realization is a writable #temp, so nothing enforces it for us.)
        if (FindReadOnlyTvpWrite(calleeCursor, calleeParameters) is { } writtenTvp)
        {
            messages.Add($"Cannot step into '{plan.DisplayName}': it writes to READONLY table-valued " +
                         $"parameter '{writtenTvp}' (native msg 10700) — stepping over (C9, §11.3).");
            return null;
        }

        var callSite = new FrameCallSite(
            unit, outputPairs, specification.Variable?.Name,
            _runtimeOptions.Snapshot(), _shadows!.CaptureScopeIdentity());
        // §7.4/A26 (D1): native callee entry reads SCOPE_IDENTITY() = NULL (a new scope).
        // The caller's value is now saved in the call site (restored at pop); null the
        // shadow client-side so the callee's first R6 read serves NULL (F3-1b). The flag
        // stays CLEAR: the plain push-seed INSERT below (BuildSeedInsert, a parameter-free
        // batch) clobbers the SERVER chain to NULL too (fact 26d) — LOAD-BEARING, it
        // implements native callee-entry chain semantics. Parameterizing the seed would
        // carry the caller's chain into the callee (the rejected mirror bug — §1.2 item 1).
        _shadows!.RestoreScopeIdentity(null);
        var callee = new Frame(
            frames.NextOrdinal(),
            plan.Identity,
            calleeCursor,
            plan.SetEnv,
            callSite)
        {
            // Runtime options are connection-scoped: the callee enters with the
            // caller's tracked XACT_ABORT (§11.2); the F5 preamble asserts it per batch.
            XactAbortOn = caller.XactAbortOn,
            // C11 (A65): a stepped-into INSERT…EXEC callee BUFFERS its result stream in a per-frame
            // stage; the fields are set below once the stage table exists (server-side push).
            CaptureTargetSql = plan.CaptureStage?.StageInsertTarget,
            CaptureFlushSql = plan.CaptureStage?.FlushCoreSql,
            CaptureTargetHasIdentity = plan.CaptureStage?.TargetHasIdentity ?? false,
        };

        try
        {
            // A59 (§4 step 2a): same rule as a batch entry — a caller statement earlier in
            // THIS batch may have created the type this callee declares (`EXEC('CREATE TYPE …')`
            // then `EXEC dbo.UsesIt`), which natively compiles fine at call time.
            await RefreshUserTypesIfFrameDeclaresThemAsync(
                calleeDeclarations, calleeCursor.Index, cancellationToken).ConfigureAwait(false);
            await RegisterFrameVariablesAsync(callee, calleeDeclarations, cancellationToken).ConfigureAwait(false);
        }
        catch (DuplicateVariableException ex)
        {
            messages.Add($"Cannot step into '{plan.DisplayName}': {ex.Message} — stepping over.");
            return null;
        }
        catch (UnsupportedVariableTypeException ex)
        {
            // A59 (§8.2): a CLR-typed local in the callee body. Step over it — the engine
            // runs the module natively, exactly as it does for every other C8/C9 refusal.
            messages.Add($"Cannot step into '{plan.DisplayName}': {ex.Message} — stepping over.");
            return null;
        }

        // A58 (§11.6): retain the dynamic text's index for the life of the session, so its
        // read-only virtual document stays readable after the frame pops (VS Code keeps the editor
        // open, and breakpoints in it must still resolve). Content-keyed, so a repeat execution of
        // the same string reuses the same entry rather than accumulating duplicates.
        if (plan.Identity.IsDynamic)
        {
            _dynamicIndexes[plan.Identity.Name] = calleeCursor.Index;
        }

        // ---- server-side push (§11.3 step 2): state table + seed + evaluated args.
        // The push writes tempdb — resurrect the safety net first when detached.
        await EnsureTransactionProtectionAsync(unit, messages, cancellationToken).ConfigureAwait(false);
        await ExecuteAndTraceAsync(StateTableDdlBuilder.BuildCreateTable(callee.Ordinal, callee.Variables.All), cancellationToken).ConfigureAwait(false);

        // C11 (A65, §11.7): create this capture frame's stage table. It is SYNTHETIC — no debuggee
        // source references it, so it is deliberately NOT registered in the frame's TempObjects (R2
        // never resolves it, and the registry never reconciles it). Its lifetime is push → pop with
        // no debuggee ROLLBACK in between (CaptureSafetyScanner refuses transaction control in the
        // callee body), so PopFrameAsync drops it explicitly on both pop flavors (flush-then-drop on
        // a completed pop; discard on an abnormal pop, leaving the target untouched — I7). A doomed
        // force-rollback (fact 22) reaps it like any trancount ≥ 1 #temp; session teardown otherwise.
        if (plan.CaptureStage is { } stage)
        {
            await ExecuteAndTraceAsync(stage.StageCreateDdl, cancellationToken).ConfigureAwait(false);
        }

        await ExecuteAndTraceAsync(StateTableDdlBuilder.BuildSeedInsert(callee.Ordinal, callee.Variables.All, defaultSeeds), cancellationToken).ConfigureAwait(false);
        if (evaluated.Count > 0)
        {
            var slots = evaluated
                .Select(e => (Slot: FindSlot(callee, e.Formal.Declaration.Name), e.Value))
                .ToList();
            string ParameterName(VariableSlot v) => $"@__dbg{_nonce}_p{v.Ordinal}";
            var reseed = StateTableDdlBuilder.BuildReseedUpdate(
                callee.Ordinal, slots.Select(s => s.Slot).ToList(), ParameterName);
            var parameters = slots.Select(s => new BatchParameter(ParameterName(s.Slot), s.Value)).ToList();
            _trace.Event("batch.send", reseed);
            var reseedResult = await _executor.ExecuteAsync(reseed, parameters, cancellationToken).ConfigureAwait(false);
            TraceBatchResult(reseedResult);
        }

        // The callee's initial snapshot is its exact seeded state, read back once —
        // the D1 invariant (frames > 0 never have a null snapshot).
        if (callee.Variables.Count > 0)
        {
            var seededState = await ExecuteAndTraceAsync(StateTableDdlBuilder.BuildSelectAll(callee.Ordinal), cancellationToken).ConfigureAwait(false);
            callee.Snapshot = seededState.ResultSets is [{ Rows: [var seededRow, ..] }, ..]
                ? seededRow.ToArray()
                : new object?[callee.Variables.Count];
        }
        else
        {
            callee.Snapshot = Array.Empty<object?>();
        }

        // §9/D7: the callee's table-variable realizations hoist at push, inside the
        // safety transaction (created-at = the current trancount → a debuggee ROLLBACK
        // destroys them, healed empty per C25).
        await HoistTableVariableRealizationsAsync(callee, _lastObservedTrancount, cancellationToken).ConfigureAwait(false);

        // A62 (§11.3 step 2 / §9): seed each TVP formal's now-hoisted (empty) realization from the
        // caller's table-type-variable realization — a #temp → #temp copy, the v2 of A59's
        // #temp → DECLARE @t materialization. A TVP formal is READONLY, so there is nothing to
        // copy back at pop; the copy is one-way at push. IDENTITY values are regenerated (C28):
        // the copy excludes the identity column (InsertableColumns) and replays rows in identity
        // order so contiguous rows keep their values. Such an insert also moves the connection's
        // SCOPE_IDENTITY chain (fact 34h) — the same reason A59's preamble does — so we poison the
        // §7.4/A26 chain-sync flag; the next completed insert-family statement re-synchronizes.
        var tvpMovesIdentity = false;
        foreach (var (source, formalName) in tvpSeeds)
        {
            var target = callee.TableTypeVariables[formalName];
            var seed = ComposedBatchBuilder.BuildTvpFormalSeed(target, source.RealizationName);
            if (seed is not null)
            {
                await ExecuteAndTraceAsync(seed, cancellationToken).ConfigureAwait(false);
            }

            tvpMovesIdentity |= target.IdentityColumn is not null && target.InsertableColumns.Count > 0;
        }

        if (tvpMovesIdentity && !_scopeChainPoisoned)
        {
            _scopeChainPoisoned = true;
            _trace.Event("scopeid.poison", "tvp-formal seed moved the identity chain at push (§9/§11.3, fact 34h)");
        }

        frames.Push(callee);
        _trace.Event("frame.push",
            $"ordinal={callee.Ordinal} module={callee.Module.Display} depth={frames.Depth} outputPairs={outputPairs.Count} rcTarget={callSite.ReturnCodeVariable ?? "-"}");
        return new StepOutcome(StepDisposition.SteppedIn);
    }

    private static VariableSlot FindSlot(Frame frame, string variableName)
        => frame.Variables.TryGet(variableName, out var slot)
            ? slot
            : throw new InvalidOperationException($"Variable {variableName} not registered — push bookkeeping bug.");

    // A62 (§11.3 step 2 / C9): does the callee body WRITE a READONLY table-valued parameter — an
    // INSERT/UPDATE/DELETE/MERGE whose target is one of the TVP formals? Native rejects such a
    // body at COMPILE (msg 10700), so nothing runs; the debugger's realization is a writable
    // #temp and would silently accept the write, so we must refuse the step-in (→ step-over) and
    // let the engine compile-fail the whole batch. Index.All flattens nested control-flow blocks
    // (StatementIndex.Walk), so a write inside an IF/WHILE/TRY body is covered. Returns the
    // offending parameter name, or null when no TVP formal is written (the common fast path — no
    // READONLY formal means an empty name set and an immediate null).
    private static string? FindReadOnlyTvpWrite(ExecutionCursor cursor, IReadOnlyList<CalleeParameter> formals)
    {
        var tvpNames = formals
            .Where(p => p.IsReadOnly)
            .Select(p => p.Declaration.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (tvpNames.Count == 0)
        {
            return null;
        }

        foreach (var su in cursor.Index.All)
        {
            // F3: direct target, alias-resolved target, AND OUTPUT … INTO target — every write
            // shape the engine's msg 10700 covers (TryGetTargetVariableName caught only the first).
            foreach (var written in DmlTargetClassifier.GetWrittenTableVariableNames(su.Fragment))
            {
                if (tvpNames.Contains(written))
                {
                    return written;
                }
            }
        }

        return null;
    }

    // Predicate/scalar-eval single-value read, RAW (arg values must round-trip typed —
    // ExtractScalarColumn's Convert.ToInt32 is for predicates/return codes only).
    private static object? ExtractScalarObject(IReadOnlyList<ResultSet> userSets, Batches.ComposedBatch batch)
    {
        if (userSets is not [{ Rows: [var row, ..] }] || row.Count == 0)
        {
            throw new InvalidOperationException(
                $"Scalar-eval batch did not return the expected single-row 'p' result set (builder bug).\n{batch.Text}");
        }

        return row[0];
    }

    /// <summary>Parameters first, then body DECLAREs in source order (§8.2 hoisting,
    /// fact 14) — shared by frame 0 init and §11.3 pushes. Table-variable DECLAREs are
    /// not scalar variables (their realization is the §9 registry's, D7).</summary>
    // DESIGN §8.2 (A59): registration is where a user-defined type is finally pinned down.
    // `DECLARE @t dbo.X` says nothing about whether dbo.X is an alias scalar, a table type,
    // or an assembly type (fact 34) — only the §4 step-2a catalog does, and only the server
    // can hand back the alias's base type (fact 34d). So the split happens HERE, at frame
    // init, not at parse time:
    //   • table type    → NOT a scalar variable at all. No state-table column, no preamble
    //                     DECLARE; registered as a table variable and realized like one (§9).
    //   • alias type    → a scalar whose STORAGE type is the resolved base type (§8.1).
    //   • assembly type → refused by name (a launch refusal at frame 0; a step-into refusal,
    //                     hence a step-OVER, at a callee push) rather than the raw 2715 it
    //                     used to produce.
    //   • anything else → registered exactly as before A59.
    private async Task RegisterFrameVariablesAsync(
        Frame frame, IReadOnlyList<VariableDeclaration> parameters, CancellationToken cancellationToken)
    {
        var declarations = new List<VariableDeclaration>(parameters);
        foreach (var unit in frame.Cursor.Index.All)
        {
            if (unit.Kind != SuKind.Declare || unit.SubKind != SuSubKind.General)
            {
                continue;
            }

            declarations.AddRange(
                VariableDeclaration.Extract((DeclareVariableStatement)unit.Fragment, frame.Cursor.Index.FullScript));
        }

        // Classify once, then resolve every alias base type the frame needs in ONE describe
        // round trip (none at all for the overwhelmingly common all-system-types frame).
        var aliasTypes = new List<UserTypeEntry>();
        var aliasByDeclaration = new Dictionary<VariableDeclaration, UserTypeEntry>();
        var tableTypeDeclarations = new List<(VariableDeclaration Declaration, UserTypeEntry Type)>();

        foreach (var declaration in declarations)
        {
            if (!_userTypes.TryResolve(declaration.Fragment.DataType, out var entry))
            {
                continue;
            }

            switch (entry.Kind)
            {
                case UserTypeKind.Table:
                    tableTypeDeclarations.Add((declaration, entry));
                    break;

                case UserTypeKind.Assembly:
                    throw new UnsupportedVariableTypeException(declaration.Name, declaration.DataTypeSql,
                        "CLR (assembly) types have no literal form the debugger can store or re-seed");

                case UserTypeKind.Alias:
                    aliasTypes.Add(entry);
                    aliasByDeclaration[declaration] = entry;
                    break;
            }
        }

        await EnsureAliasBaseTypesAsync(aliasTypes, cancellationToken).ConfigureAwait(false);

        foreach (var declaration in declarations)
        {
            if (declaration.Fragment.DataType is SqlDataTypeReference { SqlDataTypeOption: SqlDataTypeOption.Cursor })
            {
                frame.CursorVariables.Add(declaration.Name);  // A63 (§9): a cursor variable is reified as a
                continue;                                     // GLOBAL cursor at its SET site, never scalar state;
                                                              // recorded so each batch DECLAREs it real (F1: 16950)
            }

            if (tableTypeDeclarations.Any(t => t.Declaration == declaration))
            {
                continue;                                  // §8.2: a table, not a scalar
            }

            if (aliasByDeclaration.TryGetValue(declaration, out var alias))
            {
                var resolved = _aliasBaseTypes[alias.QualifiedName];
                frame.Variables.Register(declaration with
                {
                    StorageTypeSql = resolved.TypeSql,
                    StorageCollation = resolved.Collation,
                });
            }
            else if (_databaseCollation is { Length: > 0 } dbCollation
                     && IsCharTypeNeedingCollation(declaration.Fragment.DataType))
            {
                // C14 (F1): a plain char SCALAR variable takes the DATABASE collation natively, but
                // its state-table column in tempdb would take tempdb's — the same transcode hazard
                // §8.1 fixes for alias base types (a `varchar` value would corrupt on the per-step
                // round trip on a code-page-different database). The storage type stays bare (the
                // CONVERT re-seed sites reject COLLATE); only the state COLUMN DDL gains it via
                // StorageCollation. A scalar DECLARE cannot carry its own COLLATE (a syntax error),
                // so the database default is always the right value.
                frame.Variables.Register(declaration with { StorageCollation = dbCollation });
            }
            else
            {
                frame.Variables.Register(declaration);
            }
        }

        foreach (var (declaration, entry) in tableTypeDeclarations)
        {
            // Mirrors the engine's own duplicate-name rule, which does not care whether the
            // clashing declarator is scalar or tabular (§8.2).
            if (frame.Variables.TryGet(declaration.Name, out _)
                || frame.TableTypeVariables.ContainsKey(declaration.Name))
            {
                throw new DuplicateVariableException(declaration.Name);
            }

            frame.TableTypeVariables.Add(declaration.Name, new TableTypeVariable
            {
                Name = declaration.Name,
                Type = entry,
                RealizationName = RewriteContext.TableVariablePhysicalName(declaration.Name, frame.Ordinal),
            });
        }
    }

    // A59 (§4 step 2a, corrected): the catalog is re-read at every point where a frame is
    // ABOUT TO RESOLVE a named type — and nowhere else.
    //
    // The first cut refreshed after an executed CREATE TYPE / DROP TYPE, reasoning that a type
    // created by dynamic SQL "is also not declarable by the surrounding script, which parsed
    // before it existed". That is FALSE, and the review caught it live: §5.4 parses each batch
    // at BATCH ENTRY, so
    //
    //     IF TYPE_ID('dbo.X') IS NULL EXEC('CREATE TYPE dbo.X FROM int');
    //     GO
    //     DECLARE @x dbo.X;                     -- stale catalog → the raw 2715 A59 exists to fix
    //
    // died exactly as it did before A59 — and CREATE TYPE cannot sit under an IF, so that
    // EXEC() is not an exotic shape, it is THE conditional-create idiom (these very fixtures
    // use it). A stepped-over proc that creates a type has the same hole, and a dynamic
    // DROP+CREATE redefinition would have kept a stale STRUCTURE behind a name that still
    // resolved (the A55 stale-module-cache lesson, repeated).
    //
    // Refreshing on use closes all three: it cannot go stale by construction, no matter who
    // created the type or how. The cost is zero for the overwhelmingly common frame that
    // declares no named type at all — which is the only reason the trigger is worth having.
    private async Task RefreshUserTypesIfFrameDeclaresThemAsync(
        IReadOnlyList<VariableDeclaration> parameters, StatementIndex index, CancellationToken cancellationToken)
    {
        if (!DeclaresNamedType(parameters, index))
        {
            return;
        }

        await RefreshUserTypeCatalogAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Does this frame name a type anywhere the catalog will be consulted — a scalar
    /// DECLARE/parameter (§8.2) or a column inside a `DECLARE @t TABLE(…)` (§9, fact 34c)?
    /// Purely syntactic: <c>UserDataTypeReference</c> is ScriptDom's "not a system type" node,
    /// so this never needs the catalog to decide whether to read the catalog.</summary>
    private static bool DeclaresNamedType(IReadOnlyList<VariableDeclaration> parameters, StatementIndex index)
    {
        foreach (var parameter in parameters)
        {
            if (parameter.Fragment.DataType is UserDataTypeReference)
            {
                return true;
            }
        }

        foreach (var unit in index.All)
        {
            switch (unit)
            {
                case { Kind: SuKind.Declare, SubKind: SuSubKind.General, Fragment: DeclareVariableStatement declare }:
                    if (declare.Declarations.Any(d => d.DataType is UserDataTypeReference))
                    {
                        return true;
                    }

                    break;

                case { SubKind: SuSubKind.TableVarDeclare, Fragment: DeclareTableVariableStatement table }:
                    if (table.Body?.Definition?.ColumnDefinitions.Any(c => c.DataType is UserDataTypeReference) == true)
                    {
                        return true;
                    }

                    break;
            }
        }

        return false;
    }

    private async Task RefreshUserTypeCatalogAsync(CancellationToken cancellationToken)
    {
        var result = await ExecuteAndTraceAsync(UserTypeCatalog.Query, cancellationToken).ConfigureAwait(false);
        _userTypes = UserTypeCatalog.FromResultSet(result.ResultSets.Count > 0 ? result.ResultSets[0] : null);
        _aliasBaseTypes.Clear();
        _tableTypeCache.Clear();
        _trace.Event("usertypes.refresh", $"count={_userTypes.Count}");
    }

    // A59 (§8.1): the server formats the base type — one describe call covers every alias
    // type not already cached (fact 34d). The cache is session-lived and invalidated only by
    // an executed CREATE/DROP TYPE, the only thing that can change the answer.
    private async Task EnsureAliasBaseTypesAsync(
        IReadOnlyList<UserTypeEntry> aliasTypes, CancellationToken cancellationToken)
    {
        var missing = aliasTypes
            .Where(t => !_aliasBaseTypes.ContainsKey(t.QualifiedName))
            .Distinct()
            .ToList();
        if (missing.Count == 0)
        {
            return;
        }

        var result = await ExecuteAndTraceAsync(
            UserTypeResolution.BuildAliasBaseTypeQuery(missing), cancellationToken).ConfigureAwait(false);
        var resolved = UserTypeResolution.ParseBaseTypes(result.ResultSets[0], missing.Count);
        for (var i = 0; i < missing.Count; i++)
        {
            _aliasBaseTypes[missing[i].QualifiedName] = resolved[i];
            _trace.Event("usertypes.alias",
                $"{missing[i].QualifiedName} -> {resolved[i].TypeSql} collation={resolved[i].Collation ?? "-"}");
        }
    }

    // A59 (§9): a table type's structure, rebuilt from the catalog (fact 34f) because — unlike
    // DECLARE @t TABLE(…) — it is nowhere in the source text. Cached per session.
    private async Task<TableTypeDefinition> GetTableTypeDefinitionAsync(
        UserTypeEntry tableType, CancellationToken cancellationToken)
    {
        if (_tableTypeCache.TryGetValue(tableType.QualifiedName, out var cached))
        {
            return cached;
        }

        var result = await ExecuteAndTraceAsync(
            UserTypeResolution.BuildTableTypeQuery(tableType), cancellationToken).ConfigureAwait(false);
        var definition = UserTypeResolution.ParseTableType(tableType, result.ResultSets);
        _tableTypeCache[tableType.QualifiedName] = definition;
        _trace.Event("usertypes.table", $"{tableType.QualifiedName} columns={definition.Columns.Count}");
        return definition;
    }

    // §9/R1 (D7): realizations are hoisted — created empty at frame init (frame 0:
    // BEFORE BEGIN TRAN, created-at 0, structurally rollback-proof like native table
    // variables) or at push (created-at = current trancount; healed per C25).
    private async Task HoistTableVariableRealizationsAsync(
        Frame frame, int createdAtTrancount, CancellationToken cancellationToken)
    {
        foreach (var unit in frame.Cursor.Index.All)
        {
            if (unit.SubKind != SuSubKind.TableVarDeclare)
            {
                continue;
            }

            var declare = (DeclareTableVariableStatement)unit.Fragment;
            var rawName = declare.Body?.VariableName?.Value
                ?? throw new InvalidOperationException("DECLARE TABLE without a variable name.");
            var name = rawName.StartsWith('@') ? rawName : "@" + rawName;
            var definition = declare.Body!.Definition
                ?? throw new InvalidOperationException($"DECLARE {name} TABLE without a definition.");
            var definitionSql = await ResolveTableDefinitionSqlAsync(
                definition, frame.Cursor.Index.FullScript, cancellationToken).ConfigureAwait(false);

            await RealizeTableVariableAsync(frame, name, definitionSql, createdAtTrancount, cancellationToken)
                .ConfigureAwait(false);
        }

        // A59 (§8.2/§9): the same realization for a `DECLARE @t dbo.MyTable`, whose shape comes
        // from the catalog rather than the source. Registration (§8.2) already kept it out of
        // the variable catalog, so from here on it IS a table variable in every respect.
        foreach (var (name, tableTypeVariable) in frame.TableTypeVariables)
        {
            var definition = await GetTableTypeDefinitionAsync(tableTypeVariable.Type, cancellationToken)
                .ConfigureAwait(false);
            tableTypeVariable.InsertableColumns = definition.InsertableColumns;
            tableTypeVariable.IdentityColumn = definition.IdentityColumn;
            await RealizeTableVariableAsync(
                frame, name, definition.BuildColumnDdl(), createdAtTrancount, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RealizeTableVariableAsync(
        Frame frame, string name, string definitionSql, int createdAtTrancount, CancellationToken cancellationToken)
    {
        var physical = RewriteContext.TableVariablePhysicalName(name, frame.Ordinal);
        var bracketed = RewriteContext.BracketIdentifier(physical);
        var createDdl = $"CREATE TABLE {bracketed} ({definitionSql});";
        await ExecuteAndTraceAsync(createDdl, cancellationToken).ConfigureAwait(false);
        frame.TempObjects.Add(new TempObjectEntry
        {
            OriginalName = name,
            PhysicalName = physical,
            Kind = TempObjectKind.TableVariable,
            CreatedAtTrancount = createdAtTrancount,
            RecreateDdl = $"IF OBJECT_ID('tempdb..{physical}') IS NULL {createDdl}",
            SurvivesBatchBoundary = false,       // M8 (§5.4): table variables die at GO (fact 2)
        });
    }

    // A59 (§9): `DECLARE @t TABLE(c dbo.Alias, …)` is legal natively (fact 34c) — a table
    // variable is declared in the current database's context. Its #temp realization is not:
    // tempdb cannot see the alias type (fact 34a, msg 2715). So the alias type references
    // INSIDE the definition are span-patched to their base types and everything else in the
    // slice is left byte-exact, exactly as §5.3 requires.
    private async Task<string> ResolveTableDefinitionSqlAsync(
        TableDefinition definition, string fullScript, CancellationToken cancellationToken)
    {
        var span = SourceSpan.Of(definition);

        // Alias-typed columns: base-resolved, because tempdb cannot see the DB-scoped alias type.
        var aliasColumns = new List<(ColumnDefinition Column, UserTypeEntry Type)>();
        foreach (var column in definition.ColumnDefinitions)
        {
            if (_userTypes.TryResolve(column.DataType, out var entry) && entry.Kind == UserTypeKind.Alias)
            {
                aliasColumns.Add((column, entry));
            }
        }

        await EnsureAliasBaseTypesAsync(aliasColumns.Select(c => c.Type).ToList(), cancellationToken)
            .ConfigureAwait(false);

        // Span patches over the byte-exact source slice (§5.3), all on distinct DataType spans:
        //   (1) alias types → base type (+ its own collation);
        //   (2) C14: an explicit COLLATE appended to every un-COLLATE'd char column, so the #temp
        //       realization keeps the table variable's native DATABASE collation rather than
        //       tempdb's (verified live: table-variable char columns inherit the DB collation,
        //       #temp columns tempdb's). Skipped when the DB collation is unknown (scripted tests).
        // Applied descending by offset so earlier edits do not shift later spans.
        var patches = new List<(SourceSpan Span, string Replacement)>();
        var aliasSet = new HashSet<ColumnDefinition>(aliasColumns.Select(c => c.Column));

        foreach (var (column, entry) in aliasColumns)
        {
            // A COLUMN definition takes the collation form (unlike a CONVERT target).
            var resolved = _aliasBaseTypes[entry.QualifiedName];
            var columnType = resolved.Collation is null
                ? resolved.TypeSql
                : $"{resolved.TypeSql} COLLATE {resolved.Collation}";
            patches.Add((SourceSpan.Of(column.DataType!), columnType));
        }

        if (_databaseCollation is { Length: > 0 } dbCollation)
        {
            foreach (var column in definition.ColumnDefinitions)
            {
                if (aliasSet.Contains(column) || column.Collation is not null
                    || !IsCharTypeNeedingCollation(column.DataType))
                {
                    continue;
                }

                var typeSpan = SourceSpan.Of(column.DataType!);
                var typeText = fullScript.Substring(typeSpan.StartOffset, typeSpan.Length);
                patches.Add((typeSpan, $"{typeText} COLLATE {dbCollation}"));
            }
        }

        var slice = new StringBuilder(fullScript.Substring(span.StartOffset, span.Length));
        foreach (var (patchSpan, replacement) in patches.OrderByDescending(p => p.Span.StartOffset))
        {
            slice.Remove(patchSpan.StartOffset - span.StartOffset, patchSpan.Length)
                 .Insert(patchSpan.StartOffset - span.StartOffset, replacement);
        }

        var definitionSql = slice.ToString().Trim();
        return definitionSql.StartsWith('(') && definitionSql.EndsWith(')')
            ? definitionSql[1..^1]
            : definitionSql;
    }

    // C14 (§9): the char types whose collation a table-variable column inherits from the database
    // but whose #temp realization would otherwise take from tempdb. `sysname` is deliberately
    // excluded (fixed nvarchar(128), rare as a table-variable column) — a documented residual.
    private static bool IsCharTypeNeedingCollation(DataTypeReference? dataType)
        => dataType is SqlDataTypeReference { SqlDataTypeOption: var opt }
           && opt is SqlDataTypeOption.Char or SqlDataTypeOption.VarChar or SqlDataTypeOption.Text
                  or SqlDataTypeOption.NChar or SqlDataTypeOption.NVarChar or SqlDataTypeOption.NText;

    // R3 (D7): a DECLARE CURSOR with neither LOCAL nor GLOBAL falls to the database's
    // CURSOR_DEFAULT; the rename model needs GLOBAL. Returns a refusal message or null.
    private async Task<string?> CheckCursorDefaultAsync(StatementIndex index, CancellationToken cancellationToken)
    {
        var hasUnspecified = false;
        foreach (var unit in index.All)
        {
            if (unit.SubKind == SuSubKind.CursorDeclare
                && unit.Fragment is DeclareCursorStatement { CursorDefinition.Options: var options }
                && !options.Any(o => o.OptionKind is CursorOptionKind.Local or CursorOptionKind.Global))
            {
                hasUnspecified = true;
                break;
            }
        }

        if (!hasUnspecified)
        {
            return null;
        }

        if (_cursorDefaultIsGlobal is null)
        {
            var result = await ExecuteAndTraceAsync(
                "SELECT CAST(is_local_cursor_default AS int) FROM sys.databases WHERE name = DB_NAME();",
                cancellationToken).ConfigureAwait(false);
            _cursorDefaultIsGlobal = result.ResultSets is [{ Rows: [[int isLocal, ..], ..] }, ..] && isLocal == 0;
        }

        return _cursorDefaultIsGlobal == true
            ? null
            : "a DECLARE CURSOR without LOCAL/GLOBAL relies on the database's CURSOR_DEFAULT, which is LOCAL here " +
              "— the debugger's cursor model needs GLOBAL (R3/§9); declare the cursor LOCAL explicitly or ALTER the " +
              "database's CURSOR_DEFAULT";
    }

    private async Task EnsureCursorDefaultIsGlobalAsync(StatementIndex index, CancellationToken cancellationToken)
    {
        var refusal = await CheckCursorDefaultAsync(index, cancellationToken).ConfigureAwait(false);
        if (refusal is not null)
        {
            throw new InvalidOperationException($"Launch refused: {refusal}.");
        }
    }

    // A20 (§7.4 R2): true iff a LIVE user-#temp entry in a frame OUTER to the creating
    // one (ordinal strictly below) claims this original name — R2's collision-only
    // rename predicate, evaluated identically at compose time (via FrameChainNameScope,
    // creating frame = the current frame) and at registry-record time (below, same
    // frame): the registry cannot change between the two for one SU. A live SAME-frame
    // #x is deliberately NOT a collision — the duplicate CREATE keeps its original name
    // so the server raises the native 2714 exactly as the native run would; renaming it
    // would make the duplicate silently succeed (M5 gate finding E1,
    // docs/archive/reviews/m5-gate-review-fable.md).
    private bool HasLiveOuterTempTable(string originalName, int creatingFrameOrdinal)
    {
        if (_frames is null)
        {
            return false;
        }

        for (var i = _frames.Depth - 1; i >= 0; i--)
        {
            var frame = _frames.All[i];
            if (frame.Ordinal >= creatingFrameOrdinal)
            {
                continue;
            }

            foreach (var entry in frame.TempObjects.All)
            {
                if (!entry.IsDead && entry.Kind == TempObjectKind.TempTable
                    && string.Equals(entry.OriginalName, originalName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        // M8 (§5.4/§6): the session-persistent tier is OUTER to any callee but at the SAME
        // (connection) level as the bottom batch frame — a callee create must rename around
        // a promoted session #temp, but the batch frame re-creating one keeps its original
        // name so the server raises native 2714 (M5 finding E1), like a same-frame
        // duplicate. So consult the session tier only when the creating frame is NOT the
        // bottom batch frame.
        if (creatingFrameOrdinal != _frames.All[0].Ordinal)
        {
            foreach (var entry in _sessionTempObjects.All)
            {
                if (!entry.IsDead && entry.Kind == TempObjectKind.TempTable
                    && string.Equals(entry.OriginalName, originalName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    // §9 registry effects of a successfully-executed unit. Created-at uses the
    // control-row truth the watchdog just observed for THIS batch.
    private void RecordRegistryEffects(Interpreter.StatementUnit unit, Frame frame)
    {
        switch (unit.SubKind)
        {
            case SuSubKind.TempTableDdl when unit.Fragment is CreateTableStatement { SchemaObjectName.BaseIdentifier.Value: { } tableName }
                                             && tableName.StartsWith('#') && !tableName.StartsWith("##"):
                // A20: minted rename only on collision — same predicate AND same
                // creating-frame ordinal the R2 rule used when this SU's batch was
                // composed (this frame was the current frame then); otherwise the
                // physical name IS the original (what a stepped-over callee's compiled
                // body sees).
                frame.TempObjects.Add(new TempObjectEntry
                {
                    OriginalName = tableName,
                    PhysicalName = HasLiveOuterTempTable(tableName, frame.Ordinal)
                        ? RewriteContext.TempTablePhysicalName(tableName, frame.Ordinal)
                        : tableName,
                    Kind = TempObjectKind.TempTable,
                    CreatedAtTrancount = _lastObservedTrancount,
                });
                break;

            case SuSubKind.CursorDeclare when unit.Fragment is DeclareCursorStatement { Name.Value: { } cursorName } cursorDeclare:
                // M8 (§5.4): a source-GLOBAL cursor survives GO (promoted at ExitBatch); a
                // source-LOCAL cursor (realized GLOBAL by the debugger) dies at GO
                // (deallocated at ExitBatch). An unspecified cursor reaches here only if
                // the DB CURSOR_DEFAULT is GLOBAL (EnsureCursorDefaultIsGlobalAsync refuses
                // launch otherwise), so "not explicitly LOCAL" == GLOBAL.
                var cursorIsGlobal = cursorDeclare.CursorDefinition?.Options is not { } cursorOptions
                    || !cursorOptions.Any(o => o.OptionKind == CursorOptionKind.Local);
                frame.TempObjects.Add(new TempObjectEntry
                {
                    OriginalName = cursorName,
                    PhysicalName = RewriteContext.CursorPhysicalName(cursorName, frame.Ordinal),
                    Kind = TempObjectKind.Cursor,
                    CreatedAtTrancount = _lastObservedTrancount,
                    SurvivesBatchBoundary = cursorIsGlobal,
                });
                break;

            case SuSubKind.CursorDeclare when unit.Fragment is SetVariableStatement { CursorDefinition: not null, Variable.Name: { } cursorVarName }:
                // A63 (§9): reify @c as a frame-unique GLOBAL cursor, created at THIS `SET @c = CURSOR …`
                // site (where native instantiates it). A cursor variable is batch-scoped (dies at GO like
                // any variable), so SurvivesBatchBoundary=false — ExitBatch deallocates it. A re-SET of a
                // still-live variable reuses the same deterministic physical name (the composed batch's
                // CURSOR_STATUS guard deallocates+recreates it), so a duplicate registry entry is skipped.
                if (!frame.TempObjects.All.Any(e =>
                        !e.IsDead && e.Kind == TempObjectKind.Cursor &&
                        string.Equals(e.OriginalName, cursorVarName, StringComparison.OrdinalIgnoreCase)))
                {
                    frame.TempObjects.Add(new TempObjectEntry
                    {
                        OriginalName = cursorVarName,
                        PhysicalName = RewriteContext.CursorVariablePhysicalName(cursorVarName, frame.Ordinal),
                        Kind = TempObjectKind.Cursor,
                        CreatedAtTrancount = _lastObservedTrancount,
                        SurvivesBatchBoundary = false,
                    });
                }
                break;

            case SuSubKind.CursorOp when unit.Fragment is DeallocateCursorStatement { Cursor.Name.Identifier.Value: { } deallocated }:
                MarkChainEntryDead(deallocated, TempObjectKind.Cursor);
                break;

            case SuSubKind.CursorOp when unit.Fragment is DeallocateCursorStatement { Cursor.Name.ValueExpression: VariableReference { Name: { } deallocatedVar } }:
                // A63: DEALLOCATE @c marks the reified cursor-variable entry dead (native semantics —
                // fact 24 rollback mark-dead already covers cursors; this is the explicit deallocation).
                MarkChainEntryDead(deallocatedVar, TempObjectKind.Cursor);
                break;

            default:
                if (unit.Fragment is DropTableStatement drop)
                {
                    foreach (var dropped in drop.Objects)
                    {
                        if (dropped.BaseIdentifier?.Value is { } droppedName && droppedName.StartsWith('#'))
                        {
                            MarkChainEntryDead(droppedName, TempObjectKind.TempTable);
                        }
                    }
                }
                else if (unit.Fragment is SelectStatement { Into.BaseIdentifier.Value: { } intoName }
                         && intoName.StartsWith('#') && !intoName.StartsWith("##"))
                {
                    // A24 (§7.4 R2 row, §9): SELECT ... INTO #x is a create site too —
                    // classification stays General (R1), but the registry entry (and
                    // the conditional mint) is the SAME shape as CREATE TABLE's.
                    frame.TempObjects.Add(new TempObjectEntry
                    {
                        OriginalName = intoName,
                        PhysicalName = HasLiveOuterTempTable(intoName, frame.Ordinal)
                            ? RewriteContext.TempTablePhysicalName(intoName, frame.Ordinal)
                            : intoName,
                        Kind = TempObjectKind.TempTable,
                        CreatedAtTrancount = _lastObservedTrancount,
                    });
                }

                break;
        }
    }

    private void MarkChainEntryDead(string originalName, TempObjectKind kind)
    {
        for (var i = _frames!.Depth - 1; i >= 0; i--)
        {
            var entry = _frames.All[i].TempObjects.TryResolve(originalName);
            if (entry is not null && entry.Kind == kind)
            {
                entry.MarkDead();
                return;
            }
        }
    }

    // A56: diagnostic annotation — routed to _diagnosticNotes (logLevel-gated), not the
    // debuggee `messages` stream.
    private void NoteUntrackedOptionsOnce()
    {
        if (_untrackedOptionNoted || _runtimeOptions.UntrackedOptionsSeen.Count == 0)
        {
            return;
        }

        _untrackedOptionNoted = true;
        _diagnosticNotes.Add(
            $"SET option(s) [{string.Join(", ", _runtimeOptions.UntrackedOptionsSeen)}] have no tracked baseline — " +
            "they will NOT be reverted at frame pops (§11.2; cosmetic residual, M4 design notes §7).");
    }

    // C5 (§7.2/§21, M7): one shared one-time note for either trigger — the session-init
    // @@OPTIONS probe (NOCOUNT was OFF at connect) or the debuggee's own first SET
    // NOCOUNT — since both describe the exact same underlying, cosmetic-only fact.
    private const string NoCountNoteText =
        "NOCOUNT is forced ON for every composed batch by the debugger (§7.1 preamble) — " +
        "a cosmetic-only difference from native execution; this session's own SET NOCOUNT " +
        "has no observable effect on debugger-observed row-count messages (C5).";

    // A56: diagnostic annotation — routed to _diagnosticNotes (logLevel-gated), not the
    // debuggee `messages` stream. Fires from either trigger (init @@OPTIONS probe or the
    // debuggee's first SET NOCOUNT); the one-time flag makes the source irrelevant.
    private void NoteNoCountOnce()
    {
        if (_noCountNoted)
        {
            return;
        }

        _noCountNoted = true;
        _diagnosticNotes.Add(NoCountNoteText);
    }

    // C2 (§21, M7): a cached, lazy sys.triggers lookup — one round trip per DML target
    // OBJECT name per session, one console note per object that has any trigger.
    // Best-effort: any executor-level failure (should be rare — OBJECT_ID resolving
    // NULL just makes the EXISTS probe read false) is treated as "no triggers" rather
    // than faulting an otherwise-successful DML step over a side, read-only probe.
    // A56: diagnostic annotation — routed to _diagnosticNotes (logLevel-gated), not the
    // debuggee `messages` stream.
    private async Task NoteDmlTriggersOnceAsync(string targetName, CancellationToken cancellationToken)
    {
        if (_dmlTriggerCache.ContainsKey(targetName))
        {
            return;
        }

        var hasTriggers = false;
        try
        {
            var literal = ShadowValues.SqlStringLiteral(targetName);
            var result = await ExecuteAndTraceAsync(
                $"SELECT CASE WHEN EXISTS (SELECT 1 FROM sys.triggers WHERE parent_id = OBJECT_ID({literal}) " +
                "AND parent_class = 1) THEN 1 ELSE 0 END AS has_triggers;",
                cancellationToken).ConfigureAwait(false);
            hasTriggers = result.ResultSets is [{ Rows: [var row, ..] }, ..] && Convert.ToInt32(row[0]) == 1;
        }
        catch (StatementExecutionException)
        {
            hasTriggers = false;
        }

        _dmlTriggerCache[targetName] = hasTriggers;
        if (hasTriggers)
        {
            _diagnosticNotes.Add(
                $"'{targetName}' has one or more triggers — DML against it executes atomically under the " +
                "debugger (C2); trigger-side effects are not independently steppable.");
        }
    }

    // D8/C25: dead table-variable realizations, healed empty in the doomed batch's
    // healthy prefix (user #temps deliberately excluded — their rollback loss is the
    // faithful C23 surface).
    private IReadOnlyList<string>? CollectHealingDdl()
    {
        List<string>? healing = null;
        foreach (var frame in _frames!.All)
        {
            foreach (var entry in frame.TempObjects.All)
            {
                if (entry.IsDead && entry.RecreateDdl is not null)
                {
                    (healing ??= new List<string>()).Add(entry.RecreateDdl);
                }
            }
        }

        return healing;
    }

    // §10.4 A9 step 2, M4 across frames: every frame reseeds from ITS snapshot (dead
    // frame>0 tables re-created first), registries reconcile, destroyed table-variable
    // realizations are healed empty (C25) — all at trancount 0, autocommitting.
    private async Task ReseedAllFramesAfterDetachAsync(int survivingTrancount, CancellationToken cancellationToken)
    {
        foreach (var frame in _frames!.All)
        {
            if (frame.Ordinal > 0)
            {
                await ExecuteAndTraceAsync(
                    StateTableDdlBuilder.BuildCreateIfMissing(frame.Ordinal, frame.Variables.All), cancellationToken).ConfigureAwait(false);
            }

            if (frame.Variables.Count > 0 && frame.Snapshot is not null)
            {
                string ParameterName(VariableSlot v) => $"@__dbg{_nonce}_p{v.Ordinal}";
                var reseed = StateTableDdlBuilder.BuildReseedUpdate(frame.Ordinal, frame.Variables.All, ParameterName);
                var parameters = frame.Variables.All
                    .Select(v => new BatchParameter(ParameterName(v), frame.Snapshot[v.Ordinal]))
                    .ToList();
                _trace.Event("batch.send", reseed);
                var result = await _executor.ExecuteAsync(reseed, parameters, cancellationToken).ConfigureAwait(false);
                TraceBatchResult(result);
            }

            frame.TempObjects.MarkDeadAbove(survivingTrancount);
            foreach (var entry in frame.TempObjects.All)
            {
                if (entry.IsDead && entry.RecreateDdl is not null)
                {
                    await ExecuteAndTraceAsync(entry.RecreateDdl, cancellationToken).ConfigureAwait(false);
                    entry.Revive();
                }
            }
        }

        // M8 §9 (lane 1b): reconcile the session-persistent tier the same way. A debuggee
        // ROLLBACK in a LATER batch destroys the connection-scoped #temps promoted from
        // earlier batches too (they were created at trancount ≥ 1), so mark them dead above
        // the surviving level — a subsequent reference then fails like native. Promoted
        // user #temps carry no RecreateDdl (their rollback loss is the faithful C23 surface,
        // exactly as for a frame's own user #temp), so the heal loop is a no-op for them;
        // it stays for symmetry in case a healable object (table-var realization) is ever
        // promoted (none is today — SurvivesBatchBoundary is false for those).
        _sessionTempObjects.MarkDeadAbove(survivingTrancount);
        foreach (var entry in _sessionTempObjects.All)
        {
            if (entry.IsDead && entry.RecreateDdl is not null)
            {
                await ExecuteAndTraceAsync(entry.RecreateDdl, cancellationToken).ConfigureAwait(false);
                entry.Revive();
            }
        }
    }

    // A55 (§5.4/A48): evict the session module cache after a script CREATE/ALTER/DROP of a
    // module. FetchModuleBlueprintAsync caches a module's definition (and, via
    // ResolveSourceMapMatch, its workspace-file match) for the session's whole life on the
    // M6 assumption that a module's server definition is STABLE — but a script that alters its
    // own modules (A47/A48) breaks that: a definition fetched BEFORE the DDL (breakpoint
    // mapping / virtual-doc resolution / an earlier step-into) would otherwise be served stale
    // forever. Cheap and rare, so clear the whole cache rather than name-match the DDL's target
    // (the cache key is call-site text, which need not match the DDL's own name spelling). Live
    // frames are unaffected — they carry their own cursor.Index built at push, and
    // TryGetModuleIndexAsync prefers a live frame over the cache.
    private void InvalidateModuleCaches()
    {
        _moduleCache.Clear();
        _sourceMapFiles.Clear();
    }

    private async Task<ModuleBlueprint?> FetchModuleBlueprintAsync(string nameText, CancellationToken cancellationToken)
    {
        if (_moduleCache.TryGetValue(nameText, out var cached))
        {
            return cached;
        }

        // Same shape as frame 0's resolution (§11.2: QI/ANSI_NULLS are baked at
        // CREATE time, fact 4b) — one round trip, cached by call-site text (§15;
        // recursion re-parses per activation but never re-fetches).
        var escaped = nameText.Replace("'", "''");
        var result = await ExecuteAndTraceAsync(
            $"SELECT OBJECT_DEFINITION(o.object_id) AS def, m.uses_quoted_identifier AS qi, " +
            $"m.uses_ansi_nulls AS ansi_nulls, OBJECT_SCHEMA_NAME(o.object_id) AS schema_name, o.name AS name " +
            $"FROM sys.objects o JOIN sys.sql_modules m ON m.object_id = o.object_id " +
            $"WHERE o.object_id = OBJECT_ID(N'{escaped}') AND o.type IN ('P');",
            cancellationToken).ConfigureAwait(false);

        if (result.ResultSets is not [{ Rows: [var row, ..] }, ..] || row[0] is not string definition)
        {
            return null;
        }

        var blueprint = new ModuleBlueprint(
            definition, (bool)row[1]!, (bool)row[2]!, row[3] as string, (string)row[4]!);
        _moduleCache[nameText] = blueprint;

        // M7 (§5.1/§5.2, sourceMap hash-compare): first need for THIS module's
        // identity — covers both TryStepIntoAsync's push path and
        // TryGetModuleIndexAsync's proactive (not-yet-live) resolution, since both
        // funnel through this one method.
        ResolveSourceMapMatch(new ModuleIdentity(_options.Database, blueprint.Schema, blueprint.Name, IsScript: false), definition, nameText);
        return blueprint;
    }

    // M7 (§5.1/§5.2, sourceMap hash-compare): module -> the one real workspace file
    // whose CRLF-normalized content byte-matched its server definition, first-need-
    // cached per identity for the session's whole life (mid-session file edits are
    // the user's own foot-gun, same as any native debugger over a stale symbol).
    private readonly Dictionary<ModuleIdentity, string> _sourceMapFiles = new();
    // One-shot "multiple candidates matched" warnings, keyed by the nameText the
    // fetch that discovered them used (FetchModuleBlueprintAsync's own cache key) —
    // drained into whichever messages channel the caller has (TryStepIntoAsync) or
    // LaunchWarnings (frame-0 procedure mode); see ResolveSourceMapMatch's remarks
    // on the narrow cases this can go un-drained (documented residual, not a bug).
    private readonly Dictionary<string, string> _sourceMapWarnings = new();

    /// <summary>M6 S3/M7: reverse lookup for the adapter's setBreakpoints/stack-frame
    /// Source resolution — does <paramref name="filePath"/> match an ALREADY-resolved
    /// module? Full-path + ordinal-ignore-case (Windows paths) comparison so a
    /// slightly different casing/slash style from the DAP client still matches.</summary>
    public bool TryResolveModuleBySourceFile(string filePath, out ModuleIdentity? identity)
    {
        var full = SafeFullPath(filePath);
        foreach (var (module, matchedFile) in _sourceMapFiles)
        {
            if (string.Equals(SafeFullPath(matchedFile), full, StringComparison.OrdinalIgnoreCase))
            {
                identity = module;
                return true;
            }
        }

        identity = null;
        return false;
    }

    /// <summary>M7: forward lookup for stack-frame Source construction — the file (if
    /// any) this module resolved to.</summary>
    public bool TryGetSourceMapFile(ModuleIdentity module, out string? file) => _sourceMapFiles.TryGetValue(module, out file);

    private static string SafeFullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException or IOException)
        {
            return path;
        }
    }

    // M7 (§5.1/§5.2): expands every configured sourceMap glob, compares each
    // candidate's CRLF->LF-normalized content against the server definition
    // (equally normalized) byte-for-byte — the most conservative reading of "a
    // normalized hash" (anything weaker risks binding breakpoints to lines that
    // aren't the server's; a miss just means the virtual doc, which is always
    // correct). First match wins; a second is a console warning naming both,
    // queued under noteKey for whichever caller can drain it into a real message
    // channel (documented residual: TryGetModuleIndexAsync's own direct callers —
    // e.g. a proactive setBreakpoints/tsqldbg_source resolution that never also
    // steps into the module — have no messages channel to drain into; the trace
    // event below always fires regardless, so nothing is silently lost, only the
    // live console note in that one narrow path).
    private void ResolveSourceMapMatch(ModuleIdentity identity, string serverDefinition, string noteKey)
    {
        if (_sourceMapFiles.ContainsKey(identity) || _options.SourceMapGlobsOrEmpty.Count == 0)
        {
            return;
        }

        var normalizedServer = NormalizeLineEndings(serverDefinition);
        var matches = new List<string>();
        foreach (var glob in _options.SourceMapGlobsOrEmpty)
        {
            foreach (var candidate in SourceMapResolver.ExpandGlob(glob))
            {
                string text;
                try
                {
                    text = File.ReadAllText(candidate);
                }
                catch (IOException)
                {
                    continue;
                }
                catch (UnauthorizedAccessException)
                {
                    continue;
                }

                if (NormalizeLineEndings(text) == normalizedServer
                    && !matches.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                {
                    matches.Add(candidate);
                }
            }
        }

        if (matches.Count == 0)
        {
            return;
        }

        _sourceMapFiles[identity] = matches[0];
        _trace.Event("sourcemap.match",
            $"module={identity.Display} file={matches[0]}" + (matches.Count > 1 ? $" (+{matches.Count - 1} more matches)" : ""));
        if (matches.Count > 1)
        {
            _sourceMapWarnings[noteKey] =
                $"'{identity.Display}' matched multiple sourceMap files byte-for-byte ({matches[0]} and {matches[1]}" +
                (matches.Count > 2 ? $", +{matches.Count - 2} more" : "") + ") — using the first (§5.2).";
        }
    }

    private static string NormalizeLineEndings(string text) => text.Replace("\r\n", "\n");

    private static List<CalleeParameter> ExtractCalleeParameters(TSqlFragment parsed, string fullScript)
    {
        var script = (TSqlScript)parsed;
        var body = script.Batches.SelectMany(b => b.Statements).OfType<ProcedureStatementBodyBase>().Single();

        var result = new List<CalleeParameter>(body.Parameters.Count);
        foreach (var p in body.Parameters)
        {
            var rawName = p.VariableName?.Value ?? throw new InvalidOperationException("Procedure parameter without a name.");
            var name = rawName.StartsWith('@') ? rawName : "@" + rawName;
            var typeSpan = SourceSpan.Of(p.DataType ?? throw new InvalidOperationException($"Parameter {name} has no data type."));
            var typeSql = fullScript.Substring(typeSpan.StartOffset, typeSpan.Length);

            string? defaultSql = null;
            if (p.Value is { } defaultExpression)
            {
                var span = SourceSpan.Of(defaultExpression);
                defaultSql = fullScript.Substring(span.StartOffset, span.Length);
            }

            result.Add(new CalleeParameter(
                new VariableDeclaration(name, typeSql, defaultSql, p),
                p.Modifier == ParameterModifier.Output,
                p.Modifier == ParameterModifier.ReadOnly));
        }

        return result;
    }

    private sealed record CalleeParameter(VariableDeclaration Declaration, bool IsOutput, bool IsReadOnly);

    private sealed record ModuleBlueprint(
        string Definition, bool QuotedIdentifier, bool AnsiNulls, string? Schema, string Name);

    /// <summary>§9: the frame-chain name scope R1–R3 resolve through — innermost match
    /// wins; dead entries are skipped so a destroyed callee object un-shadows the
    /// caller's (native name resolution after rollback loss), EXCEPT dead
    /// table-variable realizations while doomed, which the batch's own healthy prefix
    /// re-creates (D8/C25).</summary>
    private sealed class FrameChainNameScope : ITempNameScope
    {
        private readonly Session _session;

        public FrameChainNameScope(Session session) => _session = session;

        /// <summary>M5 I6/I7 (design note §5 item 7): overrides which frame's chain
        /// resolution starts from, for a debugger-initiated eval explicitly targeting
        /// a frame OTHER than the one currently executing (DAP frameId — REPL/watch
        /// can evaluate against any live frame, not just the top one). Null means "the
        /// top (currently executing) frame" — unchanged default for debuggee
        /// compositions and untargeted evals (breakpoint conditions). Session sets
        /// this immediately before composing a frame-targeted batch and clears it in a
        /// finally block right after, under the same single-threaded mutable-session-
        /// state contract already documented on ErrorContextActive.</summary>
        public int? EvaluationFrameOrdinal { get; set; }

        public int CurrentFrameOrdinal => _session.CurrentFrame.Ordinal;

        // A20 (§7.4 R2): the collision predicate — OUTER frames only, creating frame =
        // the CURRENT frame (the same ordinal the mint suffixes with). Shared with
        // RecordRegistryEffects' minting via Session.HasLiveOuterTempTable so the
        // rule's rename decision and the registry's recorded PhysicalName can never
        // disagree (nothing mutates the registry between composing an SU's batch and
        // recording its effects, and record time passes this same frame's ordinal).
        public bool HasLiveTempTable(string originalName)
            => _session.HasLiveOuterTempTable(originalName, _session.CurrentFrame.Ordinal);

        public string? ResolveReference(string originalName, TempObjectKind kind)
        {
            var frames = _session._frames;
            if (frames is null)
            {
                return null;
            }

            var startIndex = frames.Depth - 1;
            if (EvaluationFrameOrdinal is { } targetOrdinal)
            {
                startIndex = -1;
                for (var i = 0; i < frames.All.Count; i++)
                {
                    if (frames.All[i].Ordinal == targetOrdinal)
                    {
                        startIndex = i;
                        break;
                    }
                }
            }

            // A63/N5: EvaluationFrameOrdinal never names a live frame here in practice, but if it
            // ever named a popped one, startIndex stays -1 — the loop simply runs zero times and we
            // fall through to the session tier. (Was a `frames.All[-1]` throw hazard before N3's rewrite.)
            for (var i = startIndex; i >= 0; i--)
            {
                var isOuterFrame = i < startIndex;
                foreach (var entry in frames.All[i].TempObjects.All.Reverse())
                {
                    if (entry.Kind != kind
                        || !string.Equals(entry.OriginalName, originalName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    // A63 (F2 + N3/N6): a LOCAL or variable cursor (SurvivesBatchBoundary=false) is
                    // strictly FRAME-scoped — invisible from an OUTER frame. A callee's own unallocated
                    // `@c` must fault (16950), and a caller's LOCAL named cursor must be invisible to a
                    // stepped-into callee (native 16916), not silently resolved. Only a GLOBAL cursor (or
                    // a #temp / table variable) walks up the chain. This subsumes the old '@'-prefix
                    // heuristic (which also mis-scoped a delimited named cursor `[@x]` — N6).
                    if (isOuterFrame && entry.Kind == TempObjectKind.Cursor && !entry.SurvivesBatchBoundary)
                    {
                        continue;
                    }

                    if (!entry.IsDead)
                    {
                        // A14 (§10.4): while doomed, a live user-#temp entry created
                        // in-transaction names an object the fact-22 forced rollback
                        // destroyed with certainty — record the hit so the debuggee
                        // composition sites can pre-flight (C23's object-existence
                        // face). Gated so debugger-initiated evals never arm stops.
                        if (_session._captureDoomedTemps && _session._doomed
                            && kind == TempObjectKind.TempTable && entry.CreatedAtTrancount >= 1)
                        {
                            _session._doomedTempResolutions.Add(entry);
                        }

                        return entry.PhysicalName;
                    }

                    if (_session._doomed && entry.RecreateDdl is not null)
                    {
                        return entry.PhysicalName;           // healed by this batch's prefix
                    }
                }
            }

            // M8 (§5.4/§6): the session-persistent tier — connection-scoped objects (user
            // #temp/##global, GLOBAL cursors) promoted from prior GO batches, which survive
            // the boundary (Appendix C fact 32a; facts 1/3). Consulted AFTER the whole
            // frame chain, so a same-name object live in the current chain still wins. Only
            // SurvivesBatchBoundary objects are ever promoted here, so a LOCAL/variable cursor
            // (A63) is never present — no frame-local special-case is needed at this tier.
            foreach (var entry in _session._sessionTempObjects.All.Reverse())
            {
                if (!entry.IsDead && entry.Kind == kind
                    && string.Equals(entry.OriginalName, originalName, StringComparison.OrdinalIgnoreCase))
                {
                    return entry.PhysicalName;
                }
            }

            return null;
        }
    }

    private sealed record FrameZeroBlueprint(
        string SourceText,
        IList<TSqlStatement> Statements,
        ModuleIdentity Module,
        SetOptionEnvironment SetEnv,
        IReadOnlyList<VariableDeclaration> Parameters,
        IReadOnlyDictionary<string, string> SeedLiterals);

    // §5.4 (A43): the physical position of a materialized batch entry, for the frame
    // annotation only. PhysicalIndex/PhysicalCount count DISTINCT physical batches that run
    // (a `GO 0` batch is excluded); Iteration/Repeat are the 1-based `GO N` iteration and its
    // count (Repeat 1 = an ordinary, non-repeated batch).
    private sealed record BatchPosition(int PhysicalIndex, int PhysicalCount, int Iteration, int Repeat);

    // M8 (§5.4): resolve the batch blueprints. Procedure mode returns a single-element list
    // (the proc body); script mode parses the whole file, surfaces ScriptDom parse errors as
    // a launch failure, and returns one blueprint per batch ITERATION — §5.4/A43 materializes
    // a `GO N` batch N times so an iteration reuses the whole boundary machinery. All script
    // blueprints share the file text + ModuleIdentity.Script(); `_batchPositions` is set in
    // lockstep for the physical annotation.
    private async Task<IReadOnlyList<FrameZeroBlueprint>> ResolveBatchBlueprintsAsync(CancellationToken cancellationToken)
    {
        if (_options.Mode == LaunchMode.Procedure)
        {
            if (string.IsNullOrWhiteSpace(_options.Procedure))
            {
                throw new InvalidOperationException("SessionOptions.Procedure is required when Mode = Procedure.");
            }

            // DESIGN §11.2: proc-level QUOTED_IDENTIFIER/ANSI_NULLS are fixed at
            // CREATE/ALTER time (verified live, docs/engine-facts.md fact 4b) — fetched
            // in the same round trip as OBJECT_DEFINITION (§15: never add per-step
            // queries without a lazy/budgeted design).
            var escaped = _options.Procedure.Replace("'", "''");
            var defResult = await ExecuteAndTraceAsync(
                $"SELECT OBJECT_DEFINITION(o.object_id) AS def, m.uses_quoted_identifier AS qi, m.uses_ansi_nulls AS ansi_nulls " +
                $"FROM sys.objects o JOIN sys.sql_modules m ON m.object_id = o.object_id " +
                $"WHERE o.object_id = OBJECT_ID(N'{escaped}');",
                cancellationToken).ConfigureAwait(false);

            if (defResult.ResultSets is not [{ Rows: [var row, ..] }])
            {
                throw new InvalidOperationException(
                    $"OBJECT_DEFINITION returned NULL for '{_options.Procedure}' — module not found, encrypted, or " +
                    "VIEW DEFINITION permission is missing (DESIGN.md §16).");
            }

            var def = row[0] as string
                ?? throw new InvalidOperationException($"OBJECT_DEFINITION returned NULL for '{_options.Procedure}'.");
            var qi = (bool)row[1]!;
            var ansiNulls = (bool)row[2]!;

            var parsed = ScriptParser.Parse(def, qi, _compatLevel, out _);
            var procStatements = FrameBodyResolver.ResolveProcedureBody(parsed);
            var procParameters = ExtractProcedureParameters(parsed, def);
            var seeds = BuildSeedLiterals(procParameters);

            var (schema, name) = SplitTwoPart(_options.Procedure);
            var module = new ModuleIdentity(_options.Database, schema, name, IsScript: false);

            // M7 (§5.1/§5.2): frame 0's own sourceMap match, at its one blueprint
            // fetch — a multi-match warning here IS drainable (LaunchWarnings is
            // reliably forwarded to the console at launch, unlike the step-time
            // messages channel a callee's push uses).
            ResolveSourceMapMatch(module, def, _options.Procedure);
            if (_sourceMapWarnings.Remove(_options.Procedure, out var frameZeroSourceMapWarning))
            {
                _launchWarnings.Add(frameZeroSourceMapWarning);
            }

            _batchPositions = new[] { new BatchPosition(0, 1, 1, 1) };
            return new[] { new FrameZeroBlueprint(def, procStatements, module, new SetOptionEnvironment(qi, ansiNulls), procParameters, seeds) };
        }
        else
        {
            var scriptText = _options.ScriptText
                ?? throw new InvalidOperationException("SessionOptions.ScriptText is required when Mode = Script.");
            var setEnv = SetOptionEnvironment.Default;

            // §5.4 (A43): blank any `GO <n>` repeat count to equal-length spaces so ScriptDom
            // splits the file as PLAIN `GO` (it cannot parse the count — error 46010, fact
            // 32e), capturing (GO-offset, count) markers. Offsets stay byte-identical, so line
            // ground truth (§5.2) and original-source-slice execution (§5.3) are unchanged —
            // blueprints below slice the ORIGINAL scriptText, never the blanked copy.
            var parseText = ScriptParser.BlankGoRepeatCounts(
                scriptText, setEnv.QuotedIdentifier, _compatLevel, out var goMarkers);
            var parsed = ScriptParser.Parse(parseText, setEnv.QuotedIdentifier, _compatLevel, out var parseErrors);

            // M8 (§5.4): surface ScriptDom parse errors as a launch failure instead of
            // silently debugging a truncated best-effort AST. A blanked `GO N` no longer
            // errors; a MALFORMED count (`GO -1` → `Minus`, `GO 1.5` → `Numeric`) is not an
            // Integer token, stays unblanked, and is refused here — matching sqlcmd, which
            // hard-errors the same input (fact 32e / A43).
            if (parseErrors.Count > 0)
            {
                // §5.4/§20.3 (A47): ScriptDom only ever reports a terse GENERIC 46010
                // ("Incorrect syntax near 'X'") — it cannot produce the native, actionable
                // message (e.g. 111, "'CREATE/ALTER PROCEDURE' must be the first statement
                // in a query batch"). The live server can, so ask it (SET PARSEONLY ON) and
                // lead with its real diagnostic; fall back to ScriptDom's own message when
                // the server finds nothing the parser objected to (a malformed `GO` the
                // tokenizer split away) or the probe fails.
                var native = await TryGetNativeParseDiagnosticAsync(scriptText, cancellationToken).ConfigureAwait(false);
                if (native is not null)
                {
                    throw new InvalidOperationException(
                        $"The script cannot be debugged — {native} (§5.4: fix the error to debug this script).");
                }

                var detail = string.Join("; ", parseErrors.Select(e => $"line {e.Line}: {e.Message} (error {e.Number})"));
                throw new InvalidOperationException(
                    $"The script cannot be debugged — {parseErrors.Count} ScriptDom parse error(s): {detail} " +
                    "(§5.4: a malformed `GO` count or a syntax error; fix it to debug this script).");
            }

            // §5.4 (A43): map each count to its batch by offset, then MATERIALIZE one blueprint
            // per iteration. An iteration is a fresh `GO` batch (scope resets; #temp/SET/tran
            // persist; a batch-terminal fault advances to the next; a doomed tran is force-rolled
            // — all verified native, docs/archive/reviews/go-n-repeat-count-opus.md §2), so materializing
            // reuses the entire M8 boundary machinery UNCHANGED. `_batchPositions` records the
            // physical (batch k/N, iteration i/M) orientation for the annotation only.
            var physicalBatches = FrameBodyResolver.ResolveScriptBatchesWithRepeat(parsed, goMarkers);
            foreach (var pb in physicalBatches)
            {
                if (pb.Repeat > MaxGoRepeat)
                {
                    throw new InvalidOperationException(
                        $"A `GO` repeat count of {pb.Repeat} exceeds the step debugger's limit of {MaxGoRepeat} " +
                        "(§5.4/A43) — run the script natively for larger counts.");
                }
            }

            var module = ModuleIdentity.Script();
            var totalPhysical = physicalBatches.Count;
            var blueprints = new List<FrameZeroBlueprint>();
            var positions = new List<BatchPosition>();
            for (var p = 0; p < physicalBatches.Count; p++)
            {
                var pb = physicalBatches[p];
                for (var iteration = 1; iteration <= pb.Repeat; iteration++)
                {
                    blueprints.Add(new FrameZeroBlueprint(
                        scriptText, pb.Statements, module, setEnv,
                        Array.Empty<VariableDeclaration>(), new Dictionary<string, string>()));
                    positions.Add(new BatchPosition(p, totalPhysical, iteration, pb.Repeat));
                }
            }

            _batchPositions = positions;
            return blueprints;
        }
    }

    // DESIGN §5.4 / §20.3 (A47): the server is the ORACLE for a script parse/compile
    // diagnostic. When ScriptDom refuses a script, ask the live connection — already open
    // here (the NOCOUNT probe ran) and BEFORE BEGIN TRANSACTION (§4 step 5 is later) — for
    // its OWN verdict under SET PARSEONLY ON: a side-effect-free parse-and-report, no
    // compile, no execution, no state change. GO is a client separator the server does not
    // know, so each GO batch is sent on its own; the first the server rejects IS the native
    // diagnostic (e.g. 111 CREATE-PROC-not-first, 156 syntax), its line mapped back to the
    // whole script. Returns null when the server finds nothing the ScriptDom parser objected
    // to (a malformed `GO 1.5` the tokenizer split away, or a construct ScriptDom is merely
    // stricter about) or the probe itself fails — the caller then falls back to ScriptDom's
    // own message. Strictly a refinement of the diagnostic text: never worse than before.
    private async Task<string?> TryGetNativeParseDiagnosticAsync(string scriptText, CancellationToken cancellationToken)
    {
        IReadOnlyList<OracleBatchSegment> segments;
        try
        {
            segments = ScriptParser.SplitOnGoSeparators(
                scriptText, SetOptionEnvironment.Default.QuotedIdentifier, _compatLevel);
        }
        catch
        {
            return null;   // tokenizer hiccup — fall back to ScriptDom's message
        }

        if (segments.Count == 0)
        {
            return null;
        }

        try
        {
            await ExecuteAndTraceAsync("SET PARSEONLY ON;", cancellationToken).ConfigureAwait(false);
            try
            {
                foreach (var segment in segments)
                {
                    try
                    {
                        await ExecuteAndTraceAsync(segment.Text, cancellationToken).ConfigureAwait(false);
                    }
                    catch (StatementExecutionException ex) when (ex.Number > 0)
                    {
                        var line = segment.StartLine + Math.Max(ex.LineNumber, 1) - 1;
                        return $"Msg {ex.Number}, Line {line}: {ex.Message}";
                    }
                }
            }
            finally
            {
                // Restore the connection (launch is aborting regardless — defensive).
                try
                {
                    await ExecuteAndTraceAsync("SET PARSEONLY OFF;", cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // The connection is being torn down anyway; nothing to salvage.
                }
            }
        }
        catch
        {
            return null;   // transport failure on the probe — fall back to ScriptDom's message
        }

        return null;   // server parsed every batch cleanly — ScriptDom was stricter; use its message
    }

    // DESIGN §11.3-style default application, scoped to frame 0's own parameters:
    // a supplied launch arg seeds the INSERT literal directly; an omitted parameter
    // with a declared default becomes a synthetic initializer (run once, before the
    // step loop, via the same pipeline as a DECLARE initializer); an omitted
    // parameter with no default is a clear launch-time error rather than a silent NULL.
    private IReadOnlyList<VariableDeclaration> ExtractProcedureParameters(TSqlFragment parsed, string fullScript)
    {
        var script = (TSqlScript)parsed;
        var body = script.Batches.SelectMany(b => b.Statements).OfType<ProcedureStatementBodyBase>().Single();

        var result = new List<VariableDeclaration>(body.Parameters.Count);
        foreach (var p in body.Parameters)
        {
            var rawName = p.VariableName?.Value ?? throw new InvalidOperationException("Procedure parameter without a name.");
            var name = rawName.StartsWith('@') ? rawName : "@" + rawName;
            var typeSpan = SourceSpan.Of(p.DataType ?? throw new InvalidOperationException($"Parameter {name} has no data type."));
            var typeSql = fullScript.Substring(typeSpan.StartOffset, typeSpan.Length);

            var hasArg = _options.Args is not null && _options.Args.ContainsKey(name);
            string? initializerSql = null;

            // A59 (§9): a table-valued parameter has no literal form, so launch `args` cannot
            // carry one and its absence is not an error. Procedure mode starts it EMPTY — the
            // realization (§8.2) is created with the type's structure and no rows — and says so.
            // A TVP formal is READONLY, so the body cannot write it either way.
            if (_userTypes.TryResolve(p.DataType, out var entry) && entry.Kind == UserTypeKind.Table)
            {
                _launchWarnings.Add(
                    $"{name} is a table-valued parameter of {_options.Procedure}: it starts EMPTY (launch " +
                    "'args' cannot carry rows). Add rows from the Debug Console, or debug a script that " +
                    "fills the variable and EXECs the procedure.");
            }
            else if (!hasArg && p.Value is { } defaultExpr)
            {
                var s = SourceSpan.Of(defaultExpr);
                initializerSql = fullScript.Substring(s.StartOffset, s.Length);
            }
            else if (!hasArg)
            {
                throw new InvalidOperationException(
                    $"Parameter {name} of {_options.Procedure} was not supplied in launch config 'args' and has no default.");
            }

            result.Add(new VariableDeclaration(name, typeSql, initializerSql, p));
        }

        return result;
    }

    private IReadOnlyDictionary<string, string> BuildSeedLiterals(IReadOnlyList<VariableDeclaration> parameters)
    {
        var seeds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (_options.Args is null)
        {
            return seeds;
        }

        foreach (var decl in parameters)
        {
            if (_options.Args.TryGetValue(decl.Name, out var literal))
            {
                seeds[decl.Name] = literal;
            }
        }

        return seeds;
    }

    private static (string? Schema, string Name) SplitTwoPart(string procedureName)
    {
        var parts = procedureName.Split('.');
        return parts.Length >= 2 ? (parts[^2], parts[^1]) : (null, parts[0]);
    }

    private async Task<BatchResult> ExecuteAndTraceAsync(string batchText, CancellationToken cancellationToken)
    {
        _trace.Event("batch.send", batchText);
        var result = await _executor.ExecuteAsync(batchText, cancellationToken).ConfigureAwait(false);
        TraceBatchResult(result);
        return result;
    }

    // M5 I8: setVariable's parameterized UPDATE (§8.3) — the one raw-text-plus-
    // parameters shape outside the §7.1 composed-batch template.
    private async Task<BatchResult> ExecuteAndTraceAsync(
        string batchText, IReadOnlyList<BatchParameter> parameters, CancellationToken cancellationToken)
    {
        _trace.Event("batch.send", batchText);
        var result = await _executor.ExecuteAsync(batchText, parameters, cancellationToken).ConfigureAwait(false);
        TraceBatchResult(result);
        return result;
    }
}
