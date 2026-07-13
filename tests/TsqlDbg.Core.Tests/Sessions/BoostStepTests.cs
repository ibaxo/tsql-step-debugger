using TsqlDbg.Core.Execution;
using TsqlDbg.Core.Sessions;
using TsqlDbg.Core.Tests.Fakes;
using TsqlDbg.Core.Tracing;
using Xunit;

namespace TsqlDbg.Core.Tests.Sessions;

// M6 §14/A21 — TryStepBoostedAsync dispatch gates, B8 settlement, the B7 recovery
// matrix (attention at every marker boundary of a 3-statement body), and the B10
// trace surface. The trajectory-equivalence scenarios live in
// BoostTrajectoryEquivalenceTests; shared fixture/response helpers in BoostTestKit.
public sealed class BoostStepTests
{
    // ------------------------------------------------------------- dispatch gates

    [Fact]
    public async Task BoostDisabled_NeverDispatches_AndNeverTraces()
    {
        var kit = await BoostTestKit.StartAsync(boost: false, BoostTestKit.CleanLoopInterpretedResponses());
        var result = await kit.Session.TryStepBoostedAsync();

        Assert.Null(result);
        Assert.DoesNotContain(kit.Trace.Events, e => e.Category.StartsWith("boost."));
    }

    [Fact]
    public async Task NotRestingOnAControlNode_ReturnsNull_WithoutRefusalSpam()
    {
        var kit = await BoostTestKit.StartAsync(boost: true, BoostTestKit.CleanLoopInterpretedResponses());
        // The cursor rests on SU#0 (the DECLARE), not an IF/WHILE.
        var result = await kit.Session.TryStepBoostedAsync();

        Assert.Null(result);
        Assert.DoesNotContain(kit.Trace.Events, e => e.Category == "boost.refuse");
    }

    [Fact]
    public async Task PlannerRefusal_TracesBoostRefuse_AndFallsBackToInterpreted()
    {
        var kit = await BoostTestKit.StartAsync(boost: true, BoostTestKit.CleanLoopInterpretedResponses());
        await BoostTestKit.StepToWhileAsync(kit.Session);

        // A breakpoint on a member line refuses (§13/A21).
        var refused = await kit.Session.TryStepBoostedAsync(isBlocked: _ => true);

        Assert.Null(refused);
        var refusal = Assert.Single(kit.Trace.Events, e => e.Category == "boost.refuse");
        Assert.Contains("breakpoint-or-logpoint", refusal.Message);
    }

    [Fact]
    public async Task DoomedSession_RefusesBeforeAnyServerWork_TheA14Mirror()
    {
        // I9/A14 mirror (design note §7): a boosted dispatch on a doomed session must
        // send NOTHING — the A14 pre-flight (which arms only on debuggee compositions
        // while doomed) can therefore never arm off a boost attempt. Fixture: a first
        // boosted loop faults and DOOMS but ROUTES to the outer CATCH (session
        // survives); the doom persists to the second loop's arrival.
        const string script = """
            DECLARE @i int;
            SET @i = 0;
            BEGIN TRY
                WHILE @i < 2
                BEGIN
                    SET @i = @i + 1;
                    INSERT dbo.T VALUES (@i);
                END
            END TRY
            BEGIN CATCH
                SET @i = 90;
            END CATCH
            WHILE @i < 99
            BEGIN
                SET @i = @i + 1;
            END
            """;

        var kit = await BoostTestKit.StartAsync(boost: true, executor =>
        {
            executor.Then(b => BoostTestKit.OkRow(b, state: new object?[] { 0 }));    // SET @i = 0
            executor.Then(b => BoostTestKit.CatchRow(b, 245, "INSERT dbo.T", "Conversion failed.",
                xactState: -1, includeScopeIdentity: true, state: new object?[] { 1 }));   // boosted loop 1: doom + route
            executor.Then(b => BoostTestKit.OkRow(b, xactState: -1, state: new object?[] { 90 }));   // SET @i = 90 (doomed mode)
        }, script);

        await BoostTestKit.StepToWhileAsync(kit.Session);      // DECLARE + SET @i = 0
        var first = await kit.Session.TryStepBoostedAsync();   // boosted loop 1: fires, faults, dooms, routes
        Assert.NotNull(first);
        Assert.Equal(StepDisposition.RoutedToCatch, kit.Session.LastStep.Disposition);
        Assert.True(kit.Session.IsDoomed);

        await kit.Session.StepAsync();                         // SET @i = 90 inside CATCH; cursor exits to loop 2
        Assert.Equal(TsqlDbg.Core.Interpreter.SuSubKind.While, kit.Session.Current!.SubKind);

        var batchesBefore = kit.Executor.ReceivedBatches.Count;
        var second = await kit.Session.TryStepBoostedAsync();
        Assert.Null(second);
        Assert.Equal(batchesBefore, kit.Executor.ReceivedBatches.Count);   // no server work at all
        Assert.Contains(kit.Trace.Events, e => e.Category == "boost.refuse" && e.Message.Contains("session-doomed"));
        Assert.NotEqual(StepDisposition.DoomedTempPreflight, kit.Session.LastStep.Disposition);
    }

