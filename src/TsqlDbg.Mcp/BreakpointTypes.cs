namespace TsqlDbg.Mcp;

// DESIGN §24.6: a breakpoint location. An agent may not hold a module identity, so a
// location is a small tagged shape: script line, procedure name, or a real file path.
public sealed record BreakpointLocation(
    string? Kind = "script",     // "script" | "procedure" | "file"
    string? Name = null,         // procedure name, e.g. "dbo.uspChild" (kind=procedure)
    string? Path = null);        // real .sql path (kind=file)

// DESIGN §24.6/§13: one requested breakpoint. Condition is Core-backed exact (§13); a
// hitCondition is a minimal local parser (v1 — logpoints are a v2 follow-up, §24.6).
public sealed record BreakpointRequest(
    int Line,
    string? Condition = null,
    string? HitCondition = null);
