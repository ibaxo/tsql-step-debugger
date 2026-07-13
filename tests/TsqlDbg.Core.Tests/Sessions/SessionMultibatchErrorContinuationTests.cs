using TsqlDbg.Core.Execution;
using TsqlDbg.Core.Sessions;
using TsqlDbg.Core.Tests.Fakes;

namespace TsqlDbg.Core.Tests.Sessions;

// DESIGN §5.4 / §10.3 / §10.4 (A35) — M8 lane 1b: multi-batch (GO) script ERROR
// CONTINUATION + the §8.1 doom-boundary reconciliation, driven through the fake
// IStatementExecutor (§20.2). Covers the §8.3 classification (batch-terminal advance vs
// connection-fatal end), the doom-boundary force-rollback (doom → detached), the
// detached-carry-forward, and the #temp-marked-dead reconciliation at both tiers. The
// live acceptance test is P27MultibatchDoomBoundaryFidelityTests.
public sealed class SessionMultibatchErrorContinuationTests
{
    // Composed debuggee batches carry the "__dbg_ctl" alias (ComposedBatchBuilder); raw
    // session calls (state DDL, BEGIN TRAN, the boundary force-rollback, ExitBatch cleanup)
    // do not. Respond by TEXT so the tests never count the exact per-batch call sequence.
    private static BatchResult Empty() => new(Array.Empty<ResultSet>(), Array.Empty<string>());

    private static BatchResult ControlRow(bool ok, int trancount, int xactState)
    {
        var columns = new[] { "__dbg_ctl", "ok", "rc", "scope_identity", "trancount", "xact_state" };
        var values = new object?[] { 1, ok, null, null, trancount, xactState };
        return new BatchResult(new[] { new ResultSet(columns, new IReadOnlyList<object?>[] { values }) }, Array.Empty<string>());
    }

    private static BatchResult Ok() => ControlRow(ok: true, trancount: 1, xactState: 0);

    private static FakeStatementExecutor ExecutorWith(Func<string, BatchResult> responder)
    {
        var executor = new FakeStatementExecutor();
        for (var i = 0; i < 128; i++)
        {
            executor.Then(responder);
        }

        return executor;
    }

    private static Session ScriptSession(string script, FakeStatementExecutor executor) =>
        new(new SessionOptions("DEVSQL01", "SalesDb", LaunchMode.Script, null, null, script), executor);

    // §8.2/§8.3: a BATCH-TERMINAL fault (here a compile-class 208 with no control row and no
    // armed TRY — the §10.1 propagate class) terminates the current batch but the client
    // CONTINUES to the next batch (sqlcmd/SSMS default). The session is NOT broken.
    [Fact]
    public async Task RunToEnd_BatchTerminalCompileFault_AdvancesToNextBatch_SessionNotBroken()
    {
        const string script = "SELECT 1 AS faulthere;\nGO\nSELECT 2 AS afterfault;";
        var executor = ExecutorWith(text =>
            !text.Contains("__dbg_ctl") ? Empty()
            : text.Contains("faulthere") ? throw new StatementExecutionException("Invalid object name.", 16, 208)
            : Ok());
        var session = ScriptSession(script, executor);

        await session.RunToEndAsync();      // must NOT throw — the fault advances, not ends

        Assert.False(session.IsBroken);     // batch-terminal, not connection-fatal
        Assert.Equal(1, session.CurrentBatchIndex);   // advanced past the faulted batch
        // Batch 2 actually ran (its composed batch was sent), proving continuation.
        Assert.Contains(executor.ReceivedBatches, b => b.Contains("afterfault") && b.Contains("__dbg_ctl"));
    }

    // §8.3: a connection-fatal outcome (severity ≥ 20) is session-fatal even in multi-batch
    // mode — a dead connection cannot run the next batch. The session ENDS; batch 2 never runs.
    [Fact]
    public async Task RunToEnd_ConnectionFatalSeverity20_EndsSession_DoesNotAdvance()
    {
        const string script = "SELECT 1 AS fatalhere;\nGO\nSELECT 2 AS afterfatal;";
        var executor = ExecutorWith(text =>
            !text.Contains("__dbg_ctl") ? Empty()
            : text.Contains("fatalhere") ? throw new StatementExecutionException("A severe error occurred.", 20, 5000)
            : Ok());
        var session = ScriptSession(script, executor);

        await Assert.ThrowsAsync<SessionFaultException>(() => session.RunToEndAsync());

        Assert.True(session.IsBroken);
        Assert.DoesNotContain(executor.ReceivedBatches, b => b.Contains("afterfatal"));   // batch 2 never ran
    }

