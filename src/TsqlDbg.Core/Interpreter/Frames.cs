// DESIGN §6 (FrameStack), §8 (variable catalog), §9 (temp registry), §11 (frames).
// Phase-0 reference implementation (Fable): M1 uses exactly one frame; the shape here is
// what M4 push/pop extends. See docs/phase0-integration-notes.md.
using System;
using System.Collections.Generic;

namespace TsqlDbg.Core.Interpreter;

/// <summary>Identity of the module a frame executes (§5.2/§11).</summary>
public sealed record ModuleIdentity(string? Database, string? Schema, string Name, bool IsScript)
{
    /// <summary>A58 (§11.6): the reserved schema token that marks a dynamic-SQL frame, so a
    /// dynamic identity round-trips through the existing 3-segment <c>tsqldbg:</c> virtual-doc
    /// path (<c>{schema}.{name}.sql</c>) with no new DAP surface.</summary>
    public const string DynamicSchema = "__dyn";

    public static ModuleIdentity Script(string name = "<script>") => new(null, null, name, true);

    /// <summary>A58 (§11.6): a dynamic-SQL frame — text that exists only in the session, keyed
    /// by a CONTENT hash so that re-executing the same string (a loop, a repeated call) re-binds
    /// to the same virtual document and keeps the breakpoints the user set in it.</summary>
    public static ModuleIdentity Dynamic(string? database, string contentHash)
        => new(database, DynamicSchema, contentHash, IsScript: false) { IsDynamic = true };

    /// <summary>A58: true for a dynamic-SQL frame. Distinct from <see cref="IsScript"/> (frame 0's
    /// real .sql file) and from a catalog module: a dynamic batch has NO <c>sys.sql_modules</c> row,
    /// so it takes its parse settings from its caller (fact 33a), and native <c>ERROR_PROCEDURE()</c>
    /// reads NULL inside one (fact 33c) — §10.2 must not synthesize a module name for it.</summary>
    public bool IsDynamic { get; init; }

    /// <summary>A58 (fact 33e): engine nesting levels this frame costs. A dynamic batch runs at
    /// <c>@@NESTLEVEL + 2</c> — <c>sp_executesql</c> is itself a module at level 1 — so the §11.3
    /// step-3 synthetic-217 mirror must weight it 2, not 1.</summary>
    public int NestCost => IsDynamic ? 2 : 1;

    public string Display => IsScript
        ? Name
        : IsDynamic ? $"dynamic SQL #{Name}" : $"{Schema ?? "dbo"}.{Name}";
}

/// <summary>
/// Frame SET-option environment (§7.1 preamble lines, §11.2). M1: frame 0's values come
/// from sys.sql_modules (finding F4) in procedure mode, defaults in script mode. M4 adds
/// snapshot/restore of proc-scoped options on push/pop.
/// </summary>
public sealed record SetOptionEnvironment(bool QuotedIdentifier, bool AnsiNulls)
{
    public static readonly SetOptionEnvironment Default = new(true, true);
}

/// <summary>A registered variable: catalog ordinal drives the display projection (v_0… §7.5).</summary>
public sealed record VariableSlot(int Ordinal, VariableDeclaration Declaration);

public sealed class DuplicateVariableException : Exception
{
    public DuplicateVariableException(string name)
        : base($"The variable name '{name}' has already been declared in this frame " +
               "(mirrors engine rule; DESIGN §8.2: parse-time diagnostic).") { }
}

/// <summary>
/// DESIGN §8.2 (A59): a declared type the debugger cannot store. Today that is exactly the
/// CLR (assembly) user-defined type — it has no literal form for the state table or the
/// re-seed. Named rather than left to the server, which would otherwise raise a bare 2715
/// from deep inside frame init. Refuses the launch at frame 0; a callee push catches it and
/// steps over instead.
/// </summary>
public sealed class UnsupportedVariableTypeException : Exception
{
    public UnsupportedVariableTypeException(string variableName, string dataTypeSql, string reason)
        : base($"The variable {variableName} is declared as '{dataTypeSql}', which the debugger " +
               $"cannot step through: {reason} (DESIGN §8.2).") { }
}

