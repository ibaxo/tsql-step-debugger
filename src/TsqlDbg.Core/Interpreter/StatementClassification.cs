// DESIGN §5.1 / §6 — statement-unit taxonomy and milestone gating.
// Phase-0 reference implementation (Fable); M2 control-flow classification added by the
// M2 cursor pass (Fable) — decisions in docs/archive/reviews/m2-cursor-design-notes-fable.md.
using System;
using System.Collections.Generic;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace TsqlDbg.Core.Interpreter;

/// <summary>How the interpreter treats a statement. DESIGN §5.1.</summary>
public enum SuKind
{
    /// <summary>Leaf sent to the server as one composed batch (§7.1).</summary>
    Executable,
    /// <summary>DECLARE @v — interpreted. Declarations are hoisted at frame init
    /// (engine fact 14 — T-SQL declarations are compile-time; see the §8.2 amendment
    /// proposed in the M2 design notes); the SU itself only runs its initializers.</summary>
    Declare,
    /// <summary>Pure structure (BEGIN…END) — the cursor descends transparently, no stop.</summary>
    Structural,
    /// <summary>Control-flow node the interpreter performs itself (§5.1 "interpreted
    /// nodes", §6 phase machines): IF/WHILE predicate stops, GOTO/BREAK/CONTINUE jumps,
    /// RETURN, intercepted WAITFOR. Stoppable units — they carry ordinals/spans and are
    /// breakpoint-mappable — but are never sent to the server as-is.</summary>
    Control,
    /// <summary>A label (<c>name:</c>) — a no-op jump target: never a stop, never a
    /// unit. Recorded in the frame's label map (§6) and skipped transparently by the
    /// cursor; a breakpoint on a label line binds forward to the next unit (§13).</summary>
    Label,
    /// <summary>Recognized but gated to a later milestone — session refuses at validation.</summary>
    Unsupported,
}

/// <summary>Finer classification hooks (§7.2 env recording, §9 registry, §11 step-into, §6 phase machines).</summary>
public enum SuSubKind
{
    General,
    SetVariable,        // SET @x = …
    SetOption,          // SET NOCOUNT / XACT_ABORT / QI / isolation … → frame env recording (§7.2)
    Execute,            // EXEC … — M1/M2: plain step-over; M4 intercepts for step-into (§11.1)
    TempTableDdl,       // CREATE TABLE #x → §9 registry hook (M4)
    ModuleDdl,          // CREATE/ALTER PROCEDURE/FUNCTION/VIEW/TRIGGER (§5.4/A48): must be the
                        // first statement of its batch and is illegal inside a TRY block, so it
                        // is executed BARE (its own raw batch, no §7.1 oracle wrapper)
    Print,
    RaiseError,         // sev>10 faults through the oracle and routes per §10.3 (M3); sev≤10 = InfoMessage, ok=1 (fact 18)
    Other,              // default-open: any single statement executes faithfully as its own batch
    // M2 control-flow subkinds (§6). The cursor's Peek/Advance dispatch on these.
    If,                 // predicate stop → InThenBranch | InElseBranch
    While,              // predicate stop → InWhileBody → predicate stop again
    Goto,               // jump via the frame label map (fact 11/13 semantics)
    Break,              // jump past the innermost WHILE
    Continue,           // jump to the innermost WHILE's predicate re-eval
    Return,             // frame completes (frame 0 → session end; §11.5 pop is M4)
    WaitFor,            // WAITFOR DELAY/TIME — intercepted per launch config skip|honor (§6)
    // M3 error-model subkinds (§10). Decisions in docs/archive/reviews/m3-error-model-design-notes-fable.md.
    TryCatch,           // Structural-with-phases: TRY entry is silent (no work, no stop); CATCH is reachable only via ExecutionCursor.RouteError (§10.3)
    Throw,              // THROW <n>,<msg>,<state> — plain executable; faults through the oracle as a NEW error (§10.2); batch-aborting when unhandled (verified)
    Rethrow,            // bare ;THROW — interpreted re-raise of the ACTIVE error context (§10.2): no server work, exact original number/severity/state/message
    BeginTran,          // §7.2: executed normally; control-row trancount drives the watchdog (§10.4)
    Commit,             // distinct for §10.4's parse-time COMMIT policy scan on rollback-mode sessions
    Rollback,           // distinct for trace/resurrection notes (§10.4)
    SaveTran,
    // M4 frame-scoped objects (§9, R1/R3). Decisions in docs/archive/reviews/m4-frames-design-notes-fable.md.
    TableVarDeclare,    // DECLARE @t TABLE — interpreted no-op stop: the realization (#__dbgtv_{f}_{name})
                        // is HOISTED to frame init/push (compile-time like every DECLARE, fact 14; there
                        // is no initializer syntax, so the SU performs nothing at runtime — D7)
    CursorDeclare,      // DECLARE c CURSOR — executable with R3 patches (rename + LOCAL→GLOBAL) + §9 registry hook.
                        // A63: also `SET @c = CURSOR <def>` (cursor-variable reification) — same registry hook,
                        // but composed by BuildForCursorVariableAssign (generated DECLARE, not a slice+patch).
    CursorOp,           // OPEN/FETCH/CLOSE/DEALLOCATE — executable, CursorId patched by R3; FETCH INTO vars
                        // are ordinary frame vars captured by the postamble; @@FETCH_STATUS is live truth (§7.4)
}

