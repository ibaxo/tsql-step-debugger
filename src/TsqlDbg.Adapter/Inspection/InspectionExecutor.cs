using System.Threading.Channels;

namespace TsqlDbg.Adapter.Inspection;

// M5 I1+I2 (design note §2, docs/archive/reviews/m5-inspection-design-notes-fable.md; A15,
// DESIGN §3): the epoch/gate/FIFO-lane core of the M5 threading model.
//
// - Epoch: monotonically increasing, bumped by BeginResume() BEFORE the caller
//   acquires Gate to step — so a request racing a resume observes the new epoch,
//   never mid-step state (design note §5 item 1).
// - Gate: the SAME SemaphoreSlim serializes both execution (next/continue/stepIn/
//   stepOut/goto) and every inspection work item — C20's one-command-at-a-time
//   contract, now shared by two producers instead of one.
// - The FIFO lane: a single background pump processes one EnqueueAsync item at a
//   time (never concurrently) — I2 rule "one FIFO lane is the only correct shape."
//   Each item re-checks its captured epoch (and IsRunning) both before AND after
//   acquiring Gate — stale items complete Invalidated without ever touching the
//   connection (I2 rule 1).
// - Resume preempts: BeginResume() cancels whichever inspection item is currently
//   in flight (if any) via its own CancellationTokenSource. Inspection work never
//   calls into Session's fault-routing machinery (GetStateSnapshotAsync and
//   friends are plain reads with no PerformRouteAsync involvement), so the
//   resulting OperationCanceledException is classified right HERE as a local
//   Invalidated outcome — it can never surface as the session's §10.5
//   EngineAttention path (I2 rule 3).
// - M7 (§5.4, S4 cancel): a SEPARATE "cancel fence" gives DAP `cancel` its own,
//   independent preemption channel — see CancelPending's remarks. A cancelled item
//   resolves Cancelled, never Invalidated (the adapter reports these differently);
//   the two channels never interact (a cancel can never look like a resume and
//   vice versa — checked in a fixed priority order, resume first, everywhere both
//   are re-checked).
public sealed class InspectionExecutor : IAsyncDisposable
{
    public enum InspectionOutcome { Completed, Invalidated, Cancelled }

    public readonly record struct InspectionResult<T>(InspectionOutcome Outcome, T? Value);

    public SemaphoreSlim Gate { get; } = new(1, 1);

    private int _epoch;
    private volatile bool _running;
    private int _cancelFence;
    private readonly object _inFlightLock = new();
    private CancellationTokenSource? _inFlightCts;
    private readonly Channel<Func<Task>> _queue = Channel.CreateUnbounded<Func<Task>>();
    private readonly Task _pump;

    public InspectionExecutor()
    {
        _pump = Task.Run(PumpAsync);
    }

    public int Epoch => Volatile.Read(ref _epoch);

    /// <summary>True from BeginResume() until the corresponding PublishSnapshot —
    /// stackTrace/scopes/variables all answer the DAP not-stopped error while true
    /// (A18) instead of serving a snapshot mid-step.</summary>
    public bool IsRunning => _running;

    public StopSnapshot? CurrentSnapshot { get; private set; }

    /// <summary>Call synchronously, on the protocol thread, before scheduling any
    /// resume's background step — never from inside the background Task itself, or
    /// a racing introspection request could still observe the OLD epoch (design
    /// note §5 item 1).</summary>
    public int BeginResume()
    {
        var epoch = Interlocked.Increment(ref _epoch);
        _running = true;
        lock (_inFlightLock)
        {
            _inFlightCts?.Cancel();
        }

        return epoch;
    }

    /// <summary>Call once the resumed step has settled (stopped/paused/exception) —
    /// publishes the new immutable snapshot and clears the running flag.</summary>
    public void PublishSnapshot(StopSnapshot snapshot)
    {
        CurrentSnapshot = snapshot;
        _running = false;
    }

    /// <summary>Session-ending paths that never reach a normal stop still need the
    /// running flag cleared (nothing left to preempt).</summary>
    public void MarkIdle() => _running = false;