/// <summary>
/// Ordered, case-insensitive registry of a frame's live variables (§8). Consumed by the
/// state-table DDL generator (§8.1), the composed-batch preamble/postamble (§7.1), and
/// the Locals scope (§12.1). Populated as the cursor performs DECLARE actions (§8.2).
/// </summary>
public sealed class VariableCatalog
{
    private readonly List<VariableSlot> _ordered = new();
    private readonly Dictionary<string, VariableSlot> _byName = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<VariableSlot> All => _ordered;
    public int Count => _ordered.Count;

    public VariableSlot Register(VariableDeclaration declaration)
    {
        if (_byName.ContainsKey(declaration.Name))
            throw new DuplicateVariableException(declaration.Name);
        var slot = new VariableSlot(_ordered.Count, declaration);
        _ordered.Add(slot);
        _byName.Add(declaration.Name, slot);
        return slot;
    }

    public bool TryGet(string name, out VariableSlot slot) => _byName.TryGetValue(name, out slot!);
}

/// <summary>
/// DESIGN §8.2/§9 (A59): a <c>DECLARE @t dbo.MyTable</c> variable. Realized as a #temp like
/// any table variable (R1), but it keeps its TYPE — because a #temp cannot be passed to a
/// table-valued parameter and only a variable of the type can, so an <c>EXEC p @rows = @t</c>
/// argument is served by re-declaring the real thing in the composed batch's preamble and
/// filling it from the realization (§9). <see cref="InsertableColumns"/> excludes IDENTITY
/// and computed columns: neither can be supplied to a table variable (fact 34e → C28).
/// </summary>
public sealed class TableTypeVariable
{
    public required string Name { get; init; }                    // with '@'
    public required State.UserTypeEntry Type { get; init; }
    public required string RealizationName { get; init; }         // #__dbgtv_{frame}_{name}
    public IReadOnlyList<string> InsertableColumns { get; set; } = Array.Empty<string>();

    /// <summary>The type's IDENTITY column, or null (§9/C28). Non-null means the §9 TVP
    /// materialization both needs an ORDER BY (so identity values are re-generated in the
    /// realization's own order) and MOVES the connection's identity chain (fact 34h) —
    /// SCOPE_IDENTITY() included, which is R6-shadowed and therefore the session's problem.</summary>
    public string? IdentityColumn { get; set; }
}

public enum TempObjectKind { TempTable, TableVariable, Cursor }

/// <summary>One frame-owned server object per §9 (populated from M4's R1–R3 rewrites).</summary>
public sealed class TempObjectEntry
{
    public required string OriginalName { get; init; }   // as written in source (#work, @t, cur)
    public required string PhysicalName { get; init; }   // frame-renamed (#work__f2, #__dbgtv_2_t, …)
    public required TempObjectKind Kind { get; init; }
    public required int CreatedAtTrancount { get; init; }

    /// <summary>M4 (§9/D7-D8): the guarded re-creation DDL for objects the debugger
    /// must heal after a rollback destroyed the realization where native would have
    /// kept the object — table variables only (fact 2: natively non-transactional;
    /// caveat C25 covers the contents). Null for user #temps (their rollback loss is
    /// natively faithful) and cursors.</summary>
    public string? RecreateDdl { get; init; }

    public bool IsDead { get; private set; }             // destroyed by a ROLLBACK past creation (§9)
    public void MarkDead() => IsDead = true;
    /// <summary>M4 (D8): the realization was re-created (empty, C25) after rollback loss.</summary>
    public void Revive() => IsDead = false;

