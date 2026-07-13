using System.Diagnostics;
using Microsoft.Data.SqlClient;
using TsqlDbg.Core.Execution;
using Xunit;

namespace TsqlDbg.Integration;

// Ruling on docs/archive/reviews/m6-boosted-attention-triage-fable.md §5 (Ivan, 2026-07-07,
// candidate (d)): the executor runs commands on the driver's SYNC path so that a
// §10.5 pause's SqlCommand.Cancel genuinely interrupts a compute-bound batch (engine
// fact 30a) instead of blocking until its natural end (fact 30b, the async path).
// This is the triage's raw probe made permanent: the REAL B4 marker shape, cancelled
// mid-loop, must die promptly — if it starts taking ≈ the batch's natural runtime
// again, the executor's driver path regressed to async (or the driver changed
// underneath fact 30) — triage per CLAUDE.md escalation trigger (a).
public sealed class SqlExecutorAttentionLiveTests
{
    private const string ConnEnvVar = "TSQLDBG_TEST_CONN";

    // The composed boosted batch's per-iteration shape (escalation doc §3): SET +
    // guarded shadow UPDATE + guarded boost-position UPDATE, NOCOUNT ON (C5).
    // 300k iterations ≈ 60 s natural (fact 30e: ~210 µs/iteration) — the 2 s cancel
    // has an unambiguous ~58 s of runway to prove promptness against.
    private const string MarkerShapeLoop = """
        SET NOCOUNT ON;
        CREATE TABLE #__dbg_s0 (i bigint);  INSERT #__dbg_s0 VALUES (0);
        CREATE TABLE #__dbg_boost (pos int); INSERT #__dbg_boost VALUES (-1);
        DECLARE @i bigint = 0;
        WHILE @i < 300000
        BEGIN
            SET @i = @i + 1;
            IF XACT_STATE() <> -1 AND OBJECT_ID('tempdb..#__dbg_s0') IS NOT NULL UPDATE #__dbg_s0 SET [i]=@i;
            IF XACT_STATE() <> -1 UPDATE #__dbg_boost SET pos = 0;
        END
        SELECT @i AS done;
        """;

    [SkippableFact]
    public async Task CancelMidComputeBoundMarkerLoop_InterruptsPromptly_AndLeavesTheConnectionHealthy()
    {
        var connString = Environment.GetEnvironmentVariable(ConnEnvVar);
        Skip.If(string.IsNullOrWhiteSpace(connString), $"{ConnEnvVar} is not set; skipping live probe (never fake a pass — CLAUDE.md).");

        await using var connection = new SqlConnection(connString);
        await connection.OpenAsync();
        await using var executor = new SqlStatementExecutor(connection, commandTimeoutSeconds: 300);

        using var pause = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var sw = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<StatementExecutionException>(
            () => executor.ExecuteAsync(MarkerShapeLoop, pause.Token));
        sw.Stop();

        // §10.5 attention shape: Number 0, below the connection-fatal severity band —
        // exactly what HandleExecutorFailureAsync/HandleBoostedBatchDeathAsync classify.
        Assert.Equal(0, ex.Number);
        Assert.True(ex.SeverityClass < 20, $"severity {ex.SeverityClass} would classify connection-fatal");

        // Prompt = seconds of the cancel, nowhere near the ~60 s natural runtime. The
        // 10 s bound is deliberately loose (CI machines) while still ~6x below natural.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10),
            $"attention took {sw.Elapsed.TotalSeconds:F1}s — the sync-path promptness of fact 30a has regressed");

        // Fact 28: the session survives an attention — the SAME connection keeps working
        // (B7's recovery read depends on this), and the marker write that completed
        // before the cancel is still visible in the session-scoped #temp.
        var probe = await executor.ExecuteAsync("SELECT pos FROM #__dbg_boost; SELECT i FROM #__dbg_s0;");
        Assert.Equal(0, Convert.ToInt32(probe.ResultSets[0].Rows[0][0]));
        Assert.True(Convert.ToInt64(probe.ResultSets[1].Rows[0][0]) > 0, "the pre-cancel marker state should have persisted");
    }
}
