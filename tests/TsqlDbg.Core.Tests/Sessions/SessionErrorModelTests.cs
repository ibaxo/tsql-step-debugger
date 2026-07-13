// M3 (Fable) — DESIGN §10 driven end-to-end through Session.StepAsync with a fake
// executor: routing (§10.3), the error-context stack + R7 shadows (§10.2),
// re-materialization activation (§10.7), the transaction watchdog incl. doomed mode
// and resurrection (§10.4), the §10.6 'all' two-phase stop, bare-THROW re-raise, the
// fact-17 waitfor-skip reset, and the §10.1 no-control-row classes. Engine truths:
// docs/engine-facts.md facts 15-19.
using TsqlDbg.Core.Execution;
using TsqlDbg.Core.Sessions;
using TsqlDbg.Core.Tests.Fakes;
using Xunit;

namespace TsqlDbg.Core.Tests.Sessions;

public sealed class SessionErrorModelTests
{
    // ---- fake-batch helpers (shapes mirror ComposedBatchBuilder's real output) --------

    private static BatchResult Ok(
        int? rc = null, int trancount = 1, int xactState = 0,
        IReadOnlyList<ResultSet>? userSets = null, object?[]? state = null)
    {
        var sets = new List<ResultSet>(userSets ?? Array.Empty<ResultSet>())
        {
            new(new[] { "__dbg_ctl", "ok", "rc", "scope_identity", "trancount", "xact_state" },
                new IReadOnlyList<object?>[] { new object?[] { 1, true, rc, null, trancount, xactState } }),
        };
        if (state is not null)
        {
            sets.Add(StateSet(state));
        }

        return new BatchResult(sets, Array.Empty<string>());
    }

    private static BatchResult Fault(
        int errNumber, string errMessage, int errSeverity = 16, int errState = 1, int? errLine = null,
        int trancount = 1, int xactState = 1, object?[]? state = null)
    {
        var sets = new List<ResultSet>
        {
            new(new[] { "__dbg_ctl", "ok", "err_number", "err_severity", "err_state", "err_line", "err_procedure", "err_message", "trancount", "xact_state" },
                new IReadOnlyList<object?>[] { new object?[] { 1, false, errNumber, errSeverity, errState, errLine, null, errMessage, trancount, xactState } }),
        };
        if (state is not null)
        {
            sets.Add(StateSet(state));
        }

        return new BatchResult(sets, Array.Empty<string>());
    }

    private static ResultSet StateSet(object?[] values)
    {
        var columns = new string[values.Length + 1];
        var row = new object?[values.Length + 1];
        columns[0] = "__dbg_state";
        row[0] = 1;
        for (var i = 0; i < values.Length; i++)
        {
            columns[i + 1] = $"c{i}";
            row[i + 1] = values[i];
        }

        return new ResultSet(columns, new IReadOnlyList<object?>[] { row });
    }

    private static ResultSet Scalar(string column, object? value)
        => new(new[] { column }, new IReadOnlyList<object?>[] { new object?[] { value } });

    private static Session ScriptSession(string script, FakeStatementExecutor executor)
        => new(new SessionOptions("DEVSQL01", "SalesDb", LaunchMode.Script, null, null, script), executor);

    private static FakeStatementExecutor Init(FakeStatementExecutor executor)
        => executor.ThenEmpty().ThenEmpty().ThenEmpty().ThenEmpty();   // SET opts, CREATE, seed INSERT, BEGIN TRAN

    // ---- §10.3 routing ------------------------------------------------------------------

    [Fact]
    public async Task Fault_InsideTry_RoutesToCatch_AndArmsContextShadows()
    {
        const string script = """
            BEGIN TRY
                SELECT 1 / 0 AS a;
            END TRY
            BEGIN CATCH
                SELECT ERROR_NUMBER() AS n;
            END CATCH
            """;
        var executor = Init(new FakeStatementExecutor())
            .Then(_ => Fault(8134, "Divide by zero error encountered.", errLine: 7))
            .Then(_ => Ok(userSets: new[] { Scalar("n", 8134) }));
        var session = ScriptSession(script, executor);
        await session.InitializeAsync();

        await session.StepAsync();                                     // SELECT 1/0 → fault

        Assert.Equal(StepDisposition.RoutedToCatch, session.LastStep.Disposition);
        Assert.False(session.IsBroken);
        Assert.Equal(5, session.Current!.Span.StartLine);              // first CATCH statement
        Assert.NotNull(session.ActiveErrorContext);
        Assert.Equal(8134, session.ActiveErrorContext!.Values.Number);

        await session.StepAsync();                                     // the CATCH statement

        var catchBatch = executor.ReceivedBatches[^1];
        Assert.Contains("_sh_err_number int = 8134", catchBatch);      // R7 substitution armed
        Assert.Contains("RAISERROR(N'Divide by zero", catchBatch);     // §10.7 re-materialization active
        Assert.True(session.IsCompleted);
        Assert.Null(session.ActiveErrorContext);                       // END CATCH popped it
        await session.TeardownAsync();
    }

