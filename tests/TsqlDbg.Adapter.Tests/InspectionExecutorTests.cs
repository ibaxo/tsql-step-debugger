// M5 I1/I2 §4.4 threading unit tests (docs/archive/reviews/m5-inspection-design-notes-fable.md
// §4 item 4): deterministic tests against InspectionExecutor directly — it has no
// Session/DAP dependency (round-trip work is an arbitrary Func<CancellationToken,
// Task<T>>), so these exercise the epoch/gate/FIFO-lane contract without a live
// connection.
using TsqlDbg.Adapter.Inspection;

namespace TsqlDbg.Adapter.Tests;

public sealed class InspectionExecutorTests
{
    [Fact]
    public async Task StaleEpochAtEnqueue_InvalidatesWithoutRunningWork()
    {
        var executor = new InspectionExecutor();
        executor.BeginResume(); // epoch -> 1
        var ranWork = false;

        // Enqueued tagged with epoch 0 — already stale by the time it is enqueued
        // (the caller captured its snapshot's epoch before a resume raced it).
        var result = await executor.EnqueueAsync(0, _ =>
        {
            ranWork = true;
            return Task.FromResult(42);
        });

        Assert.Equal(InspectionExecutor.InspectionOutcome.Invalidated, result.Outcome);
        Assert.False(ranWork, "I2 rule 1: a stale item must never touch the connection.");
        await executor.DisposeAsync();
    }

    [Fact]
    public async Task ResumeInvalidatesQueuedFill_BeforeItRuns()
    {
        var executor = new InspectionExecutor();
        var startedFirst = new TaskCompletionSource();

        // First item occupies the FIFO lane (and the gate) so the second is still
        // queued, not yet dequeued, when BeginResume() fires. It blocks purely on
        // its own cancellation token (Task.Delay honors cancellation deterministically —
        // no race against a manually-completed TaskCompletionSource) so the ONLY way
        // it ever unblocks is BeginResume()'s cancel.
        var firstTask = executor.EnqueueAsync(0, async ct =>
        {
            startedFirst.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return 1;
        });
        await startedFirst.Task;

        var secondRan = false;
        var secondTask = executor.EnqueueAsync(0, _ =>
        {
            secondRan = true;
            return Task.FromResult(2);
        });

        // Resume: bumps epoch to 1 and cancels the in-flight first item.
        executor.BeginResume();

        var firstResult = await firstTask;
        var secondResult = await secondTask;

        Assert.Equal(InspectionExecutor.InspectionOutcome.Invalidated, firstResult.Outcome);
        Assert.Equal(InspectionExecutor.InspectionOutcome.Invalidated, secondResult.Outcome);
        Assert.False(secondRan, "resume must invalidate a still-queued item before it ever runs (I2 rule 1).");
        await executor.DisposeAsync();
    }

    [Fact]
    public async Task CancelClassification_StaysLocal_ExecutorRemainsUsableAfterInvalidation()
    {
        // I2 rule 3: a resume's cancel of an in-flight inspection item is a LOCAL
        // outcome — nothing about it can "brick" the executor or leak into later
        // work. Proven behaviorally: after an invalidated round trip, a fresh
        // enqueue at the new epoch completes normally.
        var executor = new InspectionExecutor();
        var started = new TaskCompletionSource();

        var inFlight = executor.EnqueueAsync(0, async ct =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return 1;
        });
        await started.Task;

        var newEpoch = executor.BeginResume();
        var invalidated = await inFlight;
        Assert.Equal(InspectionExecutor.InspectionOutcome.Invalidated, invalidated.Outcome);

