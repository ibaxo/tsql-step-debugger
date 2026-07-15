using System.ComponentModel;
using ModelContextProtocol.Server;

namespace TsqlDbg.Mcp.Tools;

// DESIGN §24.4/§12: inspection — variables, temp rows, evaluate, set variable.
[McpServerToolType]
public static class InspectionTools
{
    [McpServerTool(Name = "get_variables")]
    [Description("Read a scope for a frame. scope: 'locals' (the frame's @variables), 'system' " +
                 "(@@TRANCOUNT / XACT_STATE / session options), 'temp' (temp tables & table variables with " +
                 "row counts), 'errorContext' (ERROR_NUMBER/MESSAGE/… inside a CATCH). frameId comes from a " +
                 "stop state's frame.id.")]
    public static Task<IReadOnlyList<VariableInfo>> GetVariables(
        SessionRegistry registry,
        [Description("The sessionId from start_session.")] string sessionId,
        [Description("The frame id (from a stop state's frames[].id).")] int frameId,
        [Description("'locals' | 'system' | 'temp' | 'errorContext'.")] string scope,
        CancellationToken cancellationToken)
        => ToolGuard.Run(() => registry.Get(sessionId).GetVariablesAsync(frameId, scope, cancellationToken));

    [McpServerTool(Name = "get_temp_rows")]
    [Description("Read one page of a temp table / table variable's rows. 'name' is the physical name shown " +
                 "by get_variables(scope='temp'); 'page' is the 0-based page index (tempTablePageSize rows).")]
    public static Task<ResultSetInfo?> GetTempRows(
        SessionRegistry registry,
        [Description("The sessionId from start_session.")] string sessionId,
        [Description("The temp object's physical name (from get_variables temp scope).")] string name,
        [Description("0-based page index. Default 0.")] int page,
        CancellationToken cancellationToken)
        => ToolGuard.Run(() => registry.Get(sessionId).GetTempRowsAsync(name, page < 0 ? 0 : page, cancellationToken));

    [McpServerTool(Name = "evaluate")]
    [Description("Evaluate a T-SQL expression or statement in the session (Debug Console / REPL, DESIGN " +
                 "§12.3), in the given frame (default: the top frame). Reads always work; writes are refused " +
                 "unless the session was opened with allowConsoleWrites=true AND the target allows writes. " +
                 "Returns the rendered result text.")]
    public static Task<string> Evaluate(
        SessionRegistry registry,
        [Description("The sessionId from start_session.")] string sessionId,
        [Description("The T-SQL to evaluate, e.g. 'SELECT @x + 1' or 'SELECT * FROM #stage'.")] string expression,
        [Description("The frame id to evaluate in (default: the top frame).")] int? frameId,
        CancellationToken cancellationToken)
        => ToolGuard.Run(() => registry.Get(sessionId).EvaluateAsync(frameId, expression, cancellationToken));

    [McpServerTool(Name = "set_variable")]
    [Description("Set a local @variable's value in a frame (DESIGN §8.3). value is a T-SQL literal, e.g. " +
                 "'42' or \"N'FULL'\". Returns the applied value.")]
    public static Task<string> SetVariable(
        SessionRegistry registry,
        [Description("The sessionId from start_session.")] string sessionId,
        [Description("The frame id (from a stop state's frames[].id).")] int frameId,
        [Description("The @variable name, e.g. '@OrderId'.")] string name,
        [Description("A T-SQL literal value, e.g. '42' or \"N'FULL'\".")] string value,
        CancellationToken cancellationToken)
        => ToolGuard.Run(() => registry.Get(sessionId).SetVariableAsync(frameId, name, value, cancellationToken));
}
