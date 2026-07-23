using System.Text.Json;
using System.Text.Json.Serialization;
using TsqlDbg.Core.Execution;

namespace TsqlDbg.Core.Sessions;

// DESIGN §24.8 (A73): the JSONL trace-file writer — one header line, one line per
// statement-stop, one summary line. Hoisted from the MCP host so the adapter's traceRun
// mode (§17/A73) writes the identical format (startedFrom:"launch", sessionId omitted).
// Records real data values by design (§24.1(6)); never the password (C27 — it never
// reaches either host's records).
//
// A70: null-valued fields are OMITTED from every line (token cost — the reader greps for
// presence: only steps in an active error context — the faulting step and its CATCH
// transit — contain "error"; only a step whose state was readable contains a variables
// field). The header's `variableCapture` declares which variables field the step lines
// carry. The step's frame carries the §24.5 frame ordinal as "id" (A70 review MED-1):
// {module, line} alone cannot distinguish a re-entered frame (recursion reuses both), and
// integrating "changed" deltas is only well-defined when keyed by frame identity.
//
// Nested objects (error, resultSets entries) deliberately serialize with their C#
// PascalCase property names — the pre-A73 MCP writer did (no naming policy on the
// records), and the on-disk format is a published contract external agents already parse.
public static class TraceJsonWriter
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Header(
        string? sessionId, string server, string database, string mode, string stepMode,
        string variableCapture, string startedFrom)
        => JsonSerializer.Serialize(new
        {
            kind = "header",
            session = new { sessionId, server, database, mode },
            stepMode,
            variableCapture,
            startedFrom,
        }, Options);

    // maxRows/displayValueChars: the host's §12.3/§15 caps (MaxConsoleRows /
    // DisplayValueChars), applied to the result-set projection exactly as the pre-A73
    // MCP ToResultSetInfo did.
    public static string Step(TraceStepRecord step, int maxRows, int displayValueChars)
        => JsonSerializer.Serialize(new
        {
            kind = "step",
            seq = step.Seq,
            // A74 rider (add-only): `depth` = the frame's stack depth at this statement
            // (0 = root). `id` is the §24.5 MONOTONIC ordinal — frame identity, never
            // depth (each GO batch / call gets the next number; ids are never reused).
            frame = new
            {
                id = step.FrameOrdinal,
                module = step.Module?.Display,
                line = step.Line,
                depth = step.Stack.Count > 0 ? step.Stack.Count - 1 : (int?)null,
            },
            statement = step.StatementText,
            variablesAfter = step.VariablesAfter,
            variablesChanged = step.VariablesChanged,
            tempRowCounts = step.TempRowCounts,
            output = step.Output,
            resultSets = step.ResultSets.Select(s => ProjectResultSet(s, maxRows, displayValueChars)).ToList(),
            error = step.Error,
            notes = step.Notes,
        }, Options);

    public static string Summary(
        int returnCode, IReadOnlyDictionary<string, string>? outputParams, IReadOnlyList<string> messages,
        string finalState, bool committed, int steps)
        => JsonSerializer.Serialize(new
        {
            kind = "summary",
            returnCode,
            outputParams,
            messages,
            finalState,
            committed,
            steps,
        }, Options);

    public static string FinalStateLabel(TraceFinalState state) => state switch
    {
        TraceFinalState.Completed => "completed",
        TraceFinalState.Faulted => "faulted",
        _ => "incomplete",
    };

    // §12.3/§24.5: columns + capped rows + truncated flag, cell values NULL-rendered and
    // capped at displayValueChars — the same projection the MCP stop states use.
    private static object ProjectResultSet(ResultSet set, int maxRows, int displayValueChars)
    {
        var rows = new List<IReadOnlyList<string>>();
        var truncated = false;
        for (var i = 0; i < set.Rows.Count; i++)
        {
            if (i >= maxRows)
            {
                truncated = true;
                break;
            }

            var row = set.Rows[i];
            rows.Add(row.Select(cell =>
            {
                var text = cell is null or DBNull ? "NULL" : cell.ToString() ?? "NULL";
                return text.Length > displayValueChars ? text[..displayValueChars] : text;
            }).ToList());
        }

        return new { Columns = set.Columns.ToList(), Rows = rows, Truncated = truncated };
    }
}
