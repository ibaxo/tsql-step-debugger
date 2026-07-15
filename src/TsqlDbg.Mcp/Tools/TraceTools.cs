using System.ComponentModel;
using ModelContextProtocol.Server;

namespace TsqlDbg.Mcp.Tools;

// DESIGN §24.3/§24.8: Mode A — the one-shot trace. Opens a session, drives it to completion
// with the auto-stepper capturing a per-statement record to a JSONL file, tears down
// (rollback unless commit-gated), and returns a compact summary + the file path. The agent
// reads the file and reasons offline — one tool round trip for a whole run. This is the
// recommended default path (§24.3): most "why does this do X" questions are answered here.
[McpServerToolType]
public static class TraceTools
{
    [McpServerTool(Name = "trace_procedure")]
    [Description("Trace a stored procedure to completion and return a summary plus a JSONL trace-file path " +
                 "(one line per statement: the statement, variables after it, output, result sets, errors). " +
                 "The recommended FIRST tool for investigating a procedure — cheap, one round trip. Rolls " +
                 "back unless commit-gated (DESIGN §24.1). stepMode 'over' (default) or 'into'.")]
    public static Task<TraceResult> TraceProcedure(
        SessionRegistry registry,
        [Description("Connection + procedure to trace (server, database, procedure, args).")] SessionArgs args,
        [Description("'over' (default) or 'into' (step into nested EXEC/dynamic SQL).")] string stepMode,
        [Description("Also capture temp-table row counts per statement (extra cost). Default false.")] bool captureTempRowCounts,
        CancellationToken cancellationToken)
        => ToolGuard.Run(() => RunAsync(registry, args with { Mode = "procedure" }, stepMode, captureTempRowCounts, cancellationToken));

    [McpServerTool(Name = "trace_script")]
    [Description("Trace a T-SQL script (batches separated by GO) to completion and return a summary plus a " +
                 "JSONL trace-file path (one line per statement). The recommended FIRST tool for " +
                 "investigating a script. Rolls back unless commit-gated (DESIGN §24.1). stepMode 'over' " +
                 "(default) or 'into'.")]
    public static Task<TraceResult> TraceScript(
        SessionRegistry registry,
        [Description("Connection + script to trace (server, database, script text or scriptPath).")] SessionArgs args,
        [Description("'over' (default) or 'into' (step into nested EXEC/dynamic SQL).")] string stepMode,
        [Description("Also capture temp-table row counts per statement (extra cost). Default false.")] bool captureTempRowCounts,
        CancellationToken cancellationToken)
        => ToolGuard.Run(() => RunAsync(registry, args with { Mode = "script" }, stepMode, captureTempRowCounts, cancellationToken));

    private static async Task<TraceResult> RunAsync(
        SessionRegistry registry, SessionArgs args, string stepMode, bool captureTempRowCounts, CancellationToken ct)
    {
        var session = await registry.OpenAsync(args, ct).ConfigureAwait(false);
        try
        {
            var mode = string.IsNullOrWhiteSpace(stepMode) ? "over" : stepMode;
            var (summary, filePath, steps) = await session
                .RunTraceAsync(registry.Config.TraceOutputDirectory, mode, captureTempRowCounts, ct)
                .ConfigureAwait(false);

            var note = $"Traced {steps} statement(s). The full per-statement record is at the path below " +
                       $"(JSONL, one line per statement) — read it to investigate. finalState={summary.FinalState}, " +
                       $"committed={summary.Committed}.";
            return new TraceResult(summary, filePath, note);
        }
        catch
        {
            // M11 re-review (N1): RunTraceAsync tears the LiveSession down itself on the SUCCESS
            // path — but if it throws BEFORE that (e.g. a bad trace dir on Directory.CreateDirectory,
            // or an unexpected mid-loop fault), the open connection + BEGIN TRAN would otherwise be
            // orphaned: removed from the registry below yet never rolled back, and unreachable by the
            // idle sweep. Dispose here (unconditional rollback) so the §24.1(5) C3 mitigation has no
            // hole. DisposeAsync is idempotent, so the success path (already torn down) is unaffected.
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            // Deregister so the (now-torn-down) session never lingers in the live set.
            registry.Remove(session.SessionId);
        }
    }
}