    // --------------------------------------------------------- B8 + trace surface

    [Fact]
    public async Task CleanBoostedLoop_CompletesTheNode_InOneBatch_WithTheB10Events()
    {
        var kit = await BoostTestKit.StartAsync(boost: true, BoostTestKit.CleanLoopBoostedResponses());
        var run = await BoostTestKit.DriveAsync(kit.Session);

        Assert.True(kit.Session.IsCompleted);
        // Exactly ONE batch carried boost markers (the loop), none of the rest did.
        Assert.Single(kit.Executor.ReceivedBatches, b => b.Contains("UPDATE #__dbg_boost SET pos ="));

        // B10: plan → fire → complete, all through the standard tracer.
        Assert.Contains(kit.Trace.Events, e => e.Category == "boost.plan" && e.Message.Contains("markers=3"));
        Assert.Contains(kit.Trace.Events, e => e.Category == "boost.fire" && e.Message.Contains("seq=1"));
        Assert.Contains(kit.Trace.Events, e => e.Category == "boost.complete" && e.Message.Contains("rc=0"));

        // B8: the postamble's live capture became the R4 shadow — the post-loop
        // statement's R4 rewrite seeds the shadow from it (fact 27's native 0).
        var finalBatch = kit.Executor.ReceivedBatches[^1];
        Assert.Contains("_sh_rowcount int = 0", finalBatch);
        Assert.Contains(run.Dispositions, d => d == StepDisposition.Performed);
    }

    [Fact]
    public async Task BoostedNodeAsTheFramesLastStatement_CompletesTheSession()
    {
        const string script = """
            DECLARE @i int;
            SET @i = 0;
            WHILE @i < 2
            BEGIN
                SET @i = @i + 1;
            END
            """;
        var executor = new FakeStatementExecutor();
        BoostTestKit.EnqueueInit(executor, boost: true);
        executor.Then(b => BoostTestKit.OkRow(b, state: new object?[] { 0 }));         // SET @i = 0
        executor.Then(b => BoostTestKit.OkRow(b, rc: 0, state: new object?[] { 2 })); // the boosted loop

        var trace = new RecordingTraceSink();
        var session = new Session(BoostTestKit.Options(script, boost: true), executor, trace, nonce: "b005");
        await session.InitializeAsync();
        await BoostTestKit.DriveAsync(session);

        Assert.True(session.IsCompleted);
        Assert.Contains(trace.Events, e => e.Category == "boost.complete");
    }

    // ------------------------------------------------- B7: the attention matrix

    [Theory]
    [InlineData(-1, 4)]   // nothing completed → cursor stays ON the WHILE (§10.5 verbatim)
    [InlineData(0, 7)]    // after SET @i (marker 0) → retry re-runs the INSERT
    [InlineData(1, 8)]    // after INSERT (marker 1) → retry re-runs SET @t
    [InlineData(2, 4)]    // after SET @t (body tail, marker 2) → the predicate re-eval — native continuation
    public async Task AttentionAtEveryMarkerBoundary_ReestablishesTheExactPosition(int persistedPos, int expectedLine)
    {
        var executor = new FakeStatementExecutor();
        BoostTestKit.EnqueueInit(executor, boost: true);
        executor.Then(b => BoostTestKit.OkRow(b, state: new object?[] { 0, null }));   // SET @i = 0
        executor.Then(b => BoostTestKit.OkRow(b, state: new object?[] { 0, 0 }));      // SET @t = 0
        executor.Then(_ => throw new StatementExecutionException("Operation cancelled by user.", 0, 0));
        executor.Then(_ => BoostTestKit.RecoveryRead(seq: 1, pos: persistedPos, state: new object?[] { 1, 0 }));

        var trace = new RecordingTraceSink();
        var session = new Session(BoostTestKit.Options(BoostTestKit.LoopScript, boost: true), executor, trace, nonce: "b006");
        await session.InitializeAsync();
        var run = await BoostTestKit.DriveAsync(session);

        Assert.Equal(StepDisposition.EngineAttention, run.Dispositions[^1]);
        Assert.Equal(expectedLine, session.Current!.Span.StartLine);
        Assert.Contains(run.Messages, m => m.Contains("Paused inside a boosted region"));
        Assert.Contains(trace.Events, e => e.Category == "boost.recovery" && e.Message.Contains($"pos={persistedPos}"));

        if (persistedPos >= 0)
        {
            // The snapshot re-seeded from the state-table read (invariant P: exact).
            Assert.Equal(new object?[] { 1, 0 }, session.Frames[0].Snapshot);
        }
    }

