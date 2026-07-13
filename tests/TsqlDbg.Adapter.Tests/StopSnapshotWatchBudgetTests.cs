// M5 I7 (§12.4 watch budget — design note §2, docs/archive/reviews/m5-inspection-design-notes-
// fable.md): StopSnapshot's per-epoch stopwatch. "starts when the first watch of that
// stop begins executing"; overflow marks the expression so a REPEAT request for it is
// recognized as explicit click-to-evaluate.
using TsqlDbg.Adapter.Inspection;
using Xunit;

namespace TsqlDbg.Adapter.Tests;

public sealed class StopSnapshotWatchBudgetTests
{
    [Fact]
    public void FirstWatch_WithinBudget_ProceedsAndIsNotOverflowed()
    {
        var snapshot = new StopSnapshot(0, Array.Empty<SnapshotFrame>(), null);

        var proceeded = snapshot.TryBeginWatchTurn("@a", watchBudgetMs: 2000);

        Assert.True(proceeded);
        Assert.False(snapshot.HasWatchOverflowed("@a"));
    }

    [Fact]
    public void BudgetAlreadyElapsed_ReturnsFalse_AndMarksOverflowed()
    {
        var snapshot = new StopSnapshot(0, Array.Empty<SnapshotFrame>(), null);
        // Starts the stopwatch with a real, generous budget — the first watch of a
        // stop must always proceed (elapsed is ~0 the instant it begins).
        Assert.True(snapshot.TryBeginWatchTurn("@warmup", watchBudgetMs: 2000));

        // A 0ms budget for the SECOND call is a degenerate but deterministic way to
        // force "the budget already elapsed by this watch's turn" without relying on
        // real wall-clock timing in the test.
        var proceeded = snapshot.TryBeginWatchTurn("@late", watchBudgetMs: 0);

        Assert.False(proceeded);
        Assert.True(snapshot.HasWatchOverflowed("@late"));
    }

    [Fact]
    public void OverflowedExpression_IsRecognizedAsClickToEvaluate_ForTheRestOfTheEpoch()
    {
        var snapshot = new StopSnapshot(0, Array.Empty<SnapshotFrame>(), null);
        snapshot.TryBeginWatchTurn("@warmup", watchBudgetMs: 2000);
        snapshot.TryBeginWatchTurn("@a", watchBudgetMs: 0);   // overflows (0ms budget for this turn)

        Assert.True(snapshot.HasWatchOverflowed("@a"));
        // A DIFFERENT expression that never had its own turn is NOT flagged overflowed
        // just because the epoch's budget is spent — the adapter still calls
        // TryBeginWatchTurn for it, which is what actually marks it.
        Assert.False(snapshot.HasWatchOverflowed("@never-tried"));
    }

    [Fact]
    public void DifferentSnapshots_HaveIndependentBudgets()
    {
        var epoch0 = new StopSnapshot(0, Array.Empty<SnapshotFrame>(), null);
        epoch0.TryBeginWatchTurn("@warmup", watchBudgetMs: 2000);
        epoch0.TryBeginWatchTurn("@a", watchBudgetMs: 0);
        Assert.True(epoch0.HasWatchOverflowed("@a"));

        // A NEW stop (new StopSnapshot/epoch) starts a FRESH budget — the same
        // expression is not pre-marked overflowed just because it overflowed last stop.
        var epoch1 = new StopSnapshot(1, Array.Empty<SnapshotFrame>(), null);
        Assert.False(epoch1.HasWatchOverflowed("@a"));
        Assert.True(epoch1.TryBeginWatchTurn("@a", watchBudgetMs: 2000));
    }
}