    [Fact]
    public async Task Fault_RoutesWithFact18Shadows_ErrorNumberAndZeroRowcount()
    {
        const string script = """
            BEGIN TRY
                SELECT 1 / 0 AS a;
            END TRY
            BEGIN CATCH
                SELECT @@ERROR AS e, @@ROWCOUNT AS r;
            END CATCH
            """;
        var executor = Init(new FakeStatementExecutor())
            .Then(_ => Fault(8134, "Divide by zero error encountered."))
            .Then(_ => Ok(userSets: new[] { Scalar("e", 8134) }));
        var session = ScriptSession(script, executor);
        await session.InitializeAsync();
        await session.StepAsync();
        await session.StepAsync();

        var catchBatch = executor.ReceivedBatches[^1];
        Assert.Contains("_sh_error int = 8134", catchBatch);           // fact 18: @@ERROR = number
        Assert.Contains("_sh_rowcount int = 0", catchBatch);           // fact 18: @@ROWCOUNT = 0
        await session.TeardownAsync();
    }

    [Fact]
    public async Task BareThrow_ReRoutesToOuterCatch_WithSameValues_NoServerRoundTrip()
    {
        const string script = """
            BEGIN TRY
                BEGIN TRY
                    SELECT 1 / 0 AS a;
                END TRY
                BEGIN CATCH
                    THROW;
                END CATCH
            END TRY
            BEGIN CATCH
                SELECT ERROR_NUMBER() AS n;
            END CATCH
            """;
        var executor = Init(new FakeStatementExecutor())
            .Then(_ => Fault(8134, "Divide by zero error encountered."));
        var session = ScriptSession(script, executor);
        await session.InitializeAsync();

        await session.StepAsync();                                     // fault → inner CATCH (the THROW)
        Assert.Equal(6, session.Current!.Span.StartLine);
        var batchesBefore = executor.ReceivedBatches.Count;

        await session.StepAsync();                                     // bare THROW → outer CATCH

        Assert.Equal(batchesBefore, executor.ReceivedBatches.Count);   // interpreted client-side
        Assert.Equal(StepDisposition.RoutedToCatch, session.LastStep.Disposition);
        Assert.Equal(10, session.Current!.Span.StartLine);
        Assert.Equal(8134, session.ActiveErrorContext!.Values.Number); // exact re-raise
        Assert.Equal(1, session.ErrorContextDepth);                    // replaced, not stacked
        await session.TeardownAsync();
    }

