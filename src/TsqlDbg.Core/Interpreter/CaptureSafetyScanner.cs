// DESIGN §11.7 (C11 / A64) — the INSERT…EXEC capture-safety body scan.
// Stepping INTO an `INSERT <target> EXEC proc` captures the callee's result-returning statements
// into the target by prefixing each with `INSERT INTO <target> `. A handful of callee statement
// shapes cannot be captured faithfully that way, so — mirroring the A62 READONLY-TVP-write body
// scan — the presence of ANY of them in the callee body refuses step-into → faithful native
// step-over (SQL Server performs the capture itself as one composed batch). Conservative-closed.
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace TsqlDbg.Core.Interpreter;

public static class CaptureSafetyScanner
{
    /// <summary>
    /// A human-readable reason the callee body cannot be captured statement-by-statement, or null if
    /// it can. Non-null ⇒ the interpreter refuses step-into and steps over (native does the capture).
    /// </summary>
    public static string? FindUncapturableStatement(IList<TSqlStatement> body)
    {
        var finder = new Finder();
        foreach (var statement in body)
        {
            statement.Accept(finder);
            if (finder.Reason is not null)
            {
                return finder.Reason;
            }
        }

        return null;
    }

    private sealed class Finder : TSqlFragmentVisitor
    {
        public string? Reason { get; private set; }

        // A CTE- (or XMLNAMESPACES-) headed result-returning SELECT: the `WITH …` clause must precede
        // the whole statement, so `INSERT INTO #t WITH cte AS (…) SELECT …` is a syntax error (msg 156,
        // a session-killer). Only the captured (result-returning) form is a problem — an assignment
        // `WITH … SELECT @x = …` runs unwrapped and is fine.
        public override void Visit(SelectStatement node)
        {
            if (Reason is null
                && node.WithCtesAndXmlNamespaces is not null
                && ResultCaptureClassifier.IsResultReturning(node))
            {
                Reason = "a CTE/XMLNAMESPACES-headed SELECT (cannot be wrapped as INSERT … <SELECT>)";
            }
        }

        // Native forbids transaction control inside an INSERT…EXEC (a callee ROLLBACK raises msg 3915);
        // the debugger would instead run it and corrupt its own safety transaction. Refuse all of it.
        public override void Visit(RollbackTransactionStatement node) => Set("a ROLLBACK (native msg 3915 inside INSERT…EXEC)");
        public override void Visit(CommitTransactionStatement node) => Set("a COMMIT inside INSERT…EXEC");
        public override void Visit(BeginTransactionStatement node) => Set("a BEGIN TRANSACTION inside INSERT…EXEC");
        public override void Visit(SaveTransactionStatement node) => Set("a SAVE TRANSACTION inside INSERT…EXEC");

        // A nested INSERT…EXEC is a native error (msg 8164 "cannot be nested"); capturing the outer
        // must not let the inner silently succeed.
        public override void Visit(InsertStatement node)
        {
            if (node.InsertSpecification?.InsertSource is ExecuteInsertSource)
            {
                Set("a nested INSERT … EXEC (native msg 8164)");
            }
            else if (HasStreamingOutput(node.InsertSpecification?.OutputClause, node.InsertSpecification?.OutputIntoClause))
            {
                Set("an INSERT with a streaming OUTPUT clause");
            }
        }

        // DML with an OUTPUT clause but no OUTPUT INTO streams its OUTPUT rows to the client — native
        // captures THEM into the target; the debugger's classifier treats the DML as non-returning and
        // would lose them. Refuse.
        public override void Visit(UpdateStatement node)
        {
            if (HasStreamingOutput(node.UpdateSpecification?.OutputClause, node.UpdateSpecification?.OutputIntoClause))
                Set("an UPDATE with a streaming OUTPUT clause");
        }

        public override void Visit(DeleteStatement node)
        {
            if (HasStreamingOutput(node.DeleteSpecification?.OutputClause, node.DeleteSpecification?.OutputIntoClause))
                Set("a DELETE with a streaming OUTPUT clause");
        }

        public override void Visit(MergeStatement node)
        {
            if (HasStreamingOutput(node.MergeSpecification?.OutputClause, node.MergeSpecification?.OutputIntoClause))
                Set("a MERGE with a streaming OUTPUT clause");
        }

        // A cursor FETCH without INTO streams the fetched row to the client — captured natively, lost
        // by the classifier (a FetchCursorStatement is not result-returning).
        public override void Visit(FetchCursorStatement node)
        {
            if (node.IntoVariables is null || node.IntoVariables.Count == 0)
                Set("a FETCH without INTO (streams a row)");
        }

        private static bool HasStreamingOutput(OutputClause? output, OutputIntoClause? into)
            => output is not null && into is null;

        private void Set(string reason) => Reason ??= reason;
    }
}
