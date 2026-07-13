using Microsoft.SqlServer.TransactSql.ScriptDom;
using TsqlDbg.Core.Interpreter;

namespace TsqlDbg.Core.Batches;

/// <summary>The session-health half of the §14/A21 gate (design note B2): boost
/// requires a PLAIN-HEALTHY session — not doomed, not detached, not broken, no active
/// §10.2 error context (cursor inside a CATCH's dynamic extent), and (A26/D1) the R6
/// scope-identity chain in sync (§7.4). The session supplies its tracked state; Core
/// stays fake-testable (§20.2).</summary>
public sealed record BoostSessionGate(bool Doomed, bool Detached, bool Broken, bool ErrorContextActive, bool ChainPoisoned)
{
    public static readonly BoostSessionGate PlainHealthy = new(false, false, false, false, false);
}

/// <summary>An eligible subtree, ready for <see cref="ComposedBatchBuilder.BuildForBoostedSubtree"/>
/// and the session's B6/B7 maps.</summary>
public sealed record BoostPlan(
    StatementUnit ControlNode,
    IReadOnlyList<BoostMarker> Markers,
    // LineMap: original line → the unique fault-capable SU starting there (executable
    // start lines + IF/WHILE predicate lines) — B6's err_line mapping, uniqueness
    // guaranteed by the line-ambiguity refusal.
    IReadOnlyDictionary<int, StatementUnit> LineMap,
    IReadOnlyList<StatementUnit> MemberUnits);

/// <summary>A refusal — always safe (the node just interprets normally). ReasonCode is
/// the B10 <c>boost.refuse</c> trace vocabulary: stable, greppable, the debugging
/// surface for "why is my loop slow".</summary>
public sealed record BoostRefusal(string ReasonCode, string Detail);

public sealed record BoostPlanResult(BoostPlan? Plan, BoostRefusal? Refusal)
{
    public bool Eligible => Plan is not null;
    internal static BoostPlanResult Refuse(string reasonCode, string detail) => new(null, new BoostRefusal(reasonCode, detail));
}

// DESIGN §14 (A21, ratified 2026-07-07) — the conservative-closed eligibility walk.
// Like RequiresTransactionProtection (Session.cs): membership is a WHITELIST and
// anything not explicitly allowed is refused, so a new SuSubKind (or a new DML node
// type) defaults to ineligible without anyone remembering boost exists. The lockstep
// pin (BoostPlannerTests) ties AllowedMemberSubKinds to the A21 text verbatim — the
// D5 HasArmedTry/RouteError precedent: eligibility and spec must not drift apart
// silently.
public static class BoostPlanner
{
    /// <summary>The A21 member whitelist, by SuSubKind. Two entries carry additional
    /// node-type gates enforced in the walk: General admits ONLY the five DML
    /// statement types (SELECT without INTO / INSERT / UPDATE / DELETE / MERGE — a
    /// CREATE TABLE on a real table also classifies General and must refuse), and
    /// CursorOp admits OPEN/FETCH/CLOSE but not DEALLOCATE (registry
    /// MarkChainEntryDead must run per-SU).</summary>
    public static readonly IReadOnlySet<SuSubKind> AllowedMemberSubKinds = new HashSet<SuSubKind>
    {
        SuSubKind.If, SuSubKind.While,                          // nested control
        SuSubKind.Break, SuSubKind.Continue, SuSubKind.Goto,    // internal jumps (GOTO target-checked)
        SuSubKind.General,                                      // DML/SELECT (node-type-gated)
        SuSubKind.SetVariable,
        SuSubKind.Print,
        SuSubKind.RaiseError,
        SuSubKind.Throw,                                        // THROW with args — faults through the oracle natively
        SuSubKind.CursorOp,                                     // OPEN/FETCH/CLOSE (DEALLOCATE-gated)
    };

