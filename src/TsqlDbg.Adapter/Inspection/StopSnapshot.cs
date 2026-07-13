using System.Collections.Concurrent;
using TsqlDbg.Core.Interpreter;
using TsqlDbg.Core.State;

namespace TsqlDbg.Adapter.Inspection;

// M5 I1 (design note §2, docs/archive/reviews/m5-inspection-design-notes-fable.md; A15,
// DESIGN §3): the immutable, epoch-keyed snapshot published at every stop, BEFORE
// `stopped` is emitted. Eager fields are all client-side state built with zero
// round trips; the per-frame variable value cache is the one fill-once slot the
// inspection executor (I2) populates lazily on the first `variables` request for
// (epoch, frame) — repeat requests in the same epoch are cache hits.
//
// M5 I4 (§12.2 Temp Tables scope) extends this with a second fill-once slot: a
// display-value cache keyed by PHYSICAL name (rowcount / cursor status text), plus
// a stable variablesReference mint for each table's "rows" children — the fixed
// 1000/2000/3000/4000 per-frame bases (I1/I3/I4) can't cover an unbounded number of
// live temp objects, so those references are minted dynamically per snapshot and
// resolved back through this same object (never a new "gate/cache" concept, I1's
// "variablesReference ids are minted per epoch" carried through literally).
public sealed class StopSnapshot
{
    private readonly ConcurrentDictionary<int, StateSnapshot> _valueCache = new();
    private readonly ConcurrentDictionary<string, string> _tempValueCache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _rowsReferenceByPhysicalName = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<int, string> _rowsReferenceTargets = new();
    private readonly ConcurrentDictionary<int, (IReadOnlyList<string> Columns, IReadOnlyList<object?> Values)> _rowDetailsByReference = new();
    private readonly ConcurrentDictionary<int, SystemScopeValues> _systemScopeOverrides = new();
    private int _nextRowsReference = 500_000; // above every fixed per-frame base (1000/2000/3000/4000, MaxDepth=32 wide)

    public StopSnapshot(int epoch, IReadOnlyList<SnapshotFrame> frames, ErrorContextDisplay? activeErrorContext)
    {
        Epoch = epoch;
        Frames = frames;
        ActiveErrorContext = activeErrorContext;
    }

    public int Epoch { get; }

    /// <summary>Bottom (root) → top, matching <c>Session.Frames</c> — what `stackTrace`/
    /// `scopes` serve today, plus the per-frame variable catalog (names/declared types),
    /// the System scope's eager values (I3), and the chain-resolved Temp Tables
    /// projection (I4). Never the LAZY display values themselves.</summary>
    public IReadOnlyList<SnapshotFrame> Frames { get; }

    /// <summary>Client-side display projection of the active error context (§10.6/§12.2),
    /// captured eagerly from <c>Session.ActiveErrorContext</c> at stop time; null outside
    /// any CATCH.</summary>
    public ErrorContextDisplay? ActiveErrorContext { get; }

    public static StopSnapshot Empty(int epoch) => new(epoch, Array.Empty<SnapshotFrame>(), null);

    /// <summary>I1's fill-once slot: true only once the inspection executor has cached a
    /// value read for this frame under THIS snapshot's epoch.</summary>
    public bool TryGetCachedValues(int frameOrdinal, out StateSnapshot values) => _valueCache.TryGetValue(frameOrdinal, out values!);

    public void CacheValues(int frameOrdinal, StateSnapshot values) => _valueCache.TryAdd(frameOrdinal, values);

    /// <summary>M5 I8: after a successful setVariable, the frame's cached Locals
    /// values are stale — drop them so the NEXT variables request re-fills fresh
    /// (still lazy: only if the client actually asks again).</summary>
    public void InvalidateValues(int frameOrdinal) => _valueCache.TryRemove(frameOrdinal, out _);

    /// <summary>M6 R2 (design note §5-R2, A25): the effective System scope for a
    /// frame — the republished override when one exists, else the eager value baked
    /// in at BuildSnapshot time. Callers never read <see cref="SnapshotFrame.SystemScope"/>
    /// directly for display (only this).</summary>
    public SystemScopeValues GetSystemScope(SnapshotFrame frame) =>
        _systemScopeOverrides.TryGetValue(frame.Ordinal, out var overridden) ? overridden : frame.SystemScope;

    /// <summary>M6 R2: republishes ONE frame's System scope IN PLACE — same snapshot
    /// object, same epoch, same variablesReference space (I1's contract is
    /// untouched: outstanding references stay valid). Called after any REPL
    /// evaluation whose trailing probe (or other inspection-fed observation) moved
    /// trancount/xact_state/session-mode relative to what was published.</summary>
    public void RepublishSystemScope(int frameOrdinal, SystemScopeValues values) => _systemScopeOverrides[frameOrdinal] = values;

    /// <summary>I4's fill-once slot for a Temp Tables entry's rendered value (rowcount
    /// or cursor status text), keyed by physical name (already frame-unique by
    /// construction — R1/R2/R3 all embed the frame ordinal).</summary>
    public bool TryGetCachedTempValue(string physicalName, out string value) => _tempValueCache.TryGetValue(physicalName, out value!);

    public void CacheTempValue(string physicalName, string value) => _tempValueCache.TryAdd(physicalName, value);

    /// <summary>The stable variablesReference for a table's "rows" children within
    /// THIS snapshot — minted once per physical name, reused on repeat scope expands.</summary>
    public int GetOrMintRowsReference(string physicalName)
    {
        return _rowsReferenceByPhysicalName.GetOrAdd(physicalName, name =>
        {
            var reference = Interlocked.Increment(ref _nextRowsReference);
            _rowsReferenceTargets[reference] = name;
            return reference;
        });
    }

