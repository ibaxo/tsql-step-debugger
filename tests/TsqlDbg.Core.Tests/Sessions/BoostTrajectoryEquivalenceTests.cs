using TsqlDbg.Core.Interpreter;
using TsqlDbg.Core.Sessions;
using Xunit;

namespace TsqlDbg.Core.Tests.Sessions;

// M6 §14/A21 — the trajectory-equivalence suite (design note §7, the boost suite's
// centerpiece): the SAME script driven boosted vs interpreted through the reference
// driver loop must land in the same client-visible state — final variable snapshot,
// shadow values (probed through the post-loop @@ROWCOUNT reader's R4 seed literal in
// the composed batch text), dispositions of the fault story, native error messages
// (modulo batching), session transaction state, and cursor position. The fake
// executor is the engine model, scripted to the live-probed facts (12/18/21/26-29).
public sealed class BoostTrajectoryEquivalenceTests
{
    private static string FinalBatchShadowSeed(BoostTestKit.Kit kit, string shadow)
    {
        var final = kit.Executor.ReceivedBatches[^1];
        var index = final.IndexOf(shadow, StringComparison.Ordinal);
        Assert.True(index >= 0, $"final batch carries no {shadow} seed:\n{final}");
        return final.Substring(index, final.IndexOf(';', index) - index);
    }

    // ------------------------------------------------------------ 1: clean loop

    [Fact]
    public async Task CleanLoop_BoostedAndInterpreted_LandIdentically()
    {
        var interpreted = await BoostTestKit.StartAsync(boost: false, BoostTestKit.CleanLoopInterpretedResponses());
        var interpretedRun = await BoostTestKit.DriveAsync(interpreted.Session);

        var boosted = await BoostTestKit.StartAsync(boost: true, BoostTestKit.CleanLoopBoostedResponses());
        var boostedRun = await BoostTestKit.DriveAsync(boosted.Session);

        Assert.True(interpreted.Session.IsCompleted);
        Assert.True(boosted.Session.IsCompleted);
        Assert.Equal(interpreted.Session.Frames[0].Snapshot, boosted.Session.Frames[0].Snapshot);
        Assert.Equal(new object?[] { 2, 3 }, boosted.Session.Frames[0].Snapshot);

        // The shadow probe: both final batches seed the R4 substitute with the SAME
        // native post-loop value (fact 27's reset 0) — interpreted got it from
        // ObservePredicateEvaluation, boosted from the postamble's live capture (B8).
        Assert.Equal(
            FinalBatchShadowSeed(interpreted, "_sh_rowcount"),
            FinalBatchShadowSeed(boosted, "_sh_rowcount"));
        Assert.EndsWith("= 0", FinalBatchShadowSeed(boosted, "_sh_rowcount"));

        // No fault story on either side; the whole loop collapsed to one batch.
        Assert.DoesNotContain(interpretedRun.Dispositions, d => d != StepDisposition.Performed);
        Assert.DoesNotContain(boostedRun.Dispositions, d => d != StepDisposition.Performed);
        Assert.True(boostedRun.Dispositions.Count < interpretedRun.Dispositions.Count);
    }

    // ---------------------------------- 2: unhandled statement-level fault (B6)

