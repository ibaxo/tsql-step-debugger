using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace TsqlDbg.Core.Parsing;

// DESIGN §5.4 (A43): a `GO <n>` repeat-count separator captured by
// ScriptParser.BlankGoRepeatCounts. GoOffset = the file offset of the `GO` token, which
// sits AFTER the batch it terminates and BEFORE the next batch, so a count maps to its
// batch by offset; Count = the parsed repeat count (0 = the batch is skipped).
public readonly record struct GoRepeatMarker(int GoOffset, int Count);

// DESIGN §5.4 (A43): one non-empty physical batch that runs at least once, with its repeat
// count (>= 1). A `GO 0` batch is excluded upstream (native skips it). Repeat defaults to 1
// (plain `GO`, or no separator).
public sealed record ScriptBatch(IList<TSqlStatement> Statements, int Repeat);

// DESIGN §5.2: resolves frame 0's statement list from a parsed fragment, for either
// launch mode. Verified against 180.37.3 (docs/package-versions.md):
// ProcedureStatementBodyBase.StatementList.Statements is the body of a
// CREATE/ALTER PROCEDURE; TSqlBatch.Statements is a batch's top-level statements.
public static class FrameBodyResolver
{
    // "procedure" mode: the parsed fragment is OBJECT_DEFINITION(...) text, i.e. a
    // single CREATE (OR ALTER) PROCEDURE statement.
    public static IList<TSqlStatement> ResolveProcedureBody(TSqlFragment parsed)
    {
        if (parsed is not TSqlScript script)
        {
            throw new NotSupportedException(
                $"Expected a TSqlScript from OBJECT_DEFINITION text, got {parsed.GetType().Name}.");
        }

        var bodies = script.Batches
            .SelectMany(b => b.Statements)
            .OfType<ProcedureStatementBodyBase>()
            .ToList();

        if (bodies.Count != 1)
        {
            throw new NotSupportedException(
                $"Expected exactly one CREATE/ALTER PROCEDURE statement in the module definition, found {bodies.Count}.");
        }

        return bodies[0].StatementList.Statements;
    }

    // "script" mode (DESIGN §5.4): frame 0 is the active .sql file, which may contain N
    // GO batches. ScriptDom splits them into TSqlScript.Batches with file-absolute
    // line/offset on each batch's statements (Appendix C fact 32e), so multi-batch
    // splitting is free for plain GO. Returns one statement list per NON-EMPTY batch;
    // zero-statement batches (a trailing GO, GO\nGO, or a comment-only tail that
    // ScriptDom still lists) are dropped so the debugger never enters an empty scope.
    // Procedure mode and single-batch scripts are the N = 1 case. GO N (repeat count)
    // is not split here — ScriptDom raises parse error 46010 for it, surfaced at launch
    // (§5.4); this resolver only ever sees plain-GO batches.
    public static IReadOnlyList<IList<TSqlStatement>> ResolveScriptBatches(TSqlFragment parsed)
    {
        if (parsed is not TSqlScript script)
        {
            throw new NotSupportedException($"Expected a TSqlScript, got {parsed.GetType().Name}.");
        }

        var batches = new List<IList<TSqlStatement>>(script.Batches.Count);
        foreach (var batch in script.Batches)
        {
            if (batch.Statements.Count > 0)
            {
                batches.Add(batch.Statements);
            }
        }

        if (batches.Count == 0)
        {
            throw new NotSupportedException("The script contains no executable statements.");
        }

        return batches;
    }

    // DESIGN §5.4 (A43): the repeat-count-aware counterpart of ResolveScriptBatches. Takes the
    // parse of the count-BLANKED text (so every non-empty batch splits with byte-identical
    // offsets) plus the GO-repeat markers, maps each count to the batch it terminates BY
    // OFFSET (robust to empty batches / comment trivia, unlike index counting), drops empty
    // batches (as ResolveScriptBatches does), and returns one entry per non-empty batch with
    // its repeat count. A `GO 0` batch (Repeat 0) is EXCLUDED — native skips it entirely. The
    // caller materializes each entry Repeat times, so an iteration IS the next batch.
    public static IReadOnlyList<ScriptBatch> ResolveScriptBatchesWithRepeat(
        TSqlFragment parsed, IReadOnlyList<GoRepeatMarker> markers)
    {
        if (parsed is not TSqlScript script)
        {
            throw new NotSupportedException($"Expected a TSqlScript, got {parsed.GetType().Name}.");
        }

        var result = new List<ScriptBatch>(script.Batches.Count);
        for (var p = 0; p < script.Batches.Count; p++)
        {
            var batch = script.Batches[p];
            if (batch.Statements.Count == 0)
            {
                continue;   // trailing GO / GO\nGO / comment-only tail — never entered
            }

            // The terminating GO of batch p sits at an offset >= this batch's start and < the
            // next batch's start (the last batch's terminator, if any, is simply >= its start).
            var start = batch.StartOffset;
            var end = p + 1 < script.Batches.Count ? script.Batches[p + 1].StartOffset : int.MaxValue;
            var repeat = 1;
            foreach (var marker in markers)
            {
                if (marker.GoOffset >= start && marker.GoOffset < end)
                {
                    repeat = marker.Count;
                    break;
                }
            }

            if (repeat == 0)
            {
                continue;   // `GO 0` — native skips the batch; no iteration runs
            }

            result.Add(new ScriptBatch(batch.Statements, repeat));
        }

        if (result.Count == 0)
        {
            throw new NotSupportedException("The script contains no executable statements.");
        }

        return result;
    }
}