    // §8.3: a genuinely broken connection (ConnectionBroken, even at severity < 20) is also
    // session-fatal — it must NOT advance onto a dead connection.
    [Fact]
    public async Task RunToEnd_BrokenConnection_EndsSession_DoesNotAdvance()
    {
        const string script = "SELECT 1 AS fatalhere;\nGO\nSELECT 2 AS afterfatal;";
        var executor = ExecutorWith(text =>
            !text.Contains("__dbg_ctl") ? Empty()
            : text.Contains("fatalhere")
                ? throw new StatementExecutionException("Transport-level error.", 16, 233, connectionBroken: true)
            : Ok());
        var session = ScriptSession(script, executor);

        await Assert.ThrowsAsync<SessionFaultException>(() => session.RunToEndAsync());

        Assert.True(session.IsBroken);
        Assert.DoesNotContain(executor.ReceivedBatches, b => b.Contains("afterfatal"));
    }

    // §8.1: a DOOMED batch (XACT_STATE() = -1, unhandled) cannot cross a GO boundary — the
    // boundary force-rolls the transaction to trancount 0 (fact 22), the session goes
    // doom → detached, and a #temp created INSIDE the doomed transaction is marked dead
    // (not promoted). The batch-terminal fault still advances (sqlcmd default).
    [Fact]
    public async Task StepAsync_DoomedBatchReachesBoundary_ForceRollsBack_DetachesAndDropsDoomedTemp()
    {
        const string script = "CREATE TABLE #d (n int);\nSELECT 1 AS doomhere;\nGO\nSELECT 2 AS afterdoom;";
        var executor = ExecutorWith(text =>
            !text.Contains("__dbg_ctl") ? Empty()
            : text.Contains("doomhere") ? ControlRow(ok: false, trancount: 1, xactState: -1)
            : Ok());
        var session = ScriptSession(script, executor);
        await session.InitializeAsync();

        await session.StepAsync();          // CREATE #d — registered inside the (safety) transaction
        Assert.Contains(session.Frames[0].TempObjects.All,
            e => string.Equals(e.OriginalName, "#d", StringComparison.OrdinalIgnoreCase) && !e.IsDead);

        await session.StepAsync();          // SELECT doomhere → doom → batch-terminal fault at the fault site
        Assert.Equal(StepDisposition.FrameFaulted, session.LastStep.Disposition);
        Assert.True(session.IsDoomed);
        Assert.True(session.PendingBatchAdvance);   // fault-site stop published; the next step advances
        Assert.False(session.IsBroken);             // NOT session-fatal — the batch is terminal, not the session

        await session.StepAsync();          // cross the GO boundary: §8.1 doom reconciliation
        Assert.Equal(StepDisposition.BatchCompleted, session.LastStep.Disposition);
        Assert.Equal(1, session.CurrentBatchIndex);
        Assert.False(session.IsDoomed);                 // doom resolved at the boundary
        Assert.True(session.IsTransactionDetached);     // doom → detached (fact 22)
        Assert.False(session.PendingBatchAdvance);      // consumed
        // The engine force-rollback was issued (emulating fact 22 at the separator).
        Assert.Contains(executor.ReceivedBatches, b => b.Contains("ROLLBACK TRANSACTION") && b.Contains("@@TRANCOUNT"));
        // #d was created inside the doomed transaction, so it is NOT promoted across GO — no
        // LIVE session-tier entry survives (matching native: the forced rollback destroyed it).
        Assert.DoesNotContain(session.SessionTempObjects,
            e => string.Equals(e.OriginalName, "#d", StringComparison.OrdinalIgnoreCase) && !e.IsDead);

        await session.TeardownAsync();
    }

