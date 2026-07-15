using System.Text.Json;

namespace TsqlDbg.Mcp;

// DESIGN §24.8: the JSONL trace-file writer — the debugger's own debugger for agents (§19
// philosophy applied to Mode A). One header line, one line per statement-stop, one summary
// line. Records real data values by design (§24.1(6)); never the password (C27 — it never
// reaches this process except transiently via env, and is never part of any record here).
public static class TraceJson
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    public static string Header(string sessionId, string server, string database, string mode, string stepMode)
        => JsonSerializer.Serialize(new
        {
            kind = "header",
            session = new { sessionId, server, database, mode },
            stepMode,
            startedFrom = mode == "procedure" ? "trace_procedure" : "trace_script",
        }, Options);

    public static string Step(
        int seq, string? module, int line, string? statement,
        IReadOnlyDictionary<string, string>? variablesAfter, IReadOnlyDictionary<string, string>? tempRowCounts,
        IReadOnlyList<string> output, IReadOnlyList<ResultSetInfo> resultSets, ErrorInfo? error, IReadOnlyList<string> notes)
        => JsonSerializer.Serialize(new
        {
            kind = "step",
            seq,
            frame = new { module, line },
            statement,
            variablesAfter,
            tempRowCounts,
            output,
            resultSets,
            error,
            notes,
        }, Options);

    public static string Summary(int returnCode, string finalState, bool committed, int steps)
        => JsonSerializer.Serialize(new
        {
            kind = "summary",
            returnCode,
            finalState,
            committed,
            steps,
        }, Options);
}