/// <summary>Milestones that gate currently-unsupported constructs (DESIGN §22).</summary>
public enum Milestone { M2 = 2, M3 = 3, M4 = 4 }

public sealed record ClassificationResult(SuKind Kind, SuSubKind SubKind, Milestone? RequiredMilestone, string? GateReason);

/// <summary>
/// Maps ScriptDom statement types to interpreter treatment. DESIGN §5.1.
/// Default is OPEN (unknown statement → Executable/Other): a composed batch executes any
/// single statement faithfully; only control-flow, transaction-control, and
/// module/scope-sensitive constructs are explicitly gated. Decision on record in
/// phase0-integration-notes.md — revisit at M1-close review.
/// </summary>
public static class SuClassifier
{
    public static ClassificationResult Classify(TSqlStatement statement) => statement switch
    {
        // ---- interpreted / structural -------------------------------------------------
        DeclareVariableStatement => new(SuKind.Declare, SuSubKind.General, null, null),
        BeginEndBlockStatement   => new(SuKind.Structural, SuSubKind.General, null, null),

        // ---- M2: control flow (§6 phase machines, §22 M2) -------------------------------
        IfStatement       => new(SuKind.Control, SuSubKind.If, null, null),
        WhileStatement    => new(SuKind.Control, SuSubKind.While, null, null),
        BreakStatement    => new(SuKind.Control, SuSubKind.Break, null, null),
        ContinueStatement => new(SuKind.Control, SuSubKind.Continue, null, null),
        GoToStatement     => new(SuKind.Control, SuSubKind.Goto, null, null),
        LabelStatement    => new(SuKind.Label, SuSubKind.General, null, null),
        ReturnStatement   => new(SuKind.Control, SuSubKind.Return, null, null),
        // WAITFOR DELAY/TIME is intercepted (§6: waitfor skip|honor). The Service Broker
        // WAITFOR(RECEIVE …) form is a default-open executable leaf — it simply blocks
        // until its own TIMEOUT under the one-command-at-a-time model (§3/C20).
        WaitForStatement w => w.WaitForOption is WaitForOption.Delay or WaitForOption.Time
            ? new(SuKind.Control, SuSubKind.WaitFor, null, null)
            : new(SuKind.Executable, SuSubKind.Other, null, null),

        // ---- M3: TRY/CATCH + THROW + transaction control (§10, §22 M3) -------------------
        // TRY/CATCH is structural-with-phases: BEGIN TRY does no work (unlike IF/WHILE
        // there is no predicate step), so entering it is silent; the CATCH side is
        // entered only by §10.3 routing (ExecutionCursor.RouteError).
        TryCatchStatement => new(SuKind.Structural, SuSubKind.TryCatch, null, null),
        // Bare ;THROW re-raises the active error context — interpreted client-side
        // (§10.2: the saved context IS the exact original error; no server round trip,
        // no re-materialization infidelity). THROW with arguments is an ordinary
        // executable that faults through the oracle as a NEW error.
        ThrowStatement t => t.ErrorNumber is null
            ? new(SuKind.Control, SuSubKind.Rethrow, null, null)
            : new(SuKind.Executable, SuSubKind.Throw, null, null),
        // §7.2: transaction statements execute normally; the control row's
        // trancount/xact_state drive the §10.4 watchdog (doom + resurrection). COMMIT
        // gets its own subkind for the parse-time policy scan on rollback-mode sessions.
        BeginTransactionStatement    => new(SuKind.Executable, SuSubKind.BeginTran, null, null),
        CommitTransactionStatement   => new(SuKind.Executable, SuSubKind.Commit, null, null),
        RollbackTransactionStatement => new(SuKind.Executable, SuSubKind.Rollback, null, null),
        SaveTransactionStatement     => new(SuKind.Executable, SuSubKind.SaveTran, null, null),

        // ---- M4: frame-scoped objects (§9, R1/R3) --------------------------------------
        // Table-variable DECLARE is a stoppable no-op (realization hoisted, D7); cursor
        // statements are executables whose names R3 patches through the frame chain.
        DeclareTableVariableStatement => new(SuKind.Declare, SuSubKind.TableVarDeclare, null, null),
        DeclareCursorStatement => new(SuKind.Executable, SuSubKind.CursorDeclare, null, null),
        OpenCursorStatement or FetchCursorStatement or CloseCursorStatement or DeallocateCursorStatement
            => new(SuKind.Executable, SuSubKind.CursorOp, null, null),

        // ---- executable leaves ----------------------------------------------------------
        // A63 (§9): `SET @c = CURSOR <def>` reifies a cursor variable — it is a §9 registry site
        // (creates a frame-unique GLOBAL cursor), so it classifies as CursorDeclare: boost-ineligible
        // (not in the A21 whitelist) and routed to BuildForCursorVariableAssign, not the scalar path.
        SetVariableStatement { CursorDefinition: not null } => new(SuKind.Executable, SuSubKind.CursorDeclare, null, null),
        SetVariableStatement => new(SuKind.Executable, SuSubKind.SetVariable, null, null),
        // A53: the value-carrying SET commands (SetCommandStatement = DATEFIRST / DATEFORMAT /
        // LANGUAGE / LOCK_TIMEOUT / DEADLOCK_PRIORITY; SetTextSizeStatement; SetRowCountStatement)
        // join the on/off + isolation options as SetOption, so the §11.2 tracker records them
        // (display + pop restore). They were previously SuSubKind.Other — executable but
        // untracked. Boost-eligibility is unchanged (SetOption and Other are both A21-refused).
        SetOnOffStatement or SetTransactionIsolationLevelStatement or SetRowCountStatement
            or SetCommandStatement or SetTextSizeStatement
            => new(SuKind.Executable, SuSubKind.SetOption, null, null),
        ExecuteStatement => new(SuKind.Executable, SuSubKind.Execute, null, null),
        PrintStatement   => new(SuKind.Executable, SuSubKind.Print, null, null),
        RaiseErrorStatement => new(SuKind.Executable, SuSubKind.RaiseError, null, null),
        CreateTableStatement ct => new(SuKind.Executable, IsTempTable(ct) ? SuSubKind.TempTableDdl : SuSubKind.General, null, null),
        // §5.4/A48: module-creating DDL is a leaf executed WHOLE by the server (the M2 note
        // above already keeps the validator from descending its body). ProcedureStatementBodyBase
        // covers CREATE/ALTER/CREATE-OR-ALTER PROCEDURE *and* FUNCTION; ViewStatementBody and
        // TriggerStatementBody cover the rest. All must be first in their batch and cannot live
        // inside a TRY, so they run bare (SuSubKind.ModuleDdl) — never the §7.1 oracle wrapper.
        // (In procedure mode frame 0 is the proc BODY, not the CREATE, so this only fires for a
        // module-DDL statement in a script.)
        ProcedureStatementBodyBase or ViewStatementBody or TriggerStatementBody
            => new(SuKind.Executable, SuSubKind.ModuleDdl, null, null),
        SelectStatement or InsertStatement or UpdateStatement or DeleteStatement or MergeStatement
            => new(SuKind.Executable, SuSubKind.General, null, null),

        _ => new(SuKind.Executable, SuSubKind.Other, null, null),
    };