    public bool TryResolveRowsReference(int reference, out string physicalName) => _rowsReferenceTargets.TryGetValue(reference, out physicalName!);

    /// <summary>§12.2 "each row → columns": one already-fetched row's cells behind a
    /// freshly minted reference, so expanding a row is a pure client-side read (the
    /// row was already fetched by the page query — no second round trip).</summary>
    public int MintRowDetailReference(IReadOnlyList<string> columns, IReadOnlyList<object?> values)
    {
        var reference = Interlocked.Increment(ref _nextRowsReference);
        _rowDetailsByReference[reference] = (columns, values);
        return reference;
    }

    public bool TryResolveRowDetail(int reference, out (IReadOnlyList<string> Columns, IReadOnlyList<object?> Values) detail)
        => _rowDetailsByReference.TryGetValue(reference, out detail);

    // ---- M5 I7 (§12.4 watch budget) — a per-EPOCH stopwatch: "starts when the first
    // watch of that stop begins executing." Lives here (not the executor) because the
    // budget is a per-stop concept, exactly like everything else this object owns.
    private readonly object _watchBudgetLock = new();
    private System.Diagnostics.Stopwatch? _watchStopwatch;
    private readonly HashSet<string> _watchOverflowExpressions = new(StringComparer.Ordinal);

    /// <summary>True if this expression already overflowed the budget THIS epoch — a
    /// repeat request for it is explicit click-to-evaluate (§12.4).</summary>
    public bool HasWatchOverflowed(string expression)
    {
        lock (_watchBudgetLock)
        {
            return _watchOverflowExpressions.Contains(expression);
        }
    }

    /// <summary>Call at the START of each watch's turn (sequential — one executor
    /// lane makes this race-free). Starts the stopwatch on first call. Returns true
    /// if still within budget (proceed with the real evaluation); false if the
    /// budget already elapsed — the caller renders "⏱" WITHOUT touching the
    /// connection, and this expression is marked overflowed for the rest of the
    /// epoch (so the NEXT request for it is recognized as click-to-evaluate).</summary>
    public bool TryBeginWatchTurn(string expression, int watchBudgetMs)
    {
        lock (_watchBudgetLock)
        {
            _watchStopwatch ??= System.Diagnostics.Stopwatch.StartNew();
            if (_watchStopwatch.ElapsedMilliseconds < watchBudgetMs)
            {
                return true;
            }

            _watchOverflowExpressions.Add(expression);
            return false;
        }
    }
}

/// <summary>One frame's eager, client-side-only projection (§12.1/§12.2). <see
/// cref="Module"/> is M6 (§13/A22): the stackTrace Source builder's input — script-mode
/// frame 0 gets the real file, everything else the `tsqldbg:` virtual document.
/// (<see cref="Line"/>,<see cref="Column"/>)→(<see cref="EndLine"/>,<see cref="EndColumn"/>)
/// is the FULL statement span (A51, §13): the stackTrace reports the whole statement about
/// to execute, not just its first line, so VS Code boxes the entire statement.</summary>
public sealed record SnapshotFrame(
    int Ordinal, ModuleIdentity Module, string ModuleDisplay, int Line, int Column,
    int EndLine, int EndColumn,
    IReadOnlyList<VariableSlot> Variables, SystemScopeValues SystemScope,
    IReadOnlyList<TempObjectProjection> TempObjects,
    int BatchIndex = 0, int BatchCount = 1,    // M8 (§5.4): the live PHYSICAL GO batch (stackTrace annotation); 0/1 for procedure mode / single-batch scripts
    int BatchIteration = 1, int BatchRepeat = 1);   // §5.4/A43: `GO N` iteration i of repeat M (1/1 when not repeated) — drives the `×i/M` suffix

/// <summary>Client-side display projection of one active error context (§10.6/§12.2).</summary>
public sealed record ErrorContextDisplay(int Number, int Severity, int State, int? Line, string? Procedure, string Message);

/// <summary>M5 I3 (§12.1 System scope): every value here is already-tracked,
/// zero-round-trip client-side state — trancount/xact_state from the stop's own
/// control row, spid from connection open, XACT_ABORT from the frame env, the rest
/// from the §11.2 runtime tracker.</summary>
public sealed record SystemScopeValues(
    int Trancount, int XactState, int Spid, bool XactAbortOn,
    IReadOnlyDictionary<string, string> RuntimeOptions, string ModeAnnotation,
    // A52 (§12.1): the frame's parse-time options (QUOTED_IDENTIFIER/ANSI_NULLS) from
    // Frame.SetEnv — a module frame shows its CAPTURED values (fact 16), the ad-hoc
    // script frame shows its live tracked values (A49), reflected on step-in/step-out.
    bool QuotedIdentifier = true, bool AnsiNulls = true);

/// <summary>M5 I4 (§12.2 Temp Tables scope): one chain-resolved registry entry for a
/// given DAP frame. <see cref="EagerDisplay"/> is non-null exactly when NO probe is
/// needed (doomed C23/C25 strings, broken "session terminated") — null means
/// healthy/detached, and the value must come from the lazy fill (rowcount/cursor
/// status), cached in the owning <see cref="StopSnapshot"/> by <see cref="PhysicalName"/>.</summary>
public sealed record TempObjectProjection(
    string OriginalName, string PhysicalName, TempObjectKind Kind, int CreatedAtTrancount, string? EagerDisplay);
