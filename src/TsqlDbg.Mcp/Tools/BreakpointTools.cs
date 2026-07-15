using System.ComponentModel;
using ModelContextProtocol.Server;

namespace TsqlDbg.Mcp.Tools;

// DESIGN §24.4/§24.6/§13: breakpoints + exception filters.
[McpServerToolType]
public static class BreakpointTools
{
    [McpServerTool(Name = "set_breakpoints")]
    [Description("Set (replace) the breakpoints for one location. location.kind: 'script' (a line in the " +
                 "debugged script), 'procedure' (location.name = 'dbo.uspChild'), or 'file' (location.path). " +
                 "Each breakpoint may carry a T-SQL 'condition' (exact) and a 'hitCondition' (e.g. '>=5', " +
                 "'%3'). Returns each requested line's verified/mapped result.")]
    public static Task<IReadOnlyList<BreakpointInfo>> SetBreakpoints(
        SessionRegistry registry,
        [Description("The sessionId from start_session.")] string sessionId,
        [Description("Where the breakpoints live: script line, procedure name, or file path.")] BreakpointLocation location,
        [Description("The breakpoints to set (line + optional condition/hitCondition).")] BreakpointRequest[] breakpoints,
        CancellationToken cancellationToken)
        => ToolGuard.Run(() => registry.Get(sessionId).SetBreakpointsAsync(location, breakpoints, cancellationToken));

    [McpServerTool(Name = "clear_breakpoints")]
    [Description("Clear all breakpoints for one location (same location shape as set_breakpoints).")]
    public static Task ClearBreakpoints(
        SessionRegistry registry,
        [Description("The sessionId from start_session.")] string sessionId,
        [Description("The location whose breakpoints to clear.")] BreakpointLocation location,
        CancellationToken cancellationToken)
        => ToolGuard.Run(() => registry.Get(sessionId).ClearBreakpointsAsync(location, cancellationToken));

    [McpServerTool(Name = "set_exception_filters")]
    [Description("Configure where 'continue' and traces stop on T-SQL errors (DESIGN §10.6): 'all' (stop at " +
                 "every fault site), 'caught' (stop on errors routed to a CATCH), 'unhandled' (default on).")]
    public static Task SetExceptionFilters(
        SessionRegistry registry,
        [Description("The sessionId from start_session.")] string sessionId,
        [Description("Stop at every fault site before routing. Default false.")] bool all,
        [Description("Stop on errors routed to a CATCH. Default false.")] bool caught,
        [Description("Stop on unhandled errors. Default true.")] bool unhandled,
        CancellationToken cancellationToken)
        => ToolGuard.Run(() => registry.Get(sessionId).SetExceptionFiltersAsync(all, caught, unhandled, cancellationToken));
}