    /// <param name="isBlocked">The caller's breakpoint/logpoint predicate over member
    /// SUs (incl. conditional/hit-count — all require per-hit evaluation). The adapter
    /// passes its per-module store lookup; the fidelity harness passes _ => false.
    /// Core stays ignorant of DAP breakpoint storage (B1).</param>
    public static BoostPlanResult TryPlan(
        StatementUnit controlNode, StatementIndex index,
        BoostSessionGate gate, Func<StatementUnit, bool> isBlocked)
    {
        // ---- B2: plain-healthy session only. --------------------------------------
        if (gate.Broken) return BoostPlanResult.Refuse("session-broken", "the session is broken");
        if (gate.Doomed) return BoostPlanResult.Refuse("session-doomed", "the transaction is doomed (§10.4)");
        if (gate.Detached) return BoostPlanResult.Refuse("session-detached", "the safety transaction is detached (§10.4/A9)");
        if (gate.ErrorContextActive) return BoostPlanResult.Refuse("error-context-active", "the cursor is inside a CATCH's dynamic extent (§10.2)");
        // A26/D1 (§7.4/§14): a boosted slice's post-block SCOPE_IDENTITY() capture is
        // trustworthy only when the R6 chain was in sync at dispatch — whether an
        // in-slice INSERT executed is branch-dependent and not statically knowable, so
        // gating at dispatch is the only exact placement. Refusal falls back to
        // interpreted (always safe); the window is short (until the next insert-family SU).
        if (gate.ChainPoisoned) return BoostPlanResult.Refuse("chain-poisoned", "the R6 scope-identity chain is out of sync (§7.4/A26)");

        if (controlNode.SubKind is not (SuSubKind.If or SuSubKind.While))
        {
            return BoostPlanResult.Refuse("not-control-node", $"boost triggers on IF/WHILE only; got {controlNode.SubKind}");
        }

        // ---- member walk (whole subtree, root included). --------------------------
        var members = new List<StatementUnit>();
        var lineMap = new Dictionary<int, StatementUnit>();
        var subtreeStart = controlNode.Fragment.StartOffset;
        var subtreeEnd = subtreeStart + controlNode.Fragment.FragmentLength;

        BoostRefusal? WalkStatement(TSqlStatement statement)
        {
            var c = SuClassifier.Classify(statement);
            switch (c.Kind)
            {
                case SuKind.Label:
                    break;                                       // no-op jump target — never a member

                case SuKind.Structural when c.SubKind == SuSubKind.TryCatch:
                    // An attention re-entry into a natively-entered CATCH would need an
                    // error context we never saw — refuse rather than carry that
                    // liability (A21; refusal costs only speed).
                    return new BoostRefusal("try-catch", $"line {statement.StartLine}: TRY/CATCH inside the subtree");

                case SuKind.Structural:
                    break;                                       // bare BEGIN…END — transparent

                default:
                {
                    if (!index.TryGetUnit(statement, out var unit))
                    {
                        return new BoostRefusal("unindexed-statement", $"line {statement.StartLine}: {statement.GetType().Name} has no SU — internal");
                    }

                    if (unit.Kind == SuKind.Declare)
                    {
                        // Scalar DECLARE (initializers are per-SU synthetic SETs),
                        // table-variable DECLARE (R1 hoist bookkeeping) — A21 refuses
                        // DECLARE outright before the subkind whitelist (a scalar
                        // DECLARE classifies General, which the whitelist admits for DML).
                        return new BoostRefusal("member:Declare", $"line {unit.Span.StartLine}: DECLARE is not boost-eligible (§14/A21)");
                    }

                    if (!AllowedMemberSubKinds.Contains(unit.SubKind))
                    {
                        return new BoostRefusal($"member:{unit.SubKind}", $"line {unit.Span.StartLine}: {unit.SubKind} is not boost-eligible (§14/A21)");
                    }

                    // Node-type gates on the two broad subkinds (conservative-closed
                    // INSIDE the whitelist too — unknown General shapes refuse).
                    if (unit.SubKind == SuSubKind.General)
                    {
                        var dmlRefusal = unit.Fragment switch
                        {
                            SelectStatement { Into: not null } => "select-into",   // temp DDL in disguise (A24/R1 registers it)
                            SelectStatement or InsertStatement or UpdateStatement or DeleteStatement or MergeStatement => null,
                            _ => $"member:General/{unit.Fragment.GetType().Name}",
                        };
                        if (dmlRefusal is not null)
                        {
                            return new BoostRefusal(dmlRefusal, $"line {unit.Span.StartLine}: {unit.Fragment.GetType().Name} is not boost-eligible DML (§14/A21)");
                        }
                    }

                    if (unit.SubKind == SuSubKind.CursorOp && unit.Fragment is DeallocateCursorStatement)
                    {
                        return new BoostRefusal("cursor-deallocate", $"line {unit.Span.StartLine}: DEALLOCATE is a §9 registry site and must run per-SU");
                    }

                    if (unit.SubKind == SuSubKind.Goto)
                    {
                        var key = ControlFlowMap.LabelKey(((GoToStatement)unit.Fragment).LabelName.Value);
                        var target = index.ControlFlow.Labels[key].Fragment;   // existence validated at launch (engine 133)
                        if (target.StartOffset < subtreeStart || target.StartOffset >= subtreeEnd)
                        {
                            return new BoostRefusal("goto-outside-subtree", $"line {unit.Span.StartLine}: GOTO {key} targets outside the boosted subtree");
                        }
                    }

                    if (unit.SubKind == SuSubKind.While && ((WhileStatement)unit.Fragment).Statement is not BeginEndBlockStatement)
                    {
                        // Invariant P is uninstrumentable on single-statement loop
                        // bodies: iterations would repeat with no persisted position.
                        return new BoostRefusal("loop-body-not-block", $"line {unit.Span.StartLine}: WHILE body is not a BEGIN…END block");
                    }

                    if (isBlocked(unit))
                    {
                        return new BoostRefusal("breakpoint-or-logpoint", $"line {unit.Span.StartLine}: a breakpoint or logpoint binds to this line");
                    }

                    members.Add(unit);

                    // Fault-capable positions: executable start lines + IF/WHILE
                    // predicate lines (fact 29: ERROR_LINE = the statement's START
                    // line). Two sharing one original line would make B6's
                    // err_line → SU mapping ambiguous — refuse (A21).
                    if (unit.Kind == SuKind.Executable || unit.SubKind is SuSubKind.If or SuSubKind.While)
                    {
                        if (!lineMap.TryAdd(unit.Span.StartLine, unit))
                        {
                            return new BoostRefusal("line-ambiguity", $"line {unit.Span.StartLine}: two fault-capable positions share the line (§10.2 mapping)");
                        }
                    }

                    break;
                }
            }

            foreach (var (_, children) in InterpreterScopes.ChildrenOf(statement))
            {
                foreach (var child in children)
                {
                    if (WalkStatement(child) is { } refusal)
                    {
                        return refusal;
                    }
                }
            }

            return null;
        }

        if (WalkStatement(controlNode.Fragment) is { } memberRefusal)
        {
            return new BoostPlanResult(null, memberRefusal);
        }

        // ---- structural scans over the whole slice (A21). -------------------------
        var scan = new IntrinsicScanVisitor();
        controlNode.Fragment.Accept(scan);
        if (scan.IntrinsicName is { } intrinsic)
        {
            return BoostPlanResult.Refuse("intrinsic-reference", $"{intrinsic} referenced inside the subtree (no sound interior rewrite exists — §14/A21)");
        }

        if (scan.SawNextValueFor)
        {
            return BoostPlanResult.Refuse("next-value-for", "NEXT VALUE FOR inside the subtree — a durable side effect makes assignment-retry after attention unsound (B7)");
        }

        var markers = BoostSubtreeMarkers.Compute(controlNode.Fragment);
        return new BoostPlanResult(new BoostPlan(controlNode, markers, lineMap, members), null);
    }

