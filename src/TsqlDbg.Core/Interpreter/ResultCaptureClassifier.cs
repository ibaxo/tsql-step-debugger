// DESIGN §11.1 (C11 / A64) — the result-capture classifier.
// When the debugger steps INTO an `INSERT <target> EXEC proc`, the callee's RESULT-RETURNING
// statements must be captured into the INSERT target (native "result capture" semantics) rather
// than streamed to the client. This pure sibling of SuClassifier decides which callee statements
// stream a client result set (and so must be INSERT-wrapped) versus which run unwrapped.
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace TsqlDbg.Core.Interpreter;

public static class ResultCaptureClassifier
{
    /// <summary>
    /// True iff <paramref name="fragment"/>, executed as its own statement, streams a client result
    /// set that a wrapping <c>INSERT … EXEC</c> would capture. That is exactly:
    /// <list type="bullet">
    ///   <item>a <see cref="SelectStatement"/> whose query returns rows to the client — i.e. NOT a
    ///     <c>SELECT … INTO</c> (creates a table, no client rows) and NOT a variable-assignment
    ///     <c>SELECT @x = …</c> (a <see cref="SelectSetVariable"/> element, no client rows);</item>
    ///   <item>a stepped-over <see cref="ExecuteStatement"/> (a nested <c>EXEC proc</c> / <c>EXEC(@s)</c>)
    ///     — its result sets pass through and are captured too. (When such an EXEC is stepped INTO
    ///     it is refused → step-over, so it always reaches here as one composed batch.)</item>
    /// </list>
    /// Everything else — SET, DECLARE, IF/WHILE, DML, a nested <c>INSERT … EXEC</c> that captures into
    /// its OWN target, PRINT — returns no client rows and runs unwrapped.
    /// </summary>
    public static bool IsResultReturning(TSqlFragment? fragment) => fragment switch
    {
        SelectStatement select => IsResultReturningSelect(select),
        ExecuteStatement => true,
        _ => false,
    };

    private static bool IsResultReturningSelect(SelectStatement select)
    {
        if (select.Into is not null)
        {
            return false;                       // SELECT … INTO #t — creates a table, streams nothing
        }

        // A variable-assignment SELECT (`SELECT @x = col`) has SelectSetVariable elements and returns
        // no client rows; a mixed assign/non-assign SELECT is a native error, so a single
        // SelectSetVariable is enough to classify the whole statement as non-returning. A compound
        // (UNION/…) query cannot assign, so it always returns rows.
        if (select.QueryExpression is QuerySpecification { SelectElements: { } elements })
        {
            foreach (var element in elements)
            {
                if (element is SelectSetVariable)
                {
                    return false;
                }
            }
        }

        return true;
    }
}
