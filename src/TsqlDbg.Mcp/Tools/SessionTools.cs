using System.ComponentModel;
using ModelContextProtocol.Server;

namespace TsqlDbg.Mcp.Tools;

// DESIGN §24.4: session lifecycle + state tools. Each maps onto Core work the DAP adapter
// already drives (§24.0 add-only). SessionRegistry is a DI singleton; the request's
// CancellationToken is injected by the SDK. Every body runs through ToolGuard so an
// actionable failure (e.g. a default-deny refusal) reaches the agent as a real message.
[McpServerToolType]
public static class SessionTools
{
    [McpServerTool(Name = "start_session")]
    [Description("Open a T-SQL debug session against an ALLOWLISTED server (default-deny: an " +
                 "unknown server is refused — DESIGN §24.1). Runs to the first statement and returns the " +
                 "entry stop state. mode='script' (default) debugs the given script text/path; " +
                 "mode='procedure' debugs a stored procedure with args. Writes roll back on end unless " +
                 "commitMode='commit' AND the target allows writes. A SQL-auth password must come from the " +
                 "TSQLDBG_SQL_PASSWORD env var, never a tool argument.")]
    public static Task<StopState> StartSession(
        SessionRegistry registry,
        [Description("Connection + program to debug (server, database, mode, procedure/args or script).")] SessionArgs args,
        CancellationToken cancellationToken)
        => ToolGuard.Run(async () =>
        {
            var session = await registry.OpenAsync(args, cancellationToken).ConfigureAwait(false);
            return await session.GetEntryStateAsync().ConfigureAwait(false);
        });

    [McpServerTool(Name = "end_session")]
    [Description("End a debug session and tear it down. Rolls back the session transaction unless the " +
                 "session was opened with commitMode='commit' AND the target's allowWrites is true " +
                 "(DESIGN §24.1). Returns the final summary (return code, final state, committed?).")]
    public static Task<SessionSummary> EndSession(
        SessionRegistry registry,
        [Description("The sessionId from start_session.")] string sessionId)
        => ToolGuard.Run(() => registry.EndAsync(sessionId));

    [McpServerTool(Name = "list_sessions")]
    [Description("List all live debug sessions: id, server/database, mode, current line, idle age.")]
    public static IReadOnlyList<SessionListEntry> ListSessions(SessionRegistry registry)
        => registry.All.Select(s => new SessionListEntry(
            s.SessionId, s.Server, s.Database, s.Mode, s.CurrentLine, s.StateLabel,
            (int)(DateTime.UtcNow - s.LastActivityUtc).TotalSeconds)).ToList();

    [McpServerTool(Name = "get_state")]
    [Description("Get the current stop state of a session (where execution is paused, the call stack, " +
                 "the active error and transaction state) without stepping.")]
    public static Task<StopState> GetState(
        SessionRegistry registry,
        [Description("The sessionId from start_session.")] string sessionId)
        => ToolGuard.Run(() => registry.Get(sessionId).GetStateAsync());

    [McpServerTool(Name = "get_stack")]
    [Description("Get the current call stack (frames, innermost first) with each frame's module, line, " +
                 "full statement span, and the statement about to execute.")]
    public static Task<IReadOnlyList<FrameInfo>> GetStack(
        SessionRegistry registry,
        [Description("The sessionId from start_session.")] string sessionId)
        => ToolGuard.Run(async () =>
        {
            var state = await registry.Get(sessionId).GetStateAsync().ConfigureAwait(false);
            return state.Frames;
        });
}