    // Mirrors R4/R5's GlobalVariableExpression detection and R6/R7's bare-FunctionCall
    // detection exactly (no CallTarget, no parameters — a schema-qualified
    // dbo.ERROR_MESSAGE() is a user UDF, not the intrinsic). String literals,
    // [bracketed identifiers], and comments are structurally silent (§7.4 R8-negative).
    // BuildForBoostedSubtree's RequiredShadows assert is the build-time lockstep
    // backstop: an intrinsic this scan missed but a rule rewrote still throws.
    private sealed class IntrinsicScanVisitor : TSqlFragmentVisitor
    {
        private static readonly HashSet<string> RefusedFunctions = new(StringComparer.OrdinalIgnoreCase)
        {
            "SCOPE_IDENTITY",                                   // R6
            "ERROR_NUMBER", "ERROR_SEVERITY", "ERROR_STATE",    // R7 family — refused even
            "ERROR_LINE", "ERROR_PROCEDURE", "ERROR_MESSAGE",   // outside a CATCH (A21)
        };

        public string? IntrinsicName { get; private set; }
        public bool SawNextValueFor { get; private set; }

        public override void Visit(GlobalVariableExpression node)
        {
            // @@FETCH_STATUS/@@TRANCOUNT/XACT_STATE()/@@IDENTITY/... stay LIVE truth
            // (§7.4 never rewrites them) — only the R4/R5 shadow pair refuses.
            if (string.Equals(node.Name, "@@ROWCOUNT", StringComparison.OrdinalIgnoreCase)
                || string.Equals(node.Name, "@@ERROR", StringComparison.OrdinalIgnoreCase))
            {
                IntrinsicName ??= node.Name.ToUpperInvariant();
            }
        }

        public override void Visit(FunctionCall node)
        {
            if (node.CallTarget is null && node.Parameters.Count == 0
                && RefusedFunctions.Contains(node.FunctionName.Value))
            {
                IntrinsicName ??= node.FunctionName.Value.ToUpperInvariant() + "()";
            }
        }

        public override void Visit(NextValueForExpression node) => SawNextValueFor = true;
    }
}