    [Fact]
    public async Task BareThrow_Unhandled_IsTerminal_EvenWhenHealthy()
    {
        const string script = """
            BEGIN TRY
                SELECT 1 / 0 AS a;
            END TRY
            BEGIN CATCH
                THROW;
            END CATCH
            """;
        var executor = Init(new FakeStatementExecutor())
            .Then(_ => Fault(8134, "Divide by zero error encountered."));
        var session = ScriptSession(script, executor);
        await session.InitializeAsync();
        await session.StepAsync();                                     // → CATCH

        var (_, messages) = await session.StepAsync();                 // bare THROW, no outer TRY

        Assert.Equal(StepDisposition.FrameFaulted, session.LastStep.Disposition);
        Assert.True(session.IsBroken);
        Assert.Contains(messages, m => m.Contains("batch-aborting"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.StepAsync());
        await session.TeardownAsync();
    }

    [Fact]
    public async Task ThrowWithArgs_FaultsAsNewError_AndIsTerminalWhenUnhandled()
    {
        const string script = "SELECT 1 AS a;\nTHROW 50001, 'boom', 1;\nSELECT 2 AS never;";
        var executor = Init(new FakeStatementExecutor())
            .Then(_ => Ok(rc: 1, userSets: new[] { Scalar("a", 1) }))
            .Then(_ => Fault(50001, "boom"));                          // healthy xact_state, no TRY
        var session = ScriptSession(script, executor);
        await session.InitializeAsync();
        await session.StepAsync();

        await session.StepAsync();                                     // THROW 50001 → batch-aborting

        Assert.Equal(StepDisposition.FrameFaulted, session.LastStep.Disposition);
        Assert.True(session.IsBroken);
        await session.TeardownAsync();
    }

    // ---- §10.6 'all' filter: two-phase stop ----------------------------------------------

    [Fact]
    public async Task BreakOnAllErrors_StopsAtFaultSite_ThenRoutesOnNextStep()
    {
        const string script = """
            BEGIN TRY
                SELECT 1 / 0 AS a;
            END TRY
            BEGIN CATCH
                SELECT 1 AS c;
            END CATCH
            """;
        var executor = Init(new FakeStatementExecutor())
            .Then(_ => Fault(8134, "Divide by zero error encountered."));
        var session = ScriptSession(script, executor);
        await session.InitializeAsync();
        session.BreakOnAllErrors = true;

        await session.StepAsync();

        Assert.Equal(StepDisposition.FaultAtSite, session.LastStep.Disposition);
        Assert.Equal(2, session.Current!.Span.StartLine);              // still ON the faulted unit
        Assert.Null(session.ActiveErrorContext);                       // not routed yet
        var batchesBefore = executor.ReceivedBatches.Count;

        await session.StepAsync();                                     // deferred route, no server work

        Assert.Equal(batchesBefore, executor.ReceivedBatches.Count);
        Assert.Equal(StepDisposition.RoutedToCatch, session.LastStep.Disposition);
        Assert.Equal(5, session.Current!.Span.StartLine);
        Assert.Equal(8134, session.ActiveErrorContext!.Values.Number);
        await session.TeardownAsync();
    }

    // ---- §10.4 watchdog: doom + doomed-mode batches ---------------------------------------

    [Fact]
    public async Task Doom_SwitchesToSnapshotSeededBatches_WithoutStateWrites()
    {
        const string script = """
            DECLARE @m int;
            BEGIN TRY
                SELECT 1 AS go;
            END TRY
            BEGIN CATCH
                SET @m = 2;
            END CATCH
            """;
        var executor = Init(new FakeStatementExecutor())
            .Then(_ => Fault(245, "Conversion failed.", xactState: -1, state: new object?[] { null }))
            .Then(_ => Ok(xactState: -1, state: new object?[] { 2 }));
        var session = ScriptSession(script, executor);
        await session.InitializeAsync();
        await session.StepAsync();                                     // DECLARE @m (no initializer, no batch)

        var (_, messages) = await session.StepAsync();                 // fault dooms + routes

        Assert.True(session.IsDoomed);
        Assert.Equal(StepDisposition.RoutedToCatch, session.LastStep.Disposition);
        Assert.Contains(messages, m => m.Contains("uncommittable"));

        await session.StepAsync();                                     // SET @m = 2, doomed mode

        var doomedBatch = executor.ReceivedBatches[^1];
        Assert.Contains("SELECT @m = CONVERT(int, ", doomedBatch);     // snapshot-seeded preamble
        Assert.DoesNotContain("FROM #__dbg_s0", doomedBatch);
        Assert.DoesNotContain("UPDATE #__dbg_s0", doomedBatch);        // unwritable while doomed
        Assert.True(executor.ReceivedParameters.ContainsKey(executor.ReceivedBatches.Count - 1),
            $"param keys: [{string.Join(",", executor.ReceivedParameters.Keys)}]; batches: {executor.ReceivedBatches.Count}; last: {executor.ReceivedBatches[^1][..80]}");
        // Fact 22: the doomed transaction did NOT survive the faulting batch's end (the
        // engine 3998-rolled it back) — every doomed-mode batch re-establishes real
        // doom up front so XACT_STATE()/@@TRANCOUNT/3930 semantics stay genuine.
        Assert.Contains("BEGIN TRANSACTION;", doomedBatch);
        Assert.Contains("_doom = 1/0; END TRY BEGIN CATCH END CATCH;", doomedBatch);
        await session.TeardownAsync();
    }

    // ---- §10.4 watchdog: deferred resurrection (amended per fact 22 — the immediate
    // ---- re-open made every post-rollback XACT_STATE()/@@TRANCOUNT observation read
    // ---- 1 where native reads 0, which is exactly what p05's final projection sees) ----

    [Fact]
    public async Task DebuggeeRollback_DefersReopen_AndReseedsTableImmediately()
    {
        const string script = "DECLARE @a int = 5;\nROLLBACK;\nSELECT @a AS a;";
        var executor = Init(new FakeStatementExecutor())
            .Then(_ => Ok(rc: 1, state: new object?[] { 5 }))          // initializer SET @a = 5
            .Then(_ => Ok(trancount: 0, state: new object?[] { 5 }))   // ROLLBACK → trancount 0
            .ThenEmpty()                                               // re-seed UPDATE (autocommit, trancount 0)
            .Then(_ => Ok(trancount: 0, userSets: new[] { Scalar("a", 5) }));
        var session = ScriptSession(script, executor);
        await session.InitializeAsync();
        await session.StepAsync();                                     // DECLARE initializer

        var (_, messages) = await session.StepAsync();                 // ROLLBACK

        Assert.Contains(messages, m => m.Contains("re-seeded from the"));
        Assert.False(session.IsDoomed);
        Assert.True(session.IsTransactionDetached);
        var reseed = executor.ReceivedBatches[^1];
        Assert.StartsWith("UPDATE #__dbg_s0 SET [a] = CONVERT(int, ", reseed);
        var parameters = executor.ReceivedParameters[executor.ReceivedBatches.Count - 1];
        Assert.Equal(5, Assert.Single(parameters).Value);

        await session.StepAsync();                                     // SELECT @a — a read: NO re-open

        // Exactly ONE standalone BEGIN TRANSACTION for the whole run (§4 step 5's);
        // the read after the rollback ran at native trancount 0.
        Assert.Equal(1, executor.ReceivedBatches.Count(b => b == "BEGIN TRANSACTION;"));
        Assert.True(session.IsTransactionDetached);
        Assert.True(session.IsCompleted);
        await session.TeardownAsync();
    }

    [Fact]
    public async Task PauseRacingTheDetachEdge_NeverStarvesTheReseed()
    {
        // Ruling rider (i) (m6-boosted-attention-triage-fable.md §3 invariant, applied
        // to the interpreted path): once the control row exists, the watchdog's edge
        // bookkeeping — here the detach reseed — always completes; a §10.5 pause whose
        // token cancelled DURING the batch must not starve it. The executor mimics
        // SqlClient's token check (fact 30), so a reseed issued on the cancelled step
        // token would throw before reaching the server.
        const string script = "DECLARE @a int = 5;\nROLLBACK;\nSELECT @a AS a;";
        using var pause = new CancellationTokenSource();
        var executor = Init(new FakeStatementExecutor { ThrowOnCancelledToken = true })
            .Then(_ => Ok(rc: 1, state: new object?[] { 5 }))          // initializer SET @a = 5
            .Then(_ =>
            {
                pause.Cancel();                                        // the pause races the ROLLBACK batch
                return Ok(trancount: 0, state: new object?[] { 5 });   // → detach edge on its control row
            })
            .ThenEmpty();                                              // re-seed UPDATE — must still execute
        var session = ScriptSession(script, executor);
        await session.InitializeAsync();
        await session.StepAsync();                                     // DECLARE initializer

        var (_, messages) = await session.StepAsync(pause.Token);      // ROLLBACK under the racing pause

        Assert.Contains(messages, m => m.Contains("re-seeded from the"));
        Assert.True(session.IsTransactionDetached);
        Assert.StartsWith("UPDATE #__dbg_s0 SET [a] = CONVERT(int, ", executor.ReceivedBatches[^1]);
        await session.TeardownAsync();
    }

    [Fact]
    public async Task DetachedWrite_ReopensSafetyTransactionFirst()
    {
        const string script = "DECLARE @a int = 5;\nROLLBACK;\nINSERT INTO dbo.T DEFAULT VALUES;";
        var executor = Init(new FakeStatementExecutor())
            .Then(_ => Ok(rc: 1, state: new object?[] { 5 }))          // initializer
            .Then(_ => Ok(trancount: 0, state: new object?[] { 5 }))   // ROLLBACK
            .ThenEmpty()                                               // re-seed UPDATE
            .ThenEmpty()                                               // deferred BEGIN TRANSACTION;
            .Then(_ => Ok(rc: 1, state: new object?[] { 5 }));         // INSERT under the fresh net
        var session = ScriptSession(script, executor);
        await session.InitializeAsync();
        await session.StepAsync();                                     // initializer
        await session.StepAsync();                                     // ROLLBACK → detached

        var (_, messages) = await session.StepAsync();                 // INSERT — needs protection

        Assert.Contains(messages, m => m.Contains("re-opened before line 3"));
        Assert.False(session.IsTransactionDetached);
        Assert.Equal(2, executor.ReceivedBatches.Count(b => b == "BEGIN TRANSACTION;"));
        var reopenIndex = executor.ReceivedBatches.FindLastIndex(b => b == "BEGIN TRANSACTION;");
        Assert.Contains("INSERT INTO dbo.T", executor.ReceivedBatches[reopenIndex + 1]);
        await session.TeardownAsync();
    }

    [Fact]
    public async Task DoomedWalk_P05Shape_RedoomsEachStep_RollbackExitsClean_NoReopenForReads()
    {
        // The p05 archetype end-to-end at the fake level: XACT_ABORT ON doom inside
        // TRY, `IF XACT_STATE() = -1 ROLLBACK` in CATCH, statements after the rollback.
        // Fact 22 shape: while doomed every batch re-dooms; the debuggee's ROLLBACK
        // ends its batch clean; afterwards nothing re-opens until a write would.
        const string script = """
            DECLARE @m int;
            SET XACT_ABORT ON;
            BEGIN TRY
                SET @m = 1 / 0;
            END TRY
            BEGIN CATCH
                IF XACT_STATE() = -1
                    ROLLBACK;
            END CATCH
            SET @m = 7;
            """;
        var executor = Init(new FakeStatementExecutor())
            .Then(_ => Ok())                                                        // SET XACT_ABORT ON
            .Then(_ => Fault(8134, "Divide by zero error encountered.",
                xactState: -1, state: new object?[] { null }))                      // doom + route
            .Then(_ => Ok(xactState: -1, userSets: new[] { Scalar("p", 1) }))       // IF XACT_STATE() = -1 → true
            .Then(_ => Ok(trancount: 0, xactState: 0, state: new object?[] { null }))  // ROLLBACK exits doom cleanly
            .ThenEmpty()                                                            // re-seed UPDATE
            .Then(_ => Ok(trancount: 0, state: new object?[] { 7 }));               // SET @m = 7 at native trancount 0
        var session = ScriptSession(script, executor);
        await session.InitializeAsync();
        await session.StepAsync();                                     // DECLARE (no initializer)
        await session.StepAsync();                                     // SET XACT_ABORT ON → frame env

        await session.StepAsync();                                     // SET @m = 1/0 → doomed + routed
        Assert.True(session.IsDoomed);
        Assert.Equal(StepDisposition.RoutedToCatch, session.LastStep.Disposition);

        await session.StepAsync();                                     // predicate, under re-established doom
        var predicateBatch = executor.ReceivedBatches[^1];
        Assert.Contains("BEGIN TRANSACTION;", predicateBatch);         // redoom prefix present
        Assert.Contains("_doom = 1/0; END TRY BEGIN CATCH END CATCH;", predicateBatch);
        Assert.Contains("XACT_STATE() = -1", predicateBatch);          // the debuggee predicate itself

        var (_, messages) = await session.StepAsync();                 // ROLLBACK
        Assert.False(session.IsDoomed);
        Assert.True(session.IsTransactionDetached);
        Assert.Contains(messages, m => m.Contains("re-seeded"));
        Assert.StartsWith("UPDATE #__dbg_s0 SET [m] = CONVERT(int, ", executor.ReceivedBatches[^1]);

        await session.StepAsync();                                     // SET @m = 7 — variable-only, no re-open
        var afterBatch = executor.ReceivedBatches[^1];
        Assert.Contains("FROM #__dbg_s0", afterBatch);                 // back to table-seeded batches
        Assert.DoesNotContain("_doom = 1/0", afterBatch);              // no redoom once healthy
        Assert.Equal(1, executor.ReceivedBatches.Count(b => b == "BEGIN TRANSACTION;"));
        Assert.True(session.IsCompleted);
        await session.TeardownAsync();
    }

    [Fact]
    public async Task TrailingErrors_WithHealthyControlRow_AreTerminal()
    {
        // Fact 22's defensive boundary: an all-3998 trailing exception is tolerated by
        // the executor ONLY as the doomed batch-end epilogue. If the control row says
        // the transaction was healthy, the session must refuse to continue on it.
        const string script = "SELECT 1 AS a;\nSELECT 2 AS b;";
        var executor = Init(new FakeStatementExecutor())
            .Then(_ => Ok(rc: 1, userSets: new[] { Scalar("a", 1) }) with
            {
                TrailingErrors = new[] { new BatchTrailingError(3998, 16, "Uncommittable transaction is detected at the end of the batch.") },
            });
        var session = ScriptSession(script, executor);
        await session.InitializeAsync();

        await session.StepAsync();

        Assert.Equal(StepDisposition.FrameFaulted, session.LastStep.Disposition);
        Assert.True(session.IsBroken);
        Assert.Equal(3998, session.LastStep.Error!.Number);
        await session.TeardownAsync();
    }

    // ---- fact 17: waitfor skip resets shadows ---------------------------------------------

    [Fact]
    public async Task WaitForSkip_ZeroesRowcountAndErrorShadows()
    {
        const string script = "SELECT 1 AS a;\nWAITFOR DELAY '00:00:01';\nSELECT @@ROWCOUNT AS r;";
        var executor = Init(new FakeStatementExecutor())
            .Then(_ => Ok(rc: 5, userSets: new[] { Scalar("a", 1) }))
            .Then(_ => Ok(userSets: new[] { Scalar("r", 0) }));
        var session = ScriptSession(script, executor);
        await session.InitializeAsync();
        await session.StepAsync();                                     // rc shadow := 5

        var batchesBefore = executor.ReceivedBatches.Count;
        await session.StepAsync();                                     // WAITFOR skipped
        Assert.Equal(batchesBefore, executor.ReceivedBatches.Count);   // no server work

        await session.StepAsync();                                     // SELECT @@ROWCOUNT
        Assert.Contains("_sh_rowcount int = 0", executor.ReceivedBatches[^1]);
        await session.TeardownAsync();
    }

    // ---- §10.3 step 4 unhandled-continue semantics (fact 21: probed live at the M3
    // ---- §10 line review — P1/P5-P8; the D3 model's continuation details corrected) ---

    [Fact]
    public async Task UnhandledContinued_ArmsFact18Shadows_ErrorNumberAndZeroRowcount()
    {
        // Fact 21 P5/P7 (and fact 18-D): after an UNHANDLED statement-level fault the
        // next statement natively reads @@ERROR = the fault number and @@ROWCOUNT = 0 —
        // the same observation as CATCH entry, WITHOUT any routing.
        const string script = "SELECT 1 / 0 AS a;\nSELECT @@ERROR AS e, @@ROWCOUNT AS r;";
        var executor = Init(new FakeStatementExecutor())
            .Then(_ => Fault(8134, "Divide by zero error encountered."))
            .Then(_ => Ok(userSets: new[] { Scalar("e", 8134) }));
        var session = ScriptSession(script, executor);
        await session.InitializeAsync();

        await session.StepAsync();                                     // unhandled fault, continues
        Assert.Equal(StepDisposition.UnhandledContinued, session.LastStep.Disposition);

        await session.StepAsync();                                     // next statement

        var nextBatch = executor.ReceivedBatches[^1];
        Assert.Contains("_sh_error int = 8134", nextBatch);
        Assert.Contains("_sh_rowcount int = 0", nextBatch);
        await session.TeardownAsync();
    }

    [Fact]
    public async Task UnhandledFaultedIfPredicate_TakesTheElseBranch_LikeNative()
    {
        // Fact 21 P1/P6: a faulted IF predicate with no enclosing TRY raises the error
        // AND takes the FALSE path — the ELSE branch runs (reading @@ERROR = the fault
        // number there), it is NOT a skip of the whole conditional.
        const string script = """
            IF 1 / 0 = 1
                SELECT 'then' AS t;
            ELSE
                SELECT 'else' AS e;
            SELECT 'after' AS z;
            """;
        var executor = Init(new FakeStatementExecutor())
            .Then(_ => Fault(8134, "Divide by zero error encountered."))
            .Then(_ => Ok(userSets: new[] { Scalar("e", "else") }))
            .Then(_ => Ok(userSets: new[] { Scalar("z", "after") }));
        var session = ScriptSession(script, executor);
        await session.InitializeAsync();

        var (_, messages) = await session.StepAsync();                 // predicate faults

        Assert.Equal(StepDisposition.UnhandledContinued, session.LastStep.Disposition);
        Assert.Contains(messages, m => m.Contains("Msg 8134"));
        Assert.Equal(4, session.Current!.Span.StartLine);              // the ELSE statement

        await session.StepAsync();
        Assert.Contains("'else'", executor.ReceivedBatches[^1]);       // the ELSE branch RAN
        await session.TeardownAsync();
    }

    [Fact]
    public async Task FaultedReturnExpression_Unhandled_ReturnsZeroWithNativeWarning()
    {
        // Fact 21 P8: a RETURN whose expression faults still RETURNS — natively the
        // procedure exits with "attempted to return a status of NULL ... 0 will be
        // returned instead" (an info message), and statements after the RETURN never
        // run. It is NOT a statement skip that continues the frame.
        const string procDef =
            "CREATE PROCEDURE dbo.uspFaultyReturn AS BEGIN SELECT 1 AS a; RETURN 1/0; SELECT 2 AS never; END";
        var defResultSet = new ResultSet(
            new[] { "def", "qi", "ansi_nulls" }, new IReadOnlyList<object?>[] { new object?[] { procDef, true, true } });
        var executor = new FakeStatementExecutor()
            .ThenEmpty()                                               // SET opts
            .Then(_ => new BatchResult(new[] { defResultSet }, Array.Empty<string>()))
            .ThenEmpty().ThenEmpty().ThenEmpty()                       // CREATE, seed, BEGIN TRAN
            .Then(_ => Ok(rc: 1, userSets: new[] { Scalar("a", 1) }))  // SELECT 1 AS a
            .Then(_ => Fault(8134, "Divide by zero error encountered."))   // RETURN 1/0 eval
            .ThenEmpty();                                              // teardown ROLLBACK
        var session = new Session(
            new SessionOptions("DEVSQL01", "SalesDb", LaunchMode.Procedure, "dbo.uspFaultyReturn", null, null), executor);

        var result = await session.RunToEndAsync();

        Assert.Equal(0, result.ReturnCode);
        Assert.Contains(result.Execution.Messages, m => m.Contains("Msg 8134"));
        Assert.Contains(result.Execution.Messages, m => m.Contains("return a status of NULL"));
        Assert.DoesNotContain(executor.ReceivedBatches, b => b.Contains("SELECT 2 AS never"));
    }

    [Fact]
    public async Task JumpTo_WithPendingFaultAtSite_AbandonsTheDeferredRoute()
    {
        // §10.6 'all' two-phase stop vs §13: jumping away from the fault site abandons
        // the pending deferred route — it must NOT fire later from the new position
        // (where it would suppress a statement the user asked to run).
        const string script = "SELECT 1 / 0 AS a;\nSELECT 2 AS b;\nSELECT 3 AS c;";
        var executor = Init(new FakeStatementExecutor())
            .Then(_ => Fault(8134, "Divide by zero error encountered."))
            .Then(_ => Ok(userSets: new[] { Scalar("c", 3) }));
        var session = ScriptSession(script, executor);
        await session.InitializeAsync();
        session.BreakOnAllErrors = true;

        await session.StepAsync();                                     // FaultAtSite, cursor on line 1
        Assert.Equal(StepDisposition.FaultAtSite, session.LastStep.Disposition);

        Assert.True(session.Index!.TryMapBreakpointLine(3, out var target));
        session.JumpTo(target);                                        // user skips the fault

        await session.StepAsync();                                     // must EXECUTE line 3

        Assert.Equal(StepDisposition.Performed, session.LastStep.Disposition);
        Assert.Contains("SELECT 3 AS c", executor.ReceivedBatches[^1]);
        await session.TeardownAsync();
    }

    // ---- §10.1 no-control-row classes -------------------------------------------------------

    [Fact]
    public async Task ExecutorTimeout_LeavesCursorOnUnit_NotBroken()
    {
        const string script = "SELECT 1 AS a;\nSELECT 2 AS b;";
        var executor = Init(new FakeStatementExecutor())
            .Then(_ => throw new StatementExecutionException("Execution Timeout Expired.", 11, -2))
            .Then(_ => Ok(userSets: new[] { Scalar("a", 1) }));
        var session = ScriptSession(script, executor);
        await session.InitializeAsync();

        await session.StepAsync();

        Assert.Equal(StepDisposition.EngineAttention, session.LastStep.Disposition);
        Assert.False(session.IsBroken);
        Assert.Equal(1, session.Current!.Span.StartLine);              // §10.5: still ON the unit — retry allowed

        await session.StepAsync();                                     // retry succeeds
        Assert.Equal(StepDisposition.Performed, session.LastStep.Disposition);
        await session.TeardownAsync();
    }

    [Fact]
    public async Task ExecutorAttentionByCancel_LeavesCursorOnUnit_NotBroken()
    {
        // §10 line review F3: SqlCommand.Cancel() (the §10.5 pause mechanism) arrives
        // as SqlException Number 0 ("Operation cancelled by user."). Before the fix
        // this fell through to the compile-class arm and bricked the session — a pause
        // must behave exactly like a timeout: cursor stays, retry allowed, not broken.
        const string script = "SELECT 1 AS a;\nSELECT 2 AS b;";
        var executor = Init(new FakeStatementExecutor())
            .Then(_ => throw new StatementExecutionException("Operation cancelled by user.", 11, 0))
            .Then(_ => Ok(userSets: new[] { Scalar("a", 1) }));
        var session = ScriptSession(script, executor);
        await session.InitializeAsync();

        await session.StepAsync();

        Assert.Equal(StepDisposition.EngineAttention, session.LastStep.Disposition);
        Assert.False(session.IsBroken);
        Assert.Equal(1, session.Current!.Span.StartLine);              // §10.5: still ON the unit — retry allowed

        await session.StepAsync();                                     // retry succeeds
        Assert.Equal(StepDisposition.Performed, session.LastStep.Disposition);
        await session.TeardownAsync();
    }

    [Fact]
    public async Task ExecutorCompileClass_IsTerminalForFrameZero()
    {
        const string script = "SELECT * FROM dbo.DoesNotExist;\nSELECT 2 AS never;";
        var executor = Init(new FakeStatementExecutor())
            .Then(_ => throw new StatementExecutionException("Invalid object name 'dbo.DoesNotExist'.", 16, 208));
        var session = ScriptSession(script, executor);
        await session.InitializeAsync();

        await session.StepAsync();

        Assert.Equal(StepDisposition.FrameFaulted, session.LastStep.Disposition);
        Assert.True(session.IsBroken);
        Assert.Equal(208, session.LastStep.Error!.Number);
        await session.TeardownAsync();
    }

    [Fact]
    public async Task ExecutorSeverity20_ThrowsSessionFatal()
    {
        const string script = "SELECT 1 AS a;";
        var executor = Init(new FakeStatementExecutor())
            .Then(_ => throw new StatementExecutionException("connection is dead", 20, 0));
        var session = ScriptSession(script, executor);
        await session.InitializeAsync();

        await Assert.ThrowsAsync<SessionFaultException>(() => session.StepAsync());
        Assert.True(session.IsBroken);
        await session.TeardownAsync();
    }

    // ---- launch warnings + XACT_ABORT sandwich ----------------------------------------------

    [Fact]
    public async Task CommitInBody_ProducesLaunchWarning()
    {
        const string script = "BEGIN TRAN;\nCOMMIT;";
        var session = ScriptSession(script, Init(new FakeStatementExecutor()));
        await session.InitializeAsync();

        var warning = Assert.Single(session.LaunchWarnings);
        Assert.Contains("COMMIT", warning);
        Assert.Contains("line 2", warning);
        await session.TeardownAsync();
    }

    [Fact]
    public async Task EvaluateConditionAsync_ExecutorFailure_ReturnsFaultMessage_DoesNotThrow()
    {
        // §10 line review F5: EvaluateConditionAsync's contract is "reported, not
        // thrown" on every fault path. A no-control-row executor failure (timeout,
        // compile-class) during a condition eval must come back as a FaultMessage like
        // any other faulting condition, not escape as an exception.
        const string script = "SELECT 1 AS a;";
        var executor = Init(new FakeStatementExecutor())
            .Then(_ => throw new StatementExecutionException("Execution Timeout Expired.", 11, -2));
        var session = ScriptSession(script, executor);
        await session.InitializeAsync();

        var (value, fault) = await session.EvaluateConditionAsync("1 = 1");

        Assert.Null(value);
        Assert.Contains("faulted", fault);
        Assert.Contains("Execution Timeout Expired", fault);
        await session.TeardownAsync();
    }

    [Fact]
    public async Task ConditionEval_UnderDebuggeeXactAbortOn_GetsTheSandwich()
    {
        const string script = "SET XACT_ABORT ON;\nSELECT 1 AS a;";
        var executor = Init(new FakeStatementExecutor())
            .Then(_ => Ok())                                            // SET XACT_ABORT ON executes
            .Then(_ => Ok(userSets: new[] { new ResultSet(new[] { "p" }, new IReadOnlyList<object?>[] { new object?[] { 1 } }) }));
        var session = ScriptSession(script, executor);
        await session.InitializeAsync();
        await session.StepAsync();                                      // tracks XactAbortOn = true

        var (value, fault) = await session.EvaluateConditionAsync("1 = 1");

        Assert.True(value);
        Assert.Null(fault);
        var conditionBatch = executor.ReceivedBatches[^1];
        Assert.Contains("SET XACT_ABORT OFF;", conditionBatch);         // fact 19: don't doom via a condition
        Assert.EndsWith("SET XACT_ABORT ON;\n", conditionBatch);        // debuggee setting restored
        await session.TeardownAsync();
    }
}
