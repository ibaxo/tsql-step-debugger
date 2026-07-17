using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using TsqlDbg.Core.Sessions;
using TsqlDbg.Core.Targets;
using Xunit;

namespace TsqlDbg.Integration;

// docs/DESIGN.md §11.7 residual (e) / §21 C11 (A65 F2, re-reviewed 2026-07-17). A capture callee that
// DOOMS its transaction and still reaches a COMPLETED pop cannot materialize its stage: native raises
// 3930 (routed to a caller CATCH, target empty), but the debugger has no honest 3930 to route — the
// fact-22 forced rollback already destroyed the stage — and it will not SIMULATE one (§10.4/A14), so it
// TERMINATES the run at the call site. These tests pin that terminal: the F2 fix's contract is that a
// doomed capture is NEVER a silent success. They assert the DEBUGGER'S OWN behavior (a thrown terminal /
// a batch-terminal advance), NOT native fidelity — the debugger terminates where native routes-to-CATCH,
// so a fidelity comparison would diverge BY DESIGN (which is why the doom procs stay out of the p39
// fidelity harness).
public sealed class P39DoomedCaptureTerminalTests
{
    private const string ConnEnvVar = "TSQLDBG_TEST_CONN";

    // Procedure mode (single batch): a doomed capture-completion is CONNECTION-FATAL. RunToEndAsync
    // surfaces the terminal FrameFaulted(3930) as a SessionFaultException — NOT a completed run with the
    // caller's post-EXEC `SELECT v FROM #t` streamed (which is exactly the pre-F2 silent success).
    [SkippableFact]
    public async Task DoomedCapture_ProcedureMode_TerminatesWith3930_NotSilentSuccess()
    {
        var (server, database, conn) = RequireConnection();
        await DeployFixtureAsync(conn);

        var ex = await Assert.ThrowsAsync<SessionFaultException>(() =>
            RunProcAsync(server, database, "dbo.p39_doomcap_caller"));

        Assert.Contains("3930", ex.Message);
    }

    // Multi-batch script mode (finding 1): the SAME doomed capture-completion is BATCH-terminal, not
    // connection-fatal (§10.3/A35). Native force-rolls the doomed transaction at the GO seam (fact 22)
    // and runs the next batch; the debugger must too — arm _pendingBatchAdvance, don't brick the session.
    // Batch 2's marker proves the run CONTINUED rather than terminating the whole session (the bug the
    // re-review's finding 1 caught: an unconditional _broken there would swallow batch 2).
    [SkippableFact]
    public async Task DoomedCapture_MultiBatchScript_IsBatchTerminal_NextBatchRuns()
    {
        var (server, database, conn) = RequireConnection();
        await DeployFixtureAsync(conn);

        const string script =
            "CREATE TABLE #t (v int);\n" +
            "INSERT INTO #t (v) EXEC dbo.p39_doomcap_callee @Seed = 5;\n" +
            "GO\n" +
            "SELECT 424242 AS marker;\n";

        var target = new TargetEntry(server, "test", AllowWrites: false, Options: null);
        var options = new SessionOptions(
            server, database, LaunchMode.Script,
            Procedure: null, Args: null, ScriptText: script, Boost: false);

        // Completes WITHOUT throwing (the doomed capture was batch-terminal, the GO seam force-rolled and
        // the next batch ran) and batch 2 streamed its marker.
        var result = await SessionHost.RunAsync(options, target, stepKind: StepKind.Into);

        var markers = result.Execution.ResultSets
            .Where(rs => rs.Columns.Contains("marker"))
            .SelectMany(rs => rs.Rows)
            .Select(r => Convert.ToInt32(r[0]))
            .ToList();
        Assert.Contains(424242, markers);
    }

    private static async Task RunProcAsync(string server, string database, string procedure)
    {
        var target = new TargetEntry(server, "test", AllowWrites: false, Options: null);
        var options = new SessionOptions(
            server, database, LaunchMode.Procedure, procedure,
            new Dictionary<string, string> { ["@Seed"] = "5" }, ScriptText: null, Boost: false);
        await SessionHost.RunAsync(options, target, stepKind: StepKind.Into);
    }

    private static (string Server, string Database, string ConnectionString) RequireConnection()
    {
        var rawConnectionString = Environment.GetEnvironmentVariable(ConnEnvVar);
        Skip.If(
            string.IsNullOrWhiteSpace(rawConnectionString),
            $"{ConnEnvVar} is not set; skipping fidelity test (never fake a pass — CLAUDE.md).");

        var csb = new SqlConnectionStringBuilder(rawConnectionString);
        return (csb.DataSource, csb.InitialCatalog, rawConnectionString!);
    }

    private static async Task DeployFixtureAsync(string connectionString)
    {
        var sqlPath = Path.Combine(AppContext.BaseDirectory, "corpus", "p39_insert_exec_step_into.sql");
        var script = await File.ReadAllTextAsync(sqlPath);

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        foreach (var batch in Regex.Split(script, @"(?im)^\s*GO\s*$"))
        {
            if (string.IsNullOrWhiteSpace(batch)) continue;
            await using var command = connection.CreateCommand();
            command.CommandText = batch;
            await command.ExecuteNonQueryAsync();
        }
    }
}