    /// <summary>M8 (§5.4/§9): true for connection-scoped objects that survive a <c>GO</c>
    /// boundary natively — user <c>#temp</c>/<c>##global</c> tables and GLOBAL cursors —
    /// which <c>ExitBatch</c> promotes into the session-persistent registry; false for
    /// batch-local objects (table-variable realizations R1, LOCAL cursors R3) which
    /// <c>ExitBatch</c> tears down at the boundary (Appendix C fact 32a; facts 1/2/3).
    /// Defaults true (a plain <c>#temp</c> create); table-variable and LOCAL-cursor
    /// records set it false. Immaterial in procedure mode / single-batch scripts, where
    /// no boundary ever fires.</summary>
    public bool SurvivesBatchBoundary { get; init; } = true;
}

/// <summary>M4 (§11.3/§11.5): one <c>callerVar ↔ calleeParam</c> OUTPUT pairing,
/// recorded at push, applied at a COMPLETED pop only (fact 23: copy-back is
/// completion-gated; aborted calls leave caller variables at pre-call values).</summary>
public sealed record OutputPair(string CallerVariable, string CalleeParameter);

/// <summary>
/// M4 (§11.3/§11.5): everything a pop needs to know about how this frame was entered.
/// Lives on the CALLEE frame; null exactly for frame 0 (which never pops — its
/// completion is the session-end path, §6).
/// </summary>
public sealed record FrameCallSite(
    StatementUnit CallUnit,                              // the caller's EXEC SU
    IReadOnlyList<OutputPair> OutputPairs,               // OUTPUT copy-back plan (§11.5)
    string? ReturnCodeVariable,                          // caller var of `EXEC @rc = …`, or null
    IReadOnlyDictionary<string, string> RuntimeOptionsAtEntry,  // §11.2 restore baseline (D6)
    decimal? CallerScopeIdentityAtEntry);                // SCOPE_IDENTITY() is per-scope: restored at pop

/// <summary>Per-frame registry of temp tables / table variables / cursors (§9).</summary>
public sealed class TempObjectRegistry
{
    private readonly List<TempObjectEntry> _entries = new();
    public IReadOnlyList<TempObjectEntry> All => _entries;

    public void Add(TempObjectEntry entry) => _entries.Add(entry);

    /// <summary>Innermost-frame lookup happens in <see cref="FrameStack.ResolveTempObject"/>; this is one frame's slice.</summary>
    public TempObjectEntry? TryResolve(string originalName)
    {
        for (int i = _entries.Count - 1; i >= 0; i--)
            if (!_entries[i].IsDead && string.Equals(_entries[i].OriginalName, originalName, StringComparison.OrdinalIgnoreCase))
                return _entries[i];
        return null;
    }

    /// <summary>§9/§10.4: a rollback destroyed everything created above the surviving trancount level.</summary>
    public void MarkDeadAbove(int survivingTrancount)
    {
        foreach (var e in _entries)
            if (!e.IsDead && e.CreatedAtTrancount > survivingTrancount)
                e.MarkDead();
    }
}

/// <summary>
/// One level of the virtual call stack (§1/§11): the launch script/proc is frame 0; each
/// stepped-into module pushes a frame (M4). Owns the cursor, the state-table name (§8.1 —
/// creation itself is the §8 implementer's job), variables, temp registry, and SET env.
/// M4 extension point: return-target info (EXEC @rc = …, OUTPUT pairs) lives here (§11.5).
/// DESIGN §6's per-frame "LoopStack" and "label map" are deliberately NOT fields here:
/// the loop stack is realized by the While entries on the frame's own cursor stack (no
/// parallel structure to keep in sync) and the label map lives on the cursor's
/// StatementIndex.ControlFlow — see docs/archive/reviews/m2-cursor-design-notes-fable.md.
/// </summary>
public sealed class Frame
{
    public int Ordinal { get; }
    public ModuleIdentity Module { get; }
    public ExecutionCursor Cursor { get; }
    public SetOptionEnvironment SetEnv { get; internal set; }
    public string StateTableName { get; }                       // "#__dbg_s{n}" (§8.1)
    public VariableCatalog Variables { get; } = new();
    public TempObjectRegistry TempObjects { get; } = new();

