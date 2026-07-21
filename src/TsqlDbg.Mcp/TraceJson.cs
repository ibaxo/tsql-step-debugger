using System.Text.Json;
using System.Text.Json.Serialization;

namespace TsqlDbg.Mcp;

// DESIGN §24.8: the JSONL trace-file writer — the debugger's own debugger for agents (§19
// philosophy applied to Mode A). One header line, one line per statement-stop, one summary
// line. Records real data values by design (§24.1(6)); never the password (C27 — it never
// reaches this process except transiently via env, and is never part of any record here).
//
// A70: null-valued fields are OMITTED from every line (token cost — the reader greps for
// presence: only steps in an active error context — the faulting step and its CATCH
// transit — contain "error"; only a step whose state was readable contains a variables
// field). The header's `variableCapture` declares which variables field the step lines
// carry: "changed" (default — only variables whose rendered value differs from the
// previous stop of the SAME frame; a frame's first stop is a full baseline) or "full"
// (the complete per-step snapshot, the pre-A70 shape). The step's frame carries the §24.5
// frame ordinal as "id" (A70 review MED-1): {module, line} alone cannot distinguish a
// re-entered frame (recursion reuses both), and integrating "changed" deltas is only
// well-defined when keyed by frame identity.
public static class TraceJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Header(string sessionId, string server, string database, string mode, string stepMode, string variableCapture)
        => JsonSerializer.Serialize(new
        {
            kind = "header",
            session = new { sessionId, server, database, mode },
            stepMode,
            variableCapture,
            startedFrom = mode == "procedure" ? "trace_procedure" : "trace_script",
        }, Options);

    public static string Step(
        int seq, int? frameId, string? module, int line, string? statement,
        IReadOnlyDictionary<string, string>? variablesAfter, IReadOnlyDictionary<string, string>? variablesChanged,
        IReadOnlyDictionary<string, string>? tempRowCounts,
        IReadOnlyList<string> output, IReadOnlyList<ResultSetInfo> resultSets, ErrorInfo? error, IReadOnlyList<string> notes)
        => JsonSerializer.Serialize(new
        {
            kind = "step",
            seq,
            frame = new { id = frameId, module, line },
            statement,
            variablesAfter,
            variablesChanged,
            tempRowCounts,
            output,
            resultSets,
            error,
            notes,
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
}
