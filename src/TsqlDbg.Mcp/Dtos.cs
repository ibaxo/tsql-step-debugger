namespace TsqlDbg.Mcp;

// DESIGN §24.5: the stop-state contract — the agent's flattened analog of DAP `stopped`
// + `stackTrace`, built from the same live Session state the adapter's StopSnapshot reads
// (§24.0: no new interpreter logic). Every session-scoped tool returns one of these unless
// noted (§24.4). Serialized to JSON by the MCP SDK; plain records serialize cleanly.

/// <summary>DESIGN §24.5: one debug stop as seen by an agent.</summary>
public sealed record StopState(
    string SessionId,
    string State,                       // "stopped" | "completed" | "faulted"
    string StopReason,                  // "entry" | "step" | "breakpoint" | "exception" | "goto" | "pause" | "completed"
    IReadOnlyList<FrameInfo> Frames,
    ErrorInfo? Error,
    TransactionInfo Transaction,
    IReadOnlyList<string> Output,       // server messages / PRINT produced by the step that led here
    IReadOnlyList<ResultSetInfo> ResultSets,
    bool AtImplicitReturn,              // §11.5/A54 — one inspection stop before the pop
    IReadOnlyList<string> Warnings);    // LaunchWarnings + logLevel:verbose diagnostic notes

/// <summary>DESIGN §24.5 frame — mirrors the DAP stackTrace frame (§18/§11).</summary>
public sealed record FrameInfo(
    int Id,                             // stable frame ordinal (§11.4); get_variables keys off it
    string Module,                      // f.Module.Display
    int Line,
    IReadOnlyList<int> Span,            // [startLine, startColumn, endLine, endColumn] full statement (A51)
    string? Statement,                  // byte-exact source slice of the SU about to execute
    BatchInfo? Batch);                  // §5.4 multi-batch script orientation; null unless IsScript multi-batch

/// <summary>DESIGN §5.4/§24.5: multi-batch script position (only on the script batch frame).</summary>
public sealed record BatchInfo(int Index, int Count, int Iteration, int Repeat);

/// <summary>DESIGN §24.5/§10: the active error at this stop, if any.</summary>
public sealed record ErrorInfo(
    int Number,
    int Severity,
    int State,
    int? Line,
    string? Procedure,
    string Message,
    string RoutedTo);                   // "catch" | "unhandled" | "faultSite" | "terminal"

/// <summary>DESIGN §24.5/§12.1 System-scope subset: the transaction/doom state.</summary>
public sealed record TransactionInfo(
    int Trancount,
    int XactState,
    bool Doomed,
    bool Detached,
    bool Broken);

/// <summary>DESIGN §24.5/§12.3: one result set, columns + capped rows (maxConsoleRows).</summary>
public sealed record ResultSetInfo(
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<string>> Rows,
    bool Truncated);

/// <summary>DESIGN §24.4 get_variables: one scope entry (§12.1–12.2).</summary>
public sealed record VariableInfo(
    string Name,
    string Value,
    string? Type);

/// <summary>DESIGN §24.4 set_breakpoints result (§13 mapping outcome).</summary>
public sealed record BreakpointInfo(
    int RequestedLine,
    bool Verified,
    int? MappedLine,
    string? Message);

/// <summary>DESIGN §24.4 end_session / trace summary (§20.3.1 projection subset).</summary>
public sealed record SessionSummary(
    string SessionId,
    int ReturnCode,
    IReadOnlyDictionary<string, string> OutputParams,
    IReadOnlyList<string> Messages,
    string FinalState,                  // "completed" | "faulted" | "torn-down"
    bool Committed,
    int Steps);

/// <summary>DESIGN §24.3/§24.8 trace tool result — summary + the trace-file path (the
/// full per-statement record is on disk, never inlined into the tool result — token cost).</summary>
public sealed record TraceResult(
    SessionSummary Summary,
    string TraceFilePath,
    string Note);

/// <summary>DESIGN §24.4 list_sessions row.</summary>
public sealed record SessionListEntry(
    string SessionId,
    string Server,
    string Database,
    string Mode,
    int? CurrentLine,
    string State,
    int IdleSeconds);