    private static ClassificationResult Gate(Milestone m, string reason)
        => new(SuKind.Unsupported, SuSubKind.General, m, reason);

    private static bool IsTempTable(CreateTableStatement ct)
        => ct.SchemaObjectName?.BaseIdentifier?.Value is { Length: > 0 } n && n[0] == '#';
}

/// <summary>One offending construct found by upfront validation.</summary>
public sealed record MilestoneGateSite(string StatementType, int Line, Milestone RequiredMilestone, string Reason);

/// <summary>
/// Thrown at cursor creation (session launch), never mid-step — DESIGN §6: the whole frame
/// body is validated up front so the user gets one friendly refusal listing everything.
/// </summary>
public sealed class MilestoneNotSupportedException : Exception
{
    public IReadOnlyList<MilestoneGateSite> Sites { get; }

    public MilestoneNotSupportedException(IReadOnlyList<MilestoneGateSite> sites)
        : base(BuildMessage(sites)) => Sites = sites;

    private static string BuildMessage(IReadOnlyList<MilestoneGateSite> sites)
    {
        var lines = new List<string> { $"This code uses {sites.Count} construct(s) not yet supported by the debugger:" };
        foreach (var s in sites)
            lines.Add($"  line {s.Line}: {s.StatementType} — available from {s.RequiredMilestone} ({s.Reason})");
        return string.Join(Environment.NewLine, lines);
    }
}