        // The resume settles (mirrors the adapter always publishing a snapshot once
        // a step concludes) — only then does the executor serve new inspection work.
        executor.PublishSnapshot(new StopSnapshot(newEpoch, Array.Empty<SnapshotFrame>(), null));
        var next = await executor.EnqueueAsync(newEpoch, _ => Task.FromResult(99));
        Assert.Equal(InspectionExecutor.InspectionOutcome.Completed, next.Outcome);
        Assert.Equal(99, next.Value);
        await executor.DisposeAsync();
    }

    [Fact]
    public async Task SnapshotAndEpoch_ServeInstantlyWhileAResumeIsInFlight()
    {
        // Design note §5.1 / A15: the protocol thread never blocks on the gate.
        // Simulate an in-flight continue holding the gate indefinitely and verify
        // that reading CurrentSnapshot/Epoch/IsRunning — the reads stackTrace/
        // scopes/variables now do — return immediately regardless.
        var executor = new InspectionExecutor();
        var initial = new StopSnapshot(0, Array.Empty<SnapshotFrame>(), null);
        executor.PublishSnapshot(initial);

        await executor.Gate.WaitAsync(); // simulates the background execution task mid-step
        try
        {
            var epoch = executor.BeginResume();
            Assert.Equal(1, epoch);
            Assert.True(executor.IsRunning);
            Assert.Same(initial, executor.CurrentSnapshot); // last published snapshot, unblocked read
        }
        finally
        {
            executor.Gate.Release();
        }

        var next = new StopSnapshot(1, Array.Empty<SnapshotFrame>(), null);
        executor.PublishSnapshot(next);
        Assert.False(executor.IsRunning);
        Assert.Same(next, executor.CurrentSnapshot);
        await executor.DisposeAsync();
    }

    [Fact]
    public async Task FifoLane_ProcessesOneItemAtATime_NeverConcurrently()
    {
        var executor = new InspectionExecutor();
        var concurrent = 0;
        var maxConcurrent = 0;
        var gateLock = new object();

        var tasks = Enumerable.Range(0, 5)
            .Select(i => executor.EnqueueAsync(0, async _ =>
            {
                lock (gateLock)
                {
                    concurrent++;
                    maxConcurrent = Math.Max(maxConcurrent, concurrent);
                }

                await Task.Delay(10);

                lock (gateLock)
                {
                    concurrent--;
                }

                return i;
            }))
            .ToList();

        var results = await Task.WhenAll(tasks);
        Assert.All(results, r => Assert.Equal(InspectionExecutor.InspectionOutcome.Completed, r.Outcome));
        Assert.Equal(1, maxConcurrent);
        await executor.DisposeAsync();
    }

    [Fact]
    public async Task FaultingWork_PropagatesAsException_NotInvalidated()
    {
        // A genuine fault (not a resume-cancel) must surface to the caller so it
        // can decide how to render it (REPL/watch report faults, never throw —
        // I9/F5) — it must not be silently swallowed as "Invalidated".
        var executor = new InspectionExecutor();
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await executor.EnqueueAsync<int>(0, _ => throw new InvalidOperationException("boom")));
        await executor.DisposeAsync();
    }

    // ---------------------------------------------------- M7 §5.4 (S4) cancel

    [Fact]
    public async Task CancelPending_InvalidatesQueuedFill_BeforeItRuns_AsCancelled()
    {
        // Mirrors ResumeInvalidatesQueuedFill_BeforeItRuns exactly, but via
        // CancelPending() instead of BeginResume() — a SEPARATE channel with its
        // own outcome (Cancelled, not Invalidated), same "never touches the
        // connection" guarantee for a still-queued item.
        var executor = new InspectionExecutor();
        var startedFirst = new TaskCompletionSource();

        var firstTask = executor.EnqueueAsync(0, async ct =>
        {
            startedFirst.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return 1;
        });
        await startedFirst.Task;

        var secondRan = false;
        var secondTask = executor.EnqueueAsync(0, _ =>
        {
            secondRan = true;
            return Task.FromResult(2);
        });

        executor.CancelPending();

        var firstResult = await firstTask;
        var secondResult = await secondTask;

        Assert.Equal(InspectionExecutor.InspectionOutcome.Cancelled, firstResult.Outcome);
        Assert.Equal(InspectionExecutor.InspectionOutcome.Cancelled, secondResult.Outcome);
        Assert.False(secondRan, "a cancel must invalidate a still-queued item before it ever runs, same as a resume.");
        // Epoch/IsRunning are UNTOUCHED by a cancel — the step path stays entirely
        // pause's territory; a cancel can never masquerade as a resume.
        Assert.Equal(0, executor.Epoch);
        Assert.False(executor.IsRunning);
        await executor.DisposeAsync();
    }

    [Fact]
    public async Task CancelPending_CancelsInFlightWork_AsCancelled_ExecutorRemainsUsableAfter()
    {
        var executor = new InspectionExecutor();
        var started = new TaskCompletionSource();

        var inFlight = executor.EnqueueAsync(0, async ct =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return 1;
        });
        await started.Task;

        executor.CancelPending();
        var cancelled = await inFlight;
        Assert.Equal(InspectionExecutor.InspectionOutcome.Cancelled, cancelled.Outcome);

        // Unlike a resume, a cancel never runs/settles a step — no PublishSnapshot
        // needed before the executor serves the next item at the SAME epoch.
        var next = await executor.EnqueueAsync(0, _ => Task.FromResult(99));
        Assert.Equal(InspectionExecutor.InspectionOutcome.Completed, next.Outcome);
        Assert.Equal(99, next.Value);
        await executor.DisposeAsync();
    }

    [Fact]
    public async Task CancelPending_StillWorksCorrectly_AfterAResumeHasSettled()
    {
        // Cross-check that the two channels don't clobber each other's bookkeeping:
        // once a resume has settled (the normal per-stop rhythm), a SUBSEQUENT
        // CancelPending() still classifies Cancelled on its own terms and leaves
        // epoch/running exactly where the resume left them — deliberately NOT
        // racing the two together (Cancel()'s callback-execution timing relative
        // to a second, immediately-following mutator is not something to pin).
        var executor = new InspectionExecutor();
        var newEpoch = executor.BeginResume();
        executor.PublishSnapshot(new StopSnapshot(newEpoch, Array.Empty<SnapshotFrame>(), null));

        var started = new TaskCompletionSource();
        var inFlight = executor.EnqueueAsync(newEpoch, async ct =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return 1;
        });
        await started.Task;

        executor.CancelPending();
        var result = await inFlight;

        Assert.Equal(InspectionExecutor.InspectionOutcome.Cancelled, result.Outcome);
        Assert.Equal(newEpoch, executor.Epoch);
        Assert.False(executor.IsRunning);
        await executor.DisposeAsync();
    }
}
