using Microsoft.SqlServer.TransactSql.ScriptDom;
using TsqlDbg.Core.Interpreter;

namespace TsqlDbg.Core.Batches;

// DESIGN §14 (A21, ratified 2026-07-07) — the bookkeeping-insertion plan for one
// boosted subtree: one marker candidate after every direct child of every REAL
// BEGIN…END StatementList in the subtree (single-statement IF branches have no list
// and need no interior marker — the post-IF boundary marker covers both arms;
// design note B4). Marker semantics are POSITIONAL, not conditional: pos = k means
// "control most recently passed the point after child k", which is honest even when
// the child was a skipped IF branch (fact 12/27 predicate resets) or unreachable text
// after BREAK/CONTINUE/GOTO (never fires). Computed client-side so the B7 recovery
// read can map a persisted pos back to a lexical resume point
// (ExecutionCursor.ResumeAfterBoostMarker).
public sealed record BoostMarker(
    int Pos,                 // the value the marker writes; index into the plan's marker table
    TSqlStatement Child,     // the statement-list child the marker follows (resume point = after it)
    bool WritesState,        // child (or its subtree) assigns variables → guarded state write BEFORE the pos update
    bool Suppressed);        // A21 trailing suppression: control can only exit the subtree from here — not emitted

public static class BoostSubtreeMarkers
{
    /// <summary>
    /// Computes the marker table for <paramref name="rootNode"/> (an IF or WHILE
    /// statement) in document order of the insertion points. Suppression (A21):
    /// a marker is suppressed iff it is the last of its list AND control leaving that
    /// point can only flow out of the subtree without crossing another user statement
    /// or predicate — so the postamble's live @@ROWCOUNT/@@ERROR capture reads native
    /// post-block values (fact 27). Loop-body tails are NEVER suppressed (control
    /// flows to the predicate re-evaluation, which is itself the last resetter —
    /// fact 12/27 make the values native either way).
    /// </summary>
    public static IReadOnlyList<BoostMarker> Compute(TSqlStatement rootNode)
    {
        if (rootNode is not (IfStatement or WhileStatement))
        {
            throw new ArgumentException(
                $"Boost roots are IF/WHILE nodes only (§14/A21); got {rootNode.GetType().Name}.", nameof(rootNode));
        }

        var markers = new List<BoostMarker>();
        WalkStatement(rootNode, followingPointExits: true, markers);
        return markers;
    }

    // followingPointExits: whether the point immediately AFTER `statement` (in its
    // container) flows out of the subtree without crossing another statement/predicate.
    // For the root itself that point is the postamble — it exits by definition.
    private static void WalkStatement(TSqlStatement statement, bool followingPointExits, List<BoostMarker> markers)
    {
        switch (statement)
        {
            case BeginEndBlockStatement block:
                WalkList(block.StatementList.Statements, followingPointExits, markers);
                break;
            case IfStatement ifStatement:
                // A branch tail flows to the point after the IF — exit status inherited.
                if (ifStatement.ThenStatement is { } thenStatement)
                    WalkStatement(thenStatement, followingPointExits, markers);
                if (ifStatement.ElseStatement is { } elseStatement)
                    WalkStatement(elseStatement, followingPointExits, markers);
                break;
            case WhileStatement whileStatement:
                // A body tail flows to the predicate re-evaluation — always in-subtree.
                if (whileStatement.Statement is { } body)
                    WalkStatement(body, followingPointExits: false, markers);
                break;
                // TryCatchStatement is unreachable here: B3 refuses TRY/CATCH anywhere in
                // an eligible subtree (BoostPlanner walks first). Leaves have no lists.
        }
    }

    private static void WalkList(IList<TSqlStatement> list, bool listEndExits, List<BoostMarker> markers)
    {
        for (var i = 0; i < list.Count; i++)
        {
            var child = list[i];
            var afterChildExits = i == list.Count - 1 && listEndExits;
            // Recurse first: the child's interior insertion points precede the
            // after-child point in document order, keeping Pos monotonic by offset.
            WalkStatement(child, afterChildExits, markers);
            markers.Add(new BoostMarker(markers.Count, child, AssignsVariables(child), afterChildExits));
        }
    }

    /// <summary>
    /// True when the statement (or anything in its subtree — a nested IF's branches
    /// count, design note B4) assigns scalar variables: <c>SET @v</c>,
    /// <c>SELECT @v = …</c>, <c>FETCH … INTO @v</c>, or DML <c>SET @v = …</c>
    /// assignment clauses (UPDATE can assign variables too — conservative superset of
    /// B4's list, over-emitting a state write is always sound). DECLARE initializers
    /// and EXEC @rc-assignments cannot appear: both statement kinds are B3-refused.
    /// </summary>
    internal static bool AssignsVariables(TSqlStatement statement)
    {
        var visitor = new AssignmentVisitor();
        statement.Accept(visitor);
        return visitor.Found;
    }

    private sealed class AssignmentVisitor : TSqlFragmentVisitor
    {
        public bool Found { get; private set; }

        public override void Visit(SetVariableStatement node) => Found = true;
        public override void Visit(SelectSetVariable node) => Found = true;
        public override void Visit(AssignmentSetClause node)
        {
            if (node.Variable is not null) Found = true;
        }

        public override void Visit(FetchCursorStatement node)
        {
            if (node.IntoVariables is { Count: > 0 }) Found = true;
        }
    }
}
