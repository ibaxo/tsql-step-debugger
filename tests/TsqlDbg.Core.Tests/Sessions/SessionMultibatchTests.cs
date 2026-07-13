using TsqlDbg.Core.Execution;
using TsqlDbg.Core.Sessions;
using TsqlDbg.Core.Tests.Fakes;

namespace TsqlDbg.Core.Tests.Sessions;

// DESIGN §5.4 (M8 — multi-batch GO script mode) LIFECYCLE, driven through the fake
// IStatementExecutor (§20.2). Covers batch transitions, per-batch scope reset, the
// session-#temp-promotion vs batch-local-teardown partition at a GO boundary, and
// IsCompleted timing. The §8.1 doom/detached boundary reconciliation and the
// batch-terminal-fault advance are M8 lane 1b (not exercised here).
public sealed class SessionMultibatchTests
{
    // A composed debuggee batch's SENT text carries the "__dbg_ctl" control-row alias
    // (ComposedBatchBuilder); the raw session calls (state-table DDL, BEGIN TRANSACTION,
    // ROLLBACK, the ExitBatch cleanup) do not. So respond by TEXT — an OK control row to
    // composed batches, empty to raw ones — which frees these tests from counting the
    // exact per-batch call sequence (order-independent; over-provision the queue).
    private static BatchResult Respond(string batchText) =>
        batchText.Contains("__dbg_ctl") ? OkControlRow() : Empty();

    private static BatchResult Empty() => new(Array.Empty<ResultSet>(), Array.Empty<string>());

    private static BatchResult OkControlRow()
    {
        var columns = new[] { "__dbg_ctl", "ok", "rc", "scope_identity", "trancount", "xact_state" };
        var values = new object?[] { 1, true, null, null, 1, 0 };
        return new BatchResult(new[] { new ResultSet(columns, new IReadOnlyList<object?>[] { values }) }, Array.Empty<string>());
    }

    private static FakeStatementExecutor ScriptedExecutor()
    {
        var executor = new FakeStatementExecutor();
        for (var i = 0; i < 64; i++)
        {
            executor.Then(Respond);
        }

        return executor;
    }

    private static Session ScriptSession(string script, FakeStatementExecutor executor) =>
        new(new SessionOptions("DEVSQL01", "SalesDb", LaunchMode.Script, null, null, script), executor);

    [Fact]
    public async Task RunToEndAsync_TwoBatches_SequencesBoth_AndRollsBackOnce()
    {
        const string script = "SELECT 1 AS a;\nGO\nSELECT 2 AS b;";
        var executor = ScriptedExecutor();

        await ScriptSession(script, executor).RunToEndAsync();

        // Each batch gets its OWN state table (fresh monotonic ordinal → structural scope
        // reset); the boundary drops batch 0's; both batches' statements execute; and the
        // session-level safety transaction rolls back exactly once at the end.
        Assert.Contains(executor.ReceivedBatches, b => b.StartsWith("CREATE TABLE #__dbg_s0"));
        Assert.Contains(executor.ReceivedBatches, b => b.StartsWith("CREATE TABLE #__dbg_s1"));
        Assert.Contains(executor.ReceivedBatches, b => b.Contains("#__dbg_s0") && b.Contains("DROP TABLE"));
        Assert.Contains(executor.ReceivedBatches, b => b.Contains("SELECT 1 AS a") && b.Contains("__dbg_ctl"));
        Assert.Contains(executor.ReceivedBatches, b => b.Contains("SELECT 2 AS b") && b.Contains("__dbg_ctl"));
        Assert.Single(executor.ReceivedBatches, b => b.StartsWith("BEGIN TRANSACTION"));
        Assert.Equal("IF @@TRANCOUNT > 0 ROLLBACK;", executor.ReceivedBatches[^1]);
    }