    [Fact]
    public async Task UnhandledStatementFault_ReentersAtTheMappedSU_AndMatchesInterpreted()
    {
        // Iteration 1's INSERT faults 547 (statement-level, no TRY anywhere): native
        // continues at the next statement INSIDE the block (facts 18/21). The
        // boosted run must re-enter interpreted mode AT the INSERT and apply the same
        // disposition; the remainder re-qualifies and re-boosts at the next arrival.
        var interpreted = await BoostTestKit.StartAsync(boost: false, executor =>
        {
            executor.Then(b => BoostTestKit.OkRow(b, state: new object?[] { 0, null }));
            executor.Then(b => BoostTestKit.OkRow(b, state: new object?[] { 0, 0 }));
            executor.Then(_ => BoostTestKit.PredicateRow(true));
            executor.Then(b => BoostTestKit.OkRow(b, state: new object?[] { 1, 0 }));       // SET @i = 1
            executor.Then(b => BoostTestKit.CatchRow(b, 547, "INSERT dbo.T", "The INSERT statement conflicted.",
                state: new object?[] { 1, 0 }));                                            // INSERT faults
            executor.Then(b => BoostTestKit.OkRow(b, state: new object?[] { 1, 1 }));       // SET @t = 1 (continuation)
            executor.Then(_ => BoostTestKit.PredicateRow(true));
            executor.Then(b => BoostTestKit.OkRow(b, state: new object?[] { 2, 1 }));
            executor.Then(b => BoostTestKit.OkRow(b, rc: 1, state: new object?[] { 2, 1 }));
            executor.ThenEmpty();                                                          // C2: sys.triggers lookup (dbo.T's first SUCCESSFUL insert — iteration 1 faulted before reaching it)
            executor.Then(b => BoostTestKit.OkRow(b, state: new object?[] { 2, 3 }));
            executor.Then(_ => BoostTestKit.PredicateRow(false));
            executor.Then(b => BoostTestKit.OkRow(b, state: new object?[] { 2, 3 }));
        });
        var interpretedRun = await BoostTestKit.DriveAsync(interpreted.Session);

        var boosted = await BoostTestKit.StartAsync(boost: true, executor =>
        {
            executor.Then(b => BoostTestKit.OkRow(b, state: new object?[] { 0, null }));
            executor.Then(b => BoostTestKit.OkRow(b, state: new object?[] { 0, 0 }));
            executor.Then(b => BoostTestKit.CatchRow(b, 547, "INSERT dbo.T", "The INSERT statement conflicted.",
                includeScopeIdentity: true, state: new object?[] { 1, 0 }));                // boosted batch 1: faults
            executor.Then(b => BoostTestKit.OkRow(b, state: new object?[] { 1, 1 }));       // SET @t = 1 interpreted
            executor.Then(b => BoostTestKit.OkRow(b, rc: 0, state: new object?[] { 2, 3 })); // re-boost: the remainder
            executor.Then(b => BoostTestKit.OkRow(b, state: new object?[] { 2, 3 }));       // post-loop reader
        });
        var boostedRun = await BoostTestKit.DriveAsync(boosted.Session);

        // Identical fault story: exactly one UnhandledContinued each, same native text.
        Assert.Equal(1, interpretedRun.Dispositions.Count(d => d == StepDisposition.UnhandledContinued));
        Assert.Equal(1, boostedRun.Dispositions.Count(d => d == StepDisposition.UnhandledContinued));
        Assert.Contains(interpretedRun.Messages, m => m.Contains("Msg 547"));
        Assert.Contains(boostedRun.Messages, m => m.Contains("Msg 547"));

        // The B6 re-entry position pin: after the fault step, both runs rest on the
        // statement AFTER the faulted INSERT (line 8 — the fact-21 skip disposition).
        var interpretedFaultStop = interpretedRun.StopLines[interpretedRun.Dispositions.IndexOf(StepDisposition.UnhandledContinued)];
        var boostedFaultStop = boostedRun.StopLines[boostedRun.Dispositions.IndexOf(StepDisposition.UnhandledContinued)];
        Assert.Equal(8, interpretedFaultStop);
        Assert.Equal(8, boostedFaultStop);

        // Identical landing: variables, completion, and two boosted batches fired
        // (the remainder re-qualified — B1's re-arrival rule).
        Assert.Equal(interpreted.Session.Frames[0].Snapshot, boosted.Session.Frames[0].Snapshot);
        Assert.True(interpreted.Session.IsCompleted && boosted.Session.IsCompleted);
        Assert.Equal(2, boosted.Executor.ReceivedBatches.Count(b => b.Contains("UPDATE #__dbg_boost SET pos =")));
    }

    // ------------------------------------------ 3: fault routed to an outer CATCH

    private const string RoutedScript = """
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
        """;

