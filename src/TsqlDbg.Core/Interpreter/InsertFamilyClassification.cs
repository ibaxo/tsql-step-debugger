// DESIGN §7.4 (A26, D1) — the SCOPE_IDENTITY() chain-sync classifier.
// Engine truth: fact 31b (docs/engine-facts.md). A small pure sibling of SuClassifier.
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace TsqlDbg.Core.Interpreter;

/// <summary>
/// Classifies a statement as <b>insert-family</b> for R6 SCOPE_IDENTITY() chain-sync
/// (DESIGN §7.4 / A26). A statement is insert-family iff it natively MOVES the session's
/// SCOPE_IDENTITY() chain — the only statement class that does (fact 26d). While the
/// session's real chain is poisoned (a frame pop or doom desynchronized it from the
/// current frame's native chain), a completed insert-family statement re-synchronizes
/// both chains to its own result, so the session clears the poison flag and takes the
/// capture; every other statement leaves both chains untouched and the capture is skipped.
///
/// Membership is <b>statement-class-based, not runtime-conditional</b> (fact 31b): a MERGE
/// with an INSERT action clause moves the chain even when it inserts ZERO rows, exactly
/// like fact 26d's zero-row plain insert. Members (chain-movers):
/// <list type="bullet">
///   <item><see cref="InsertStatement"/> — every source: VALUES, SELECT, and INSERT…EXEC.</item>
///   <item><see cref="SelectStatement"/> with an INTO clause — SELECT…INTO creates + inserts.</item>
///   <item><see cref="MergeStatement"/> with at least one <see cref="InsertMergeAction"/> clause.</item>
/// </list>
/// <b>NEUTRAL</b> (deliberately EXCLUDED — fact 31b): <c>UPDATE</c>/<c>DELETE</c> with or
/// without <c>OUTPUT</c>/<c>OUTPUT INTO</c> (the output-target insert does NOT move the
/// caller's chain), and update/delete-only MERGE. Excluding a non-mover is the safe
/// direction: had they been treated as movers, a poisoned-state UPDATE…OUTPUT INTO would
/// falsely clear the flag and take a stale capture (the exact bug fact 31b prevents).
/// </summary>
public static class InsertFamilyClassifier
{
    public static bool IsInsertFamily(TSqlFragment? fragment) => fragment switch
    {
        InsertStatement => true,
        SelectStatement { Into: not null } => true,
        MergeStatement merge => HasInsertAction(merge),
        _ => false,
    };

    private static bool HasInsertAction(MergeStatement merge)
    {
        var clauses = merge.MergeSpecification?.ActionClauses;
        if (clauses is null)
        {
            return false;
        }

        foreach (var clause in clauses)
        {
            if (clause.Action is InsertMergeAction)
            {
                return true;
            }
        }

        return false;
    }
}
