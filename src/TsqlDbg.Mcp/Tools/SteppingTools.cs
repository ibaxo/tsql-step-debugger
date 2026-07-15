using System.ComponentModel;
using ModelContextProtocol.Server;

namespace TsqlDbg.Mcp.Tools;

// DESIGN §24.4: stepping + goto. Every tool returns the resulting stop state (§24.5) so the
// agent never needs a separate "where am I" call.
[McpServerToolType]
public static class SteppingTools
{
    [McpServerTool(Name = "step")]
    [Description("Execute one step and return the new stop state. granularity: 'over' (default) runs the " +
                 "current statement, stepping over any EXEC; 'in' steps into an eligible EXEC / dynamic SQL; " +
                 "'out' runs until the current frame returns. Output and result sets produced by the step " +
                 "are included in the stop state.")]
    public static Task<StopState> Step(
        SessionRegistry registry,
        [Description("The sessionId from start_session.")] string sessionId,
        [Description("'over' | 'in' | 'out'. Default 'over'.")] string granularity,
        CancellationToken cancellationToken)
        => ToolGuard.Run(() => registry.Get(sessionId).StepAsync(
            string.IsNullOrWhiteSpace(granularity) ? "over" : granularity, cancellationToken));

    [McpServerTool(Name = "continue")]
    [Description("Run until the next breakpoint, a fault the exception filters stop on, a COMMIT (stopped " +
                 "once for confirmation), or completion. Returns the resulting stop state, including all " +
                 "output/result sets produced along the way.")]
    public static Task<StopState> Continue(
        SessionRegistry registry,
        [Description("The sessionId from start_session.")] string sessionId,
        CancellationToken cancellationToken)
        => ToolGuard.Run(() => registry.Get(sessionId).ContinueAsync(cancellationToken));

    [McpServerTool(Name = "goto")]
    [Description("Move the cursor to a line WITHOUT executing the skipped statements (DESIGN §13). State " +
                 "does not change. Use to skip a COMMIT or jump past code. Returns the new stop state.")]
    public static Task<StopState> Goto(
        SessionRegistry registry,
        [Description("The sessionId from start_session.")] string sessionId,
        [Description("The 1-based source line to jump to (in the current frame).")] int line,
        CancellationToken cancellationToken)
        => ToolGuard.Run(() => registry.Get(sessionId).GotoAsync(line, cancellationToken));
}