    /// <summary>DAP `cancel` (§5.4, S4): best-effort cancellation of whatever
    /// inspection work (REPL/watch/hover/temp-table-page fetches — the §3 FIFO
    /// lane) is currently pending — never the step/execution path, which belongs to
    /// `pause`, not this. The SDK does not expose the DAP request's own `seq` to
    /// Handle* overrides (verified by reflection against the pinned 18.0.10427.1
    /// package: IRequestResponder/IRequestResponder&lt;TArgs&gt; surface only
    /// Arguments/Command; the concrete Protocol.RequestResponder DOES carry a `seq`
    /// field but it is private, not exposed through any public API this adapter can
    /// reach), so per-requestId selectivity is not achievable — DAP itself
    /// documents `cancel` as "best effort," and this is the honest reading of that:
    /// cancel EVERYTHING currently pending in the lane. Advancing the fence makes
    /// every item still sitting in the queue resolve Cancelled without ever
    /// running; cancelling the in-flight cts (the SAME mechanism BeginResume
    /// already uses for resume-preemption) preempts whatever round trip is
    /// actually executing right now.</summary>
    public void CancelPending()
    {
        Interlocked.Increment(ref _cancelFence);
        lock (_inFlightLock)
        {
            _inFlightCts?.Cancel();
        }
    }

    /// <summary>I2: enqueue one round-trip inspection work item onto the single FIFO
    /// lane, tagged with the epoch the CALLER observed when it decided the work was
    /// needed (I1's fill-once contract). Resolves Invalidated — without invoking
    /// <paramref name="work"/> at all — if a resume has moved the epoch on by the
    /// time this item is dequeued, or moves it on again between the gate wait and
    /// actually running. Resolves Cancelled the same no-op way if a DAP `cancel`
    /// advanced the fence instead.</summary>
    public Task<InspectionResult<T>> EnqueueAsync<T>(int epochAtEnqueue, Func<CancellationToken, Task<T>> work)
    {
        var fenceAtEnqueue = Volatile.Read(ref _cancelFence);
        var tcs = new TaskCompletionSource<InspectionResult<T>>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_queue.Writer.TryWrite(() => RunItemAsync(epochAtEnqueue, fenceAtEnqueue, work, tcs)))
        {
            tcs.TrySetResult(new InspectionResult<T>(InspectionOutcome.Invalidated, default));
        }

        return tcs.Task;
    }

    private async Task RunItemAsync<T>(
        int epochAtEnqueue, int fenceAtEnqueue, Func<CancellationToken, Task<T>> work, TaskCompletionSource<InspectionResult<T>> tcs)
    {
        if (epochAtEnqueue != Epoch || _running)
        {
            tcs.TrySetResult(new InspectionResult<T>(InspectionOutcome.Invalidated, default));
            return;
        }

        if (fenceAtEnqueue != Volatile.Read(ref _cancelFence))
        {
            tcs.TrySetResult(new InspectionResult<T>(InspectionOutcome.Cancelled, default));
            return;
        }

        CancellationTokenSource cts;
        lock (_inFlightLock)
        {
            cts = new CancellationTokenSource();
            _inFlightCts = cts;
        }

        try
        {
            await Gate.WaitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Preempted while still queued waiting for the gate — a resume takes
            // priority in classification if BOTH happened to fire (rare race);
            // otherwise this was a cancel.
            var outcome = (epochAtEnqueue != Epoch || _running)
                ? InspectionOutcome.Invalidated
                : InspectionOutcome.Cancelled;
            tcs.TrySetResult(new InspectionResult<T>(outcome, default));
            ClearInFlight(cts);
            return;
        }

        try
        {
            if (epochAtEnqueue != Epoch || _running)
            {
                tcs.TrySetResult(new InspectionResult<T>(InspectionOutcome.Invalidated, default));
                return;
            }

            if (fenceAtEnqueue != Volatile.Read(ref _cancelFence))
            {
                tcs.TrySetResult(new InspectionResult<T>(InspectionOutcome.Cancelled, default));
                return;
            }

            var value = await work(cts.Token).ConfigureAwait(false);
            tcs.TrySetResult(new InspectionResult<T>(InspectionOutcome.Completed, value));
        }
        catch (OperationCanceledException)
        {
            // Resumed or cancelled while this item's round trip was actually in
            // flight — BeginResume()/CancelPending() cancelled cts. Local outcome
            // only (I2 rule 3): never the session's §10.5 EngineAttention path.
            var outcome = (epochAtEnqueue != Epoch || _running)
                ? InspectionOutcome.Invalidated
                : InspectionOutcome.Cancelled;
            tcs.TrySetResult(new InspectionResult<T>(outcome, default));
        }
        catch (Exception ex)
        {
            tcs.TrySetException(ex);
        }
        finally
        {
            Gate.Release();
            ClearInFlight(cts);
        }
    }

    private void ClearInFlight(CancellationTokenSource cts)
    {
        lock (_inFlightLock)
        {
            if (ReferenceEquals(_inFlightCts, cts))
            {
                _inFlightCts = null;
            }
        }

        cts.Dispose();
    }

    private async Task PumpAsync()
    {
        await foreach (var item in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            await item().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        await _pump.ConfigureAwait(false);
        Gate.Dispose();
    }
}