/// <summary>
/// Walks a frame body and collects every milestone-gated statement, including those
/// nested inside other gated constructs (e.g. a cursor loop inside a TRY body), so the
/// refusal message is complete. DESIGN §6. M2 change: the walk descends interpreter
/// scopes ONLY (<see cref="InterpreterScopes"/>) — module-creating DDL in a script frame
/// (CREATE PROC/FUNCTION/TRIGGER) is a leaf executed whole by the server, so constructs
/// inside its body must NOT trip the gate (the M1 visitor over-descended there).
/// </summary>
public static class MilestoneValidator
{
    public static void ValidateOrThrow(IEnumerable<TSqlStatement> body)
    {
        var sites = new List<MilestoneGateSite>();
        Walk(body, sites);
        if (sites.Count > 0) throw new MilestoneNotSupportedException(sites);
    }

    private static void Walk(IEnumerable<TSqlStatement> statements, List<MilestoneGateSite> sites)
    {
        foreach (var s in statements)
        {
            var c = SuClassifier.Classify(s);
            if (c.Kind == SuKind.Unsupported)
                sites.Add(new MilestoneGateSite(s.GetType().Name, s.StartLine, c.RequiredMilestone!.Value, c.GateReason ?? ""));
            foreach (var (_, children) in InterpreterScopes.ChildrenOf(s))
                Walk(children, sites);
        }
    }
}