    [Fact]
    public async Task RawOperationCanceledSurface_StillRunsB7Recovery()
    {
        // Fact 30 (m6-boosted-attention triage): on the async driver path a pause's
        // cancellation usually surfaces as a RAW OperationCanceledException, not the
        // wrapped Number-0 SqlException. Before the fix that surface bypassed
        // HandleBoostedBatchDeathAsync entirely — no recovery read, cursor left ON
        // the node, completed subtree work persisted → a later continue re-fired the
        // subtree and double-applied it.
        var executor = new FakeStatementExecutor();
        BoostTestKit.EnqueueInit(executor, boost: true);
        executor.Then(b => BoostTestKit.OkRow(b, state: new object?[] { 0, null }));   // SET @i = 0
        executor.Then(b => BoostTestKit.OkRow(b, state: new object?[] { 0, 0 }));      // SET @t = 0
        executor.Then(_ => throw new OperationCanceledException());                    // the boosted WHILE
        executor.Then(_ => BoostTestKit.RecoveryRead(seq: 1, pos: 0, state: new object?[] { 1, 0 }));

        var trace = new RecordingTraceSink();
        var session = new Session(BoostTestKit.Options(BoostTestKit.LoopScript, boost: true), executor, trace, nonce: "b016");
        await session.InitializeAsync();
        var run = await BoostTestKit.DriveAsync(session);

        Assert.Equal(StepDisposition.EngineAttention, run.Dispositions[^1]);
        Assert.Equal(7, session.Current!.Span.StartLine);      // pos 0 = after SET @i → retry re-runs the INSERT
        Assert.Equal(new object?[] { 1, 0 }, session.Frames[0].Snapshot);
        Assert.Contains(run.Messages, m => m.Contains("Paused inside a boosted region"));
        Assert.Contains(trace.Events, e => e.Category == "boost.recovery" && e.Message.Contains("pos=0"));
    }

    [Fact]
    public async Task PausedStepToken_NeverStarvesTheRecoveryRead()
    {
        // The recovery read is debugger-initiated (B7): it runs precisely when the
        // §10.5 pause token is ALREADY cancelled. Issued on that token it would throw
        // before reaching the server and recovery would be silently skipped — the
        // executor here mimics SqlClient's token check to pin that the read goes out
        // on CancellationToken.None instead.
        using var pause = new CancellationTokenSource();
        var executor = new FakeStatementExecutor { ThrowOnCancelledToken = true };
        BoostTestKit.EnqueueInit(executor, boost: true);
        executor.Then(b => BoostTestKit.OkRow(b, state: new object?[] { 0, null }));   // SET @i = 0
        executor.Then(b => BoostTestKit.OkRow(b, state: new object?[] { 0, 0 }));      // SET @t = 0
        executor.Then(_ =>
        {
            // Attention lands mid-batch: the pause fires DURING execution, then the
            // driver reports the cancelled command (the sync-path SqlException shape).
            pause.Cancel();
            throw new StatementExecutionException("Operation cancelled by user.", 11, 0);
        });
        executor.Then(_ => BoostTestKit.RecoveryRead(seq: 1, pos: 1, state: new object?[] { 1, 0 }));

        var trace = new RecordingTraceSink();
        var session = new Session(BoostTestKit.Options(BoostTestKit.LoopScript, boost: true), executor, trace, nonce: "b017");
        await session.InitializeAsync();
        await BoostTestKit.StepToWhileAsync(session);
        var boosted = await session.TryStepBoostedAsync(cancellationToken: pause.Token);

        Assert.NotNull(boosted);
        Assert.Equal(StepDisposition.EngineAttention, session.LastStep.Disposition);
        Assert.Equal(8, session.Current!.Span.StartLine);      // pos 1 = after INSERT → retry re-runs SET @t
        Assert.Equal(new object?[] { 1, 0 }, session.Frames[0].Snapshot);
        Assert.Contains(trace.Events, e => e.Category == "boost.recovery" && e.Message.Contains("pos=1"));
    }