    [Fact]
    public async Task FaultRoutedToAnOuterCatch_LandsIdentically()
    {
        var interpreted = await BoostTestKit.StartAsync(boost: false, executor =>
        {
            executor.Then(b => BoostTestKit.OkRow(b, state: new object?[] { 0 }));
            executor.Then(_ => BoostTestKit.PredicateRow(true));
            executor.Then(b => BoostTestKit.OkRow(b, state: new object?[] { 1 }));
            executor.Then(b => BoostTestKit.CatchRow(b, 8134, "INSERT dbo.T", "Divide by zero error encountered.",
                state: new object?[] { 1 }));
            executor.Then(b => BoostTestKit.OkRow(b, state: new object?[] { 90 }));   // CATCH body
        }, RoutedScript);
        var interpretedRun = await BoostTestKit.DriveAsync(interpreted.Session);

        var boosted = await BoostTestKit.StartAsync(boost: true, executor =>
        {
            executor.Then(b => BoostTestKit.OkRow(b, state: new object?[] { 0 }));
            executor.Then(b => BoostTestKit.CatchRow(b, 8134, "INSERT dbo.T", "Divide by zero error encountered.",
                includeScopeIdentity: true, state: new object?[] { 1 }));             // the boosted loop
            executor.Then(b => BoostTestKit.OkRow(b, state: new object?[] { 90 }));   // CATCH body
        }, RoutedScript);
        var boostedRun = await BoostTestKit.DriveAsync(boosted.Session);

        foreach (var run in new[] { interpretedRun, boostedRun })
        {
            var routedIndex = run.Dispositions.IndexOf(StepDisposition.RoutedToCatch);
            Assert.True(routedIndex >= 0);
            Assert.Equal(11, run.StopLines[routedIndex]);       // first CATCH statement (SET @i = 90)
        }

        Assert.Equal(interpreted.Session.Frames[0].Snapshot, boosted.Session.Frames[0].Snapshot);
        Assert.Equal(new object?[] { 90 }, boosted.Session.Frames[0].Snapshot);
        Assert.True(interpreted.Session.IsCompleted && boosted.Session.IsCompleted);
    }

    // ---------------------------------------------------------- 4: doom fault

    [Fact]
    public async Task DoomFault_ObservesTheEdgeOnTheControlRow_AndTerminatesLikeInterpreted()
    {
        var interpreted = await BoostTestKit.StartAsync(boost: false, executor =>
        {
            executor.Then(b => BoostTestKit.OkRow(b, state: new object?[] { 0, null }));
            executor.Then(b => BoostTestKit.OkRow(b, state: new object?[] { 0, 0 }));
            executor.Then(_ => BoostTestKit.PredicateRow(true));
            executor.Then(b => BoostTestKit.OkRow(b, state: new object?[] { 1, 0 }));
            executor.Then(b => BoostTestKit.CatchRow(b, 245, "INSERT dbo.T", "Conversion failed.",
                xactState: -1, state: new object?[] { 1, 0 }));
        });
        var interpretedRun = await BoostTestKit.DriveAsync(interpreted.Session);

        var boosted = await BoostTestKit.StartAsync(boost: true,
            BoostTestKit.Fault(245, "Conversion failed.", xactState: -1, atLine: "INSERT dbo.T"));
        var boostedRun = await BoostTestKit.DriveAsync(boosted.Session);

        foreach (var (session, run) in new[] { (interpreted.Session, interpretedRun), (boosted.Session, boostedRun) })
        {
            Assert.Equal(StepDisposition.FrameFaulted, run.Dispositions[^1]);
            Assert.True(session.IsBroken);
            Assert.True(session.IsDoomed);                       // §10.4: the edge was control-row-observed
            Assert.Contains(run.Messages, m => m.Contains("uncommittable (doomed)"));
            Assert.Contains(run.Messages, m => m.Contains("Unhandled error 245"));
        }

        // Inspection parity: both cursors rest ON the faulted INSERT (line 7) — the
        // boosted run's B6 JumpTo placed it there before routing went terminal.
        Assert.Equal(7, interpreted.Session.Current!.Span.StartLine);
        Assert.Equal(7, boosted.Session.Current!.Span.StartLine);
    }

    // ------------------------------------- 5: trigger-rollback detach edge (3609)