    /// <summary>
    /// DESIGN §8.2/§9 (A59): the frame's <c>DECLARE @t dbo.MyTable</c> variables — table
    /// variables in everything but syntax, so they are deliberately absent from
    /// <see cref="Variables"/> (no state-table column, no preamble DECLARE) and present in
    /// <see cref="TempObjects"/> as R1 realizations instead. Kept alongside because the
    /// composed batch needs the type back: passing one as a TVP argument means declaring a
    /// real variable of that type and filling it from the realization (§9).
    /// Name (with '@') → the variable.
    /// </summary>
    public Dictionary<string, TableTypeVariable> TableTypeVariables { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// M3 (§7.2/§10.4): the debuggee's runtime SET XACT_ABORT state, tracked from
    /// executed SET SUs (session-scoped on the real connection; starts OFF because
    /// session init runs SET XACT_ABORT OFF). Load-bearing for the fact-19 sandwich:
    /// re-materialization and debugger-initiated evals must restore exactly this.
    /// M4: a pushed frame INHERITS the caller's value (runtime options are
    /// connection-scoped); the F5 preamble re-asserts it per batch, so pop restore is
    /// free. Parse-time options (QI/ANSI_NULLS) live in <see cref="SetEnv"/> and are
    /// pinned — fact 16: a mid-MODULE SET of those is a runtime no-op. A49 exception:
    /// the ad-hoc SCRIPT batch (script mode, <see cref="CallSite"/> null) is not a
    /// module, so it DOES fold a runtime SET QI/ANSI_NULLS into <see cref="SetEnv"/>
    /// (Session ExecuteUnit path) — see §5.4/§11.2.
    /// </summary>
    public bool XactAbortOn { get; internal set; }

    /// <summary>M4 (§8.1/D1): this frame's binary snapshot — the values of the last
    /// __dbg_state set a batch of THIS frame produced. Frames &gt; 0 are seeded at push
    /// with the evaluated argument vector and are therefore never null; frame 0 keeps
    /// the M3 nullable-with-table-fallback semantics (doom before the first
    /// value-bearing batch reads the not-yet-stale table, which is correct).</summary>
    public object?[]? Snapshot { get; internal set; }

    /// <summary>M4 (§11.5): the frame's __ret — set by RETURN (0 for bare/body-end),
    /// consumed by the completed pop's `EXEC @rc =` assignment (fact 23). Frame 0's
    /// value surfaces as SessionResult.ReturnCode.</summary>
    public int ReturnCode { get; internal set; }

    /// <summary>M4 (§11.3): how this frame was entered; null exactly for frame 0.</summary>
    public FrameCallSite? CallSite { get; }

    public Frame(
        int ordinal, ModuleIdentity module, ExecutionCursor cursor, SetOptionEnvironment setEnv,
        FrameCallSite? callSite = null)
    {
        Ordinal = ordinal;
        Module = module;
        Cursor = cursor;
        SetEnv = setEnv;
        StateTableName = $"#__dbg_s{ordinal}";
        CallSite = callSite;
    }
}

public sealed class FrameDepthException : Exception
{
    public FrameDepthException()
        : base("Maximum module nesting depth (32) exceeded — mirrors engine error 217 (DESIGN §11.3).") { }
}

/// <summary>
/// The virtual call stack (§6/§11). M1: exactly the root frame. Ordinals are monotonic
/// per session and never reused, so state tables and renames stay unique under recursion
/// (§11.4). Push/pop mechanics live here now so M4 extends rather than rebuilds.
/// </summary>
public sealed class FrameStack
{
    public const int MaxDepth = 32;                              // engine nesting limit (§11.3, Appendix C fact 10)