    [Fact]
    public async Task RunToEndAsync_BatchRedeclaresVariableAtDifferentType_EachBatchHasItsOwnCatalog()
    {
        // Batch 2 re-DECLAREs @x at a DIFFERENT type — legal across GO (fact 32a). It
        // composes cleanly (no DuplicateVariableException) precisely because each batch's
        // frame carries only its own catalog, and the two state tables carry different
        // column types — the GO scope reset reproduced structurally (§5.4).
        const string script = "DECLARE @x int = 1;\nGO\nDECLARE @x nvarchar(10) = N'two';";
        var executor = ScriptedExecutor();

        await ScriptSession(script, executor).RunToEndAsync();

        var s0Create = Assert.Single(executor.ReceivedBatches, b => b.StartsWith("CREATE TABLE #__dbg_s0"));
        var s1Create = Assert.Single(executor.ReceivedBatches, b => b.StartsWith("CREATE TABLE #__dbg_s1"));
        Assert.Contains("int NULL", s0Create);
        Assert.DoesNotContain("nvarchar", s0Create);
        Assert.Contains("nvarchar(10) NULL", s1Create);
        Assert.DoesNotContain("int NULL", s1Create);   // batch 1 never sees batch 0's @x int
    }

    [Fact]
    public async Task RunToEndAsync_SessionTempPromoted_TableVariableTornDown_AtBoundary()
    {
        // #acc is connection-scoped (survives GO → promoted to the session tier); @tv is
        // batch-local (its realization is dropped at the boundary). Batch 2 then reads
        // #acc, proving the #temp persisted across GO and the batch sequenced past it.
        const string script =
            "CREATE TABLE #acc (n int);\nDECLARE @tv TABLE (n int);\nGO\nSELECT n FROM #acc;";
        var executor = ScriptedExecutor();

        await ScriptSession(script, executor).RunToEndAsync();

        // The boundary cleanup drops batch 0's state table AND the @tv realization, but
        // NOT #acc (promoted, not torn down).
        var boundaryCleanup = Assert.Single(
            executor.ReceivedBatches, b => b.Contains("#__dbg_s0") && b.Contains("DROP TABLE"));
        Assert.Contains("#__dbgtv", boundaryCleanup);   // @tv realization torn down (batch-local, fact 2)
        Assert.DoesNotContain("#acc", boundaryCleanup);  // #acc survives the boundary (connection-scoped, fact 1)

        // Batch 2's SELECT ran against #acc — the #temp resolved through the promoted
        // session-tier registry entry.
        Assert.Contains(executor.ReceivedBatches, b => b.Contains("SELECT n FROM #acc") && b.Contains("__dbg_ctl"));
    }

    [Fact]
    public async Task StepAsync_IsCompleted_IsTrueOnlyAfterTheLastBatch()
    {
        const string script = "SELECT 1;\nGO\nSELECT 2;";
        var executor = ScriptedExecutor();
        var session = ScriptSession(script, executor);
        await session.InitializeAsync();

        Assert.False(session.IsCompleted);              // batch 0, first SU
        Assert.Equal(0, session.CurrentBatchIndex);
        Assert.Equal(2, session.BatchCount);

        await session.StepAsync();                      // executes SELECT 1 → crosses the GO boundary
        Assert.Equal(StepDisposition.BatchCompleted, session.LastStep.Disposition);
        Assert.Equal(1, session.CurrentBatchIndex);     // now positioned in batch 1
        Assert.False(session.IsCompleted);              // NOT complete — batch 1 still has a statement

        await session.StepAsync();                      // executes SELECT 2 (the last batch)
        Assert.True(session.IsCompleted);               // only NOW is the session complete

        await session.TeardownAsync();
    }

    [Fact]
    public async Task StepAsync_SingleBatchScript_HasOneBatch_AndNeverCrossesABoundary()
    {
        // The N = 1 case: a single-batch script behaves byte-identically to M4 — no
        // BatchCompleted, completion at the last SU.
        const string script = "SELECT 1;\nSELECT 2;";
        var executor = ScriptedExecutor();
        var session = ScriptSession(script, executor);
        await session.InitializeAsync();

        Assert.Equal(1, session.BatchCount);

        await session.StepAsync();
        Assert.NotEqual(StepDisposition.BatchCompleted, session.LastStep.Disposition);
        Assert.False(session.IsCompleted);

        await session.StepAsync();
        Assert.True(session.IsCompleted);               // single batch completes at its last SU

        await session.TeardownAsync();
    }
}