    // §8.1: a session that is merely DETACHED (a debuggee ROLLBACK earlier, not doomed)
    // carries the detached state ACROSS a GO boundary unchanged — no extra force-rollback,
    // and the next batch is entered detached (its first write re-opens the safety net).
    [Fact]
    public async Task StepAsync_DetachedSession_CarriesForwardAcrossBoundary()
    {
        const string script = "ROLLBACK TRANSACTION;\nGO\nSELECT 2 AS afterdetach;";
        var executor = ExecutorWith(text =>
            !text.Contains("__dbg_ctl") ? Empty()
            : text.Contains("ROLLBACK") ? ControlRow(ok: true, trancount: 0, xactState: 0)
            : Ok());
        var session = ScriptSession(script, executor);
        await session.InitializeAsync();

        await session.StepAsync();          // ROLLBACK detaches, then crosses the GO boundary
        Assert.Equal(StepDisposition.BatchCompleted, session.LastStep.Disposition);
        Assert.Equal(1, session.CurrentBatchIndex);
        Assert.False(session.IsDoomed);
        Assert.True(session.IsTransactionDetached);     // detached state carried across GO (§8.1)

        await session.TeardownAsync();
    }

    // §9 (lane 1b): a debuggee ROLLBACK in a LATER batch destroys connection-scoped #temps
    // that were PROMOTED to the session tier by an earlier boundary — they must be marked
    // dead there too (the ReseedAllFramesAfterDetach session-tier reconciliation), so a
    // later reference fails like native.
    [Fact]
    public async Task StepAsync_PromotedSessionTemp_MarkedDead_OnLaterBatchRollback()
    {
        const string script = "CREATE TABLE #s (n int);\nGO\nROLLBACK TRANSACTION;\nGO\nSELECT 3 AS after;";
        var executor = ExecutorWith(text =>
            !text.Contains("__dbg_ctl") ? Empty()
            : text.Contains("ROLLBACK") ? ControlRow(ok: true, trancount: 0, xactState: 0)
            : Ok());
        var session = ScriptSession(script, executor);
        await session.InitializeAsync();

        await session.StepAsync();          // batch 1: CREATE #s, then cross → #s PROMOTED (alive)
        Assert.Equal(1, session.CurrentBatchIndex);
        Assert.Contains(session.SessionTempObjects,
            e => string.Equals(e.OriginalName, "#s", StringComparison.OrdinalIgnoreCase) && !e.IsDead);

        await session.StepAsync();          // batch 2: ROLLBACK → detach → the promoted #s dies
        Assert.True(session.IsTransactionDetached);
        Assert.Contains(session.SessionTempObjects,
            e => string.Equals(e.OriginalName, "#s", StringComparison.OrdinalIgnoreCase) && e.IsDead);

        await session.TeardownAsync();
    }

    // -----------------------------------------------------------------------------------
    // M8 FIX (§10.1/§8.3, §10-gated): CLIENT-SIDE compile-refused batches across GO.
    // The batch-terminal tests above use a SERVER-reported fault (208 / severity 20 /
    // ConnectionBroken), which routes through PerformRouteAsync. p26's batch 3 is the OTHER
    // flavor: an undeclared @nope (engine 137) the debugger's OWN compile validator catches
    // while BUILDING the batch's cursor (ExecutionCursor.Create → ControlFlowMap /
    // MilestoneValidator), before the batch is ever live. Pre-fix, EnterBatchAsync's
    // ParseTimeDiagnosticException propagated uncaught through AdvanceToNextBatchAsync and
    // crashed the whole session instead of "batch aborts at compile, next batch continues"
    // (fact 32a). A compile refusal has no cursor/SU/frame to anchor a stopped:exception at
    // (the batch never entered), so it surfaces as a diagnostic message + the ordinary batch
    // advance — NOT a synthetic FrameFaulted (the interactive-fault-site decision;
    // docs/archive/reviews/m8-multibatch-fix-opus.md). The FakeExecutor runs the REAL client-side
    // validation (cursor building is executor-independent), so these exercise the true path.
    // -----------------------------------------------------------------------------------

    private static FakeStatementExecutor HealthyExecutor() =>
        ExecutorWith(text => text.Contains("__dbg_ctl") ? Ok() : Empty());