    [Fact]
    public async Task Attention_WithAStaleSeq_TreatsItAsNothingCompleted()
    {
        var executor = new FakeStatementExecutor();
        BoostTestKit.EnqueueInit(executor, boost: true);
        executor.Then(b => BoostTestKit.OkRow(b, state: new object?[] { 0, null }));
        executor.Then(b => BoostTestKit.OkRow(b, state: new object?[] { 0, 0 }));
        executor.Then(_ => throw new StatementExecutionException("Operation cancelled by user.", 0, 0));
        executor.Then(_ => BoostTestKit.RecoveryRead(seq: 99, pos: 2, state: new object?[] { 9, 9 }));  // stale seq (B4)

        var session = new Session(BoostTestKit.Options(BoostTestKit.LoopScript, boost: true), executor, new RecordingTraceSink(), nonce: "b007");
        await session.InitializeAsync();
        var run = await BoostTestKit.DriveAsync(session);

        Assert.Equal(StepDisposition.EngineAttention, run.Dispositions[^1]);
        Assert.Equal(4, session.Current!.Span.StartLine);      // cursor stays ON the WHILE
        Assert.NotEqual(new object?[] { 9, 9 }, session.Frames[0].Snapshot);   // stale state NOT applied
    }

    [Fact]
    public async Task RecoveryReadFailure_IsNothingCompleted_NeverAnEscalatedError()
    {
        var executor = new FakeStatementExecutor();
        BoostTestKit.EnqueueInit(executor, boost: true);
        executor.Then(b => BoostTestKit.OkRow(b, state: new object?[] { 0, null }));
        executor.Then(b => BoostTestKit.OkRow(b, state: new object?[] { 0, 0 }));
        executor.Then(_ => throw new StatementExecutionException("Operation cancelled by user.", 0, 0));
        executor.Then(_ => throw new StatementExecutionException("Invalid object name '#__dbg_boost'.", 16, 208));

        var session = new Session(BoostTestKit.Options(BoostTestKit.LoopScript, boost: true), executor, new RecordingTraceSink(), nonce: "b008");
        await session.InitializeAsync();
        var run = await BoostTestKit.DriveAsync(session);

        Assert.Equal(StepDisposition.EngineAttention, run.Dispositions[^1]);
        Assert.Equal(4, session.Current!.Span.StartLine);
    }

    [Fact]
    public async Task PropagateClassDeath_RecoversPosition_ThenRoutesFromTheCaller()
    {
        // §10.1 no-control-row class (deferred resolution etc.) on a boosted batch:
        // recovery first (variables reflect the last marker), then the standard
        // sameScopeUncatchable walk — frame 0 with no caller = terminal.
        var executor = new FakeStatementExecutor();
        BoostTestKit.EnqueueInit(executor, boost: true);
        executor.Then(b => BoostTestKit.OkRow(b, state: new object?[] { 0, null }));
        executor.Then(b => BoostTestKit.OkRow(b, state: new object?[] { 0, 0 }));
        executor.Then(_ => throw new StatementExecutionException("Invalid object name 'dbo.T'.", 16, 208));
        executor.Then(_ => BoostTestKit.RecoveryRead(seq: 1, pos: 0, state: new object?[] { 1, 0 }));

        var trace = new RecordingTraceSink();
        var session = new Session(BoostTestKit.Options(BoostTestKit.LoopScript, boost: true), executor, trace, nonce: "b009");
        await session.InitializeAsync();
        var run = await BoostTestKit.DriveAsync(session);

        Assert.Equal(StepDisposition.FrameFaulted, run.Dispositions[^1]);
        Assert.True(session.IsBroken);
        Assert.Equal(new object?[] { 1, 0 }, session.Frames[0].Snapshot);       // last-marker state, kept for inspection
        Assert.Contains(run.Messages, m => m.Contains("variable state reflects the last completed statement"));
    }
}