    [Fact]
    public async Task TrancountZeroOnTheFaultRow_FiresTheDetachEdge_BothModes()
    {
        // The 3609 family: a trigger-forced ROLLBACK ends the transaction mid-node.
        // Whatever the client-visible wrapper (Sonnet's live observation says a hard
        // SqlException in some shapes; a caught CATCH row in others), the CATCH-row
        // shape must fire the §10.4 detach edge on BOTH paths — never skipped past.
        var interpreted = await BoostTestKit.StartAsync(boost: false, executor =>
        {
            executor.Then(b => BoostTestKit.OkRow(b, state: new object?[] { 0, null }));
            executor.Then(b => BoostTestKit.OkRow(b, state: new object?[] { 0, 0 }));
            executor.Then(_ => BoostTestKit.PredicateRow(true));
            executor.Then(b => BoostTestKit.OkRow(b, state: new object?[] { 1, 0 }));
            executor.Then(b => BoostTestKit.CatchRow(b, 3609, "INSERT dbo.T", "The transaction ended in the trigger.",
                trancount: 0, xactState: 0, state: new object?[] { 1, 0 }));
        });
        var boosted = await BoostTestKit.StartAsync(boost: true, executor =>
        {
            executor.Then(b => BoostTestKit.OkRow(b, state: new object?[] { 0, null }));
            executor.Then(b => BoostTestKit.OkRow(b, state: new object?[] { 0, 0 }));
            executor.Then(b => BoostTestKit.CatchRow(b, 3609, "INSERT dbo.T", "The transaction ended in the trigger.",
                trancount: 0, xactState: 0, includeScopeIdentity: true, state: new object?[] { 1, 0 }));
        });

        var interpretedRun = await DriveOneFaultAsync(interpreted.Session);
        var boostedRun = await DriveOneFaultAsync(boosted.Session);

        Assert.True(interpreted.Session.IsTransactionDetached);
        Assert.True(boosted.Session.IsTransactionDetached);     // §8 checklist 1: the edge was observed, not skipped
        Assert.Equal(interpretedRun, boostedRun);               // same disposition for the fault step
    }

    private static async Task<StepDisposition> DriveOneFaultAsync(Session session)
    {
        var guard = 0;
        while (!session.IsCompleted)
        {
            if (++guard > 20)
            {
                throw new InvalidOperationException("no fault surfaced");
            }

            var boosted = await session.TryStepBoostedAsync();
            if (boosted is null)
            {
                await session.StepAsync();
            }

            var disposition = session.LastStep.Disposition;
            if (disposition is not (StepDisposition.Performed or StepDisposition.FrameCompleted))
            {
                return disposition;
            }
        }

        throw new InvalidOperationException("no fault surfaced before completion");
    }

    // -------------------------------------------- F2: fault-time scope identity

    [Fact]
    public async Task BoostedFaultRow_FeedsScopeIdentityToTheR6Shadow()
    {
        // A boosted CATCH row carrying scope_identity = 42 (a completed in-slice
        // identity insert) must reach the R6 shadow (F2 ruling) — probed through the
        // next composed batch's shadow-seed literal, exactly like the rc probe.
        const string script = """
            DECLARE @i int, @s int;
            SET @i = 0;
            WHILE @i < 2
            BEGIN
                SET @i = @i + 1;
                INSERT dbo.T VALUES (@i);
            END
            SET @s = SCOPE_IDENTITY();
            """;

        var kit = await BoostTestKit.StartAsync(boost: true, executor =>
        {
            executor.Then(b => BoostTestKit.OkRow(b, state: new object?[] { 0, null }));
            executor.Then(b => BoostTestKit.CatchRow(b, 547, "INSERT dbo.T", "conflict",
                includeScopeIdentity: true, scopeIdentity: 42m, state: new object?[] { 2, null }));   // iteration-2 fault
            executor.Then(_ => BoostTestKit.PredicateRow(false));                                     // interpreted re-eval exits
            executor.Then(b => BoostTestKit.OkRow(b, scopeIdentity: 42m, state: new object?[] { 2, 42 }));   // the reader
        }, script);

        await BoostTestKit.StepToWhileAsync(kit.Session);       // DECLARE + SET @i = 0
        var faulted = await kit.Session.TryStepBoostedAsync();  // boosted loop: faults at the INSERT (last body child)
        Assert.NotNull(faulted);
        Assert.Equal(StepDisposition.UnhandledContinued, kit.Session.LastStep.Disposition);
        // Fact-21 skip past the body's LAST child lands on the predicate re-eval.
        Assert.Equal(3, kit.Session.Current!.Span.StartLine);

        // Step INTERPRETED from here (deliberately bypassing a re-boost): between the
        // fault and the reader only a predicate evaluation runs, which is
        // scope-identity-neutral (fact 12) — so the reader's R6 seed literal is
        // EXACTLY the F2 feed from the boosted CATCH row.
        await kit.Session.StepAsync();                          // predicate false → loop exits
        await kit.Session.StepAsync();                          // SET @s = SCOPE_IDENTITY();
        Assert.True(kit.Session.IsCompleted);

        var readerBatch = kit.Executor.ReceivedBatches[^1];
        Assert.Contains("_sh_scopeid numeric(38,0) = 42", readerBatch);
    }
}