    // §8.3: a MID-SESSION batch (index ≥ 1) the client-side validator refuses (undeclared
    // @nope — p26's batch-3 shape) aborts at compile and the client CONTINUES to the next
    // batch. The GO cross yields the ordinary BatchCompleted landing on the next runnable
    // batch (NOT a FrameFaulted), and the session is not broken.
    [Fact]
    public async Task StepAsync_MidSessionClientCompileFault_SkipsBatch_AdvancesToNextRunnable_NotBroken()
    {
        // batch 0 (ok) · batch 1 (undeclared @nope → client-side 137) · batch 2 (ok)
        const string script = "SELECT 1 AS ok0;\nGO\nSELECT @nope AS bad1;\nGO\nSELECT 2 AS ok2;";
        var executor = HealthyExecutor();
        var session = ScriptSession(script, executor);
        await session.InitializeAsync();

        await session.StepAsync();          // run batch 0 → cross GO → batch 1 is compile-refused + skipped → land on batch 2
        Assert.Equal(StepDisposition.BatchCompleted, session.LastStep.Disposition);
        Assert.False(session.IsBroken);                 // a compile refusal advances, never breaks the session
        Assert.False(session.PendingBatchAdvance);
        Assert.Equal(2, session.CurrentBatchIndex);     // batch 1 skipped; landed on the next runnable batch
        Assert.False(session.IsCompleted);              // batch 2 still has a statement to run

        await session.StepAsync();          // run batch 2's SELECT
        Assert.True(session.IsCompleted);

        Assert.DoesNotContain(executor.ReceivedBatches, b => b.Contains("bad1"));    // the refused batch never ran
        Assert.Contains(executor.ReceivedBatches, b => b.Contains("ok2") && b.Contains("__dbg_ctl"));

        await session.TeardownAsync();
    }

    // §8.3 (the p26 shape reduced to the fake executor): the RunToEnd / fidelity path must
    // NOT crash on a mid-session compile-refused batch (the pre-fix behavior). The run
    // completes, the session is not broken, and the batch AFTER the refused one ran.
    [Fact]
    public async Task RunToEnd_MidSessionClientCompileFault_Completes_SessionNotBroken()
    {
        const string script = "SELECT 1 AS ok0;\nGO\nSELECT @nope AS bad1;\nGO\nSELECT 2 AS ok2;";
        var executor = HealthyExecutor();
        var session = ScriptSession(script, executor);

        await session.RunToEndAsync();      // pre-fix: ParseTimeDiagnosticException crashed the session

        Assert.False(session.IsBroken);
        Assert.DoesNotContain(executor.ReceivedBatches, b => b.Contains("bad1"));    // the refused batch never ran
        Assert.Contains(executor.ReceivedBatches, b => b.Contains("ok2") && b.Contains("__dbg_ctl"));   // continuation
    }

    // §8.3: a compile refusal in the LAST batch leaves no runnable batch — the script is
    // COMPLETE (a completion, not a fault: the refused batch never executed, so prior
    // batches' output stands; the sqlcmd-continue oracle produces it). The session ends.
    [Fact]
    public async Task StepAsync_ClientCompileFaultInLastBatch_MarksSessionComplete_NotBroken()
    {
        const string script = "SELECT 1 AS ok0;\nGO\nSELECT @nope AS badlast;";
        var executor = HealthyExecutor();
        var session = ScriptSession(script, executor);
        await session.InitializeAsync();

        Assert.False(session.IsCompleted);

        await session.StepAsync();          // run batch 0 → cross GO → batch 1 (last) compile-refused → script complete
        Assert.Equal(StepDisposition.BatchCompleted, session.LastStep.Disposition);
        Assert.True(session.IsCompleted);   // no runnable batch remains
        Assert.False(session.IsBroken);     // a compile refusal is not a session fault
        Assert.DoesNotContain(executor.ReceivedBatches, b => b.Contains("badlast"));   // the last batch never ran

        await session.TeardownAsync();
    }

    // §8.3: two CONSECUTIVE compile-refused batches are BOTH skipped — the advance loops
    // until a runnable batch enters (or the batches run out), landing on the first runnable
    // batch after the refused run.
    [Fact]
    public async Task RunToEnd_TwoConsecutiveClientCompileFaults_BothSkipped_LandsOnRunnableBatch()
    {
        // batch 0 (ok) · batch 1 (@a undeclared) · batch 2 (@b undeclared) · batch 3 (ok)
        const string script =
            "SELECT 1 AS ok0;\nGO\nSELECT @a AS bad1;\nGO\nSELECT @b AS bad2;\nGO\nSELECT 2 AS ok3;";
        var executor = HealthyExecutor();
        var session = ScriptSession(script, executor);

        await session.RunToEndAsync();

        Assert.False(session.IsBroken);
        Assert.DoesNotContain(executor.ReceivedBatches, b => b.Contains("bad1"));
        Assert.DoesNotContain(executor.ReceivedBatches, b => b.Contains("bad2"));
        Assert.Contains(executor.ReceivedBatches, b => b.Contains("ok3") && b.Contains("__dbg_ctl"));   // landed + ran
    }
}