    private readonly List<Frame> _frames = new();
    private int _nextOrdinal;

    public Frame Current => _frames.Count > 0
        ? _frames[^1]
        : throw new InvalidOperationException("Frame stack is empty.");
    public int Depth => _frames.Count;
    public IReadOnlyList<Frame> All => _frames;                  // bottom (root) → top; DAP stackTrace reverses

    /// <summary>A58 (§11.3 step 3 / fact 33e): the engine <c>@@NESTLEVEL</c> this virtual stack
    /// models. A script batch is level 0, a procedure frame costs 1, and a dynamic-SQL frame costs
    /// **2** (<c>sp_executesql</c> is itself a module, so its dynamic batch runs two levels down).
    /// Feeds the synthetic-217 mirror only: the debugger FLATTENS, so the server never actually
    /// nests — the limit exists solely to reproduce the engine's own refusal.</summary>
    public int EngineNestLevel
    {
        get
        {
            var level = 0;
            foreach (var frame in _frames)
            {
                level += frame.Module.IsScript ? 0 : frame.Module.NestCost;
            }

            return level;
        }
    }

    /// <summary>Reserves the next monotonic frame ordinal (§11.4).</summary>
    public int NextOrdinal() => _nextOrdinal++;

    public static FrameStack CreateRoot(Frame root)
    {
        if (root.Ordinal != 0)
            throw new ArgumentException("Root frame must have ordinal 0.", nameof(root));
        var s = new FrameStack();
        s._frames.Add(root);
        s._nextOrdinal = 1;
        return s;
    }

    /// <summary>M4 (§11.3 push sequence). Mechanics shipped now; callers arrive with step-into.</summary>
    public void Push(Frame frame)
    {
        if (_frames.Count >= MaxDepth) throw new FrameDepthException();
        _frames.Add(frame);
    }

    /// <summary>M8 (§5.4): at a <c>GO</c> boundary, REPLACE the bottom batch frame with the
    /// next batch's frame — a sequential scope replacement, not a push/pop (GO is not a
    /// call). The stack must be exactly the batch frame (depth 1): every EXEC callee has
    /// already popped, because EXEC is synchronous and returns before a batch's last SU
    /// completes. Ordinals stay monotonic and are never reused (§11.4), so the incoming
    /// frame's fresh ordinal keeps its state table <c>#__dbg_s{ordinal}</c> collision-free.
    /// Unlike <see cref="CreateRoot"/> it asserts no ordinal-0 (batches ≥ 1 carry a fresh
    /// non-zero ordinal); the batch frame is identified by <c>CallSite == null</c>/index 0.</summary>
    public void EnterBatch(Frame frame)
    {
        if (_frames.Count != 1)
        {
            throw new InvalidOperationException(
                $"EnterBatch requires exactly the batch frame on the stack (depth {_frames.Count}) — a " +
                "callee did not pop before the GO boundary (EXEC is synchronous; §5.4/§11.5).");
        }

        if (frame.CallSite is not null)
        {
            throw new ArgumentException("A batch frame carries no call site (CallSite == null; §5.4).", nameof(frame));
        }

        _frames[0] = frame;
    }

    /// <summary>M4 (§11.5 pop sequence). Root-frame completion is the driver's session-end path, not a pop.</summary>
    public Frame Pop()
    {
        if (_frames.Count <= 1)
            throw new InvalidOperationException("Cannot pop the root frame — frame-0 completion ends the session (§6).");
        var top = _frames[^1];
        _frames.RemoveAt(_frames.Count - 1);
        return top;
    }

    /// <summary>§9: caller-created temp objects are visible to callees; innermost match wins.</summary>
    public TempObjectEntry? ResolveTempObject(string originalName)
    {
        for (int i = _frames.Count - 1; i >= 0; i--)
        {
            var hit = _frames[i].TempObjects.TryResolve(originalName);
            if (hit is not null) return hit;
        }
        return null;
    }
}
