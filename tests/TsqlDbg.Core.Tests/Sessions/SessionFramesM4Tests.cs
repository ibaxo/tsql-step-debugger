// M4 (Fable) — DESIGN §11 frames driven end-to-end through Session.StepAsync with a
// fake executor: step-into push (§11.3), completion-gated pops (§11.5 per engine fact
// 23: copy-back + @rc iff the call ran to completion), the §10.3 cross-frame routing
// walk (incl. the compile-class caller-start rule, facts 1b/6/23-F), fact 23-H's
// continue-in-the-callee, doomed pops doing no server work, the §10.4 A9 multi-frame
// reseed, the §9 registry through R1/R2, and §11.2 SET restores (D6). Design:
// docs/archive/reviews/m4-frames-design-notes-fable.md; engine truths: facts 2/9/14/22/23.
using TsqlDbg.Core.Execution;
using TsqlDbg.Core.Interpreter;
using TsqlDbg.Core.Sessions;
using TsqlDbg.Core.Tests.Fakes;
using Xunit;

namespace TsqlDbg.Core.Tests.Sessions;

public sealed class SessionFramesM4Tests
{
    // ---- fake-batch helpers (shapes mirror the real builders' output) ----------------

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

    private static BatchResult Row(params object?[] values) => new(
        new[]
        {
            new ResultSet(
                Enumerable.Range(0, values.Length).Select(i => $"c{i}").ToArray(),
                new IReadOnlyList<object?>[] { values }),
        },
        Array.Empty<string>());

    // The step-into module fetch's single row (§11.3/D4).
    private static BatchResult ModuleRow(string definition) => new(
        new[]
        {
            new ResultSet(new[] { "def", "qi", "ansi_nulls", "schema_name", "name" },
                new IReadOnlyList<object?>[] { new object?[] { definition, true, true, "dbo", "child" } }),
        },
        Array.Empty<string>());

    private static Session ScriptSession(string script, FakeStatementExecutor executor)
        => new(new SessionOptions("DEVSQL01", "SalesDb", LaunchMode.Script, null, null, script), executor);

    private static FakeStatementExecutor Init(FakeStatementExecutor executor)
        => executor.ThenEmpty().ThenEmpty().ThenEmpty().ThenEmpty();   // SET opts, CREATE s0, seed, BEGIN TRAN

    /// <summary>Queues the §11.3 push traffic for a one-arg callee: module fetch, one
    /// arg eval, CREATE s{n}, seed INSERT, parameterized reseed, SELECT * read-back.</summary>
    private static FakeStatementExecutor QueuePush(FakeStatementExecutor executor, string calleeDef, object? argValue)
        => executor
            .Then(_ => ModuleRow(calleeDef))
            .Then(_ => Ok(userSets: new[] { Scalar("p", argValue) }))
            .ThenEmpty()
            .ThenEmpty()
            .ThenEmpty()
            .Then(_ => Row(argValue));

    // ---- §11.3 push + §11.5 completed pop (fact 23 A) --------------------------------

    [Fact]
    public async Task StepInto_PushesFrame_AndCompletedPop_CopiesOutputAndRc()
    {
        const string script = """
            DECLARE @x int = 5, @rc int;
            EXEC @rc = dbo.child @a = @x OUTPUT;
            SELECT @rc AS rc;
            """;
        const string calleeDef = """
            CREATE PROCEDURE dbo.child @a int OUTPUT AS
            BEGIN
            SET @a = @a + 1;
            RETURN 7;
            END
            """;
        var executor = Init(new FakeStatementExecutor())
            .Then(_ => Ok(state: new object?[] { 5, null }));          // DECLARE initializer (@x = 5)
        QueuePush(executor, calleeDef, 5)
            .Then(_ => Ok(state: new object?[] { 6 }))                 // callee: SET @a = @a + 1
            .Then(_ => Ok(userSets: new[] { Scalar("p", 7) }))         // callee: RETURN 7 eval
            .ThenEmpty()                                               // pop: copy-back UPDATE
            .Then(_ => Row(6, 7))                                      // pop: caller SELECT * read-back
            .ThenEmpty();                                              // pop: cleanup (drop s1)
        var session = ScriptSession(script, executor);
        await session.InitializeAsync();
        await session.StepAsync();                                     // DECLARE

        await session.StepAsync(StepKind.Into);                        // EXEC → push

        Assert.Equal(StepDisposition.SteppedIn, session.LastStep.Disposition);
        Assert.Equal(2, session.Frames.Count);
        Assert.Equal("dbo.child", session.TopFrame!.Module.Display);
        Assert.Equal(3, session.Current!.Span.StartLine);              // callee's SET line
        var calleeVariable = Assert.Single(session.TopFrame.Variables.All);
        Assert.Equal("@a", calleeVariable.Declaration.Name);
        Assert.Equal(new object?[] { 5 }, session.TopFrame.Snapshot);  // D1: seeded snapshot at push

        await session.StepAsync();                                     // SET @a = @a + 1
        await session.StepAsync();                                     // RETURN 7 → completed pop (A54: explicit RETURN is NEVER parked)

        Assert.Equal(StepDisposition.FrameCompleted, session.LastStep.Disposition);
        Assert.False(session.AtImplicitReturn);                        // A54: an explicit RETURN pops in ONE step (it is already a stoppable line)
        Assert.Single(session.Frames);
        Assert.Equal(3, session.Current!.Span.StartLine);              // caller resumed after the EXEC

        // Fact 23-A: copy-back + @rc, server-side and type-faithful (§11.5). Matched by
        // the cross-table subselect — the §7.1 postamble's own state write also says
        // "UPDATE #__dbg_s0 SET".
        var copyBack = Assert.Single(executor.ReceivedBatches, b => b.Contains("(SELECT [a] FROM #__dbg_s1)"));
        Assert.Contains("UPDATE #__dbg_s0 SET [x] = (SELECT [a] FROM #__dbg_s1)", copyBack);
        Assert.Contains("[rc] = CONVERT(int, @__dbg", copyBack);
        Assert.Contains(executor.ReceivedParameters.Values, p => p.Any(x => Equals(x.Value, 7)));   // __ret rode a parameter
        Assert.Contains(executor.ReceivedBatches, b => b.Contains("DROP TABLE #__dbg_s1"));
        await session.TeardownAsync();
    }

    // ---- A54 (§6/§11.5): a body-end callee (no explicit RETURN) PARKS at its implicit
    // return for one inspection stop, THEN the next step performs the §11.5 pop. --------

    [Fact]
    public async Task BodyEndCallee_ParksAtImplicitReturn_ThenCompletedPop()
    {
        const string script = """
            DECLARE @x int = 5;
            EXEC dbo.child @a = @x;
            SELECT @x AS x;
            """;
        const string calleeDef = """
            CREATE PROCEDURE dbo.child @a int AS
            BEGIN
            SET @a = @a + 1;
            END
            """;
        var executor = Init(new FakeStatementExecutor())
            .Then(_ => Ok(state: new object?[] { 5 }));                // DECLARE @x = 5
        QueuePush(executor, calleeDef, 5)
            .Then(_ => Ok(state: new object?[] { 6 }))                 // callee: SET @a = @a + 1 → runs off the body end
            .ThenEmpty();                                              // pop cleanup (DROP s1) — no OUTPUT/@rc, so no copy-back
        var session = ScriptSession(script, executor);
        await session.InitializeAsync();
        await session.StepAsync();                                     // DECLARE
        await session.StepAsync(StepKind.Into);                        // EXEC → push
        Assert.Equal(2, session.Frames.Count);
        Assert.Equal(3, session.Current!.Span.StartLine);             // callee's SET (calleeDef line 3)

        // The last body statement runs off the end → PARK, not pop.
        await session.StepAsync();                                     // callee SET → implicit return
        Assert.Equal(StepDisposition.AtImplicitReturn, session.LastStep.Disposition);
        Assert.True(session.AtImplicitReturn);
        Assert.Equal(2, session.Frames.Count);                         // callee still on the stack (state table intact)
        Assert.Null(session.Current);                                  // cursor completed, awaiting the pop
        Assert.False(session.IsCompleted);
        Assert.Equal("dbo.child", session.TopFrame!.Module.Display);   // still inside the callee's own scope
        Assert.DoesNotContain(executor.ReceivedBatches, b => b.Contains("DROP TABLE #__dbg_s1"));  // NOT yet popped

        // The next step performs the deferred §11.5 pop and lands back in the caller.
        await session.StepAsync();                                     // consume the park → completed pop
        Assert.Equal(StepDisposition.FrameCompleted, session.LastStep.Disposition);
        Assert.False(session.AtImplicitReturn);
        Assert.Single(session.Frames);
        Assert.Equal(3, session.Current!.Span.StartLine);             // caller's SELECT @x (script line 3)
        Assert.Contains(executor.ReceivedBatches, b => b.Contains("DROP TABLE #__dbg_s1"));  // the pop ran now
        await session.TeardownAsync();
    }

    // ---- A54: the ad-hoc SCRIPT frame 0 is NOT a module — its body end just completes,
    // with no implicit-return park (a script has no OUTPUT params / return code). --------

    [Fact]
    public async Task ScriptFrame0_BodyEnd_DoesNotPark()
    {
        const string script = """
            DECLARE @x int = 1;
            SELECT @x AS x;
            """;
        var executor = Init(new FakeStatementExecutor())
            .Then(_ => Ok(state: new object?[] { 1 }))                            // DECLARE @x = 1
            .Then(_ => Ok(userSets: new[] { Scalar("x", 1) }, state: new object?[] { 1 }));  // SELECT @x → last SU of the script
        var session = ScriptSession(script, executor);
        await session.InitializeAsync();
        await session.StepAsync();                                     // DECLARE

        await session.StepAsync();                                     // SELECT @x → script body end
        Assert.False(session.AtImplicitReturn);                        // the script frame never parks
        Assert.NotEqual(StepDisposition.AtImplicitReturn, session.LastStep.Disposition);
        Assert.True(session.IsCompleted);                              // it simply completed
        await session.TeardownAsync();
    }

    // ---- fact 23 C: caller TRY converts a callee fault into an ABORTED call ----------

    [Fact]
    public async Task CalleeFault_UnderCallerTry_RoutesToCallerCatch_WithoutCopyBack()
    {
        const string script = """
            DECLARE @x int, @rc int;
            BEGIN TRY
            EXEC @rc = dbo.child @a = @x OUTPUT;
            END TRY
            BEGIN CATCH
            SELECT ERROR_NUMBER() AS n;
            END CATCH
            """;
        const string calleeDef = """
            CREATE PROCEDURE dbo.child @a int OUTPUT AS
            BEGIN
            SELECT 1/0 AS boom;
            RETURN 9;
            END
            """;
        var executor = Init(new FakeStatementExecutor());
        QueuePush(executor, calleeDef, null)
            .Then(_ => Fault(8134, "Divide by zero error encountered."))
            .ThenEmpty();                                              // abnormal pop: cleanup only
        var session = ScriptSession(script, executor);
        await session.InitializeAsync();
        await session.StepAsync();                                     // DECLARE (no initializers, no batch)
        await session.StepAsync(StepKind.Into);
        Assert.Equal(2, session.Frames.Count);

        await session.StepAsync();                                     // callee SELECT 1/0 → fault

        Assert.Equal(StepDisposition.RoutedToCatch, session.LastStep.Disposition);
        Assert.Single(session.Frames);                                 // callee abnormal-popped
        Assert.Equal(6, session.Current!.Span.StartLine);              // caller's CATCH statement
        Assert.Equal(8134, session.ActiveErrorContext!.Values.Number);
        Assert.Equal(1, session.ActiveErrorContext.OriginFrame);       // fault originated in the callee

        // Fact 23 C-G: an aborted call never copies back and never assigns @rc.
        Assert.DoesNotContain(executor.ReceivedBatches, b => b.Contains("(SELECT [a] FROM #__dbg_s1)"));
        Assert.Contains(executor.ReceivedBatches, b => b.Contains("DROP TABLE #__dbg_s1"));
        await session.TeardownAsync();
    }

    // ---- fact 23 H: no TRY anywhere → the callee CONTINUES and completes --------------

    [Fact]
    public async Task StatementLevelFault_NoTryAnywhere_ContinuesInCallee_ThenCompletedPopCopiesBack()
    {
        const string script = """
            DECLARE @x int, @rc int;
            EXEC @rc = dbo.child @a = @x OUTPUT;
            SELECT @rc AS rc;
            """;
        const string calleeDef = """
            CREATE PROCEDURE dbo.child @a int OUTPUT AS
            BEGIN
            SET @a = 31;
            RAISERROR('boom', 16, 1);
            SET @a = 32;
            RETURN 9;
            END
            """;
        var executor = Init(new FakeStatementExecutor());
        QueuePush(executor, calleeDef, null)
            .Then(_ => Ok(state: new object?[] { 31 }))                // SET @a = 31
            .Then(_ => Fault(50000, "boom"))                           // RAISERROR unhandled
            .Then(_ => Ok(state: new object?[] { 32 }))                // SET @a = 32 (native continuation!)
            .Then(_ => Ok(userSets: new[] { Scalar("p", 9) }))         // RETURN 9
            .ThenEmpty()                                               // pop: copy-back
            .Then(_ => Row(32, 9))                                     // pop: caller read-back
            .ThenEmpty();                                              // pop: cleanup
        var session = ScriptSession(script, executor);
        await session.InitializeAsync();
        await session.StepAsync();
        await session.StepAsync(StepKind.Into);
        await session.StepAsync();                                     // SET @a = 31

        await session.StepAsync();                                     // RAISERROR → unhandled

        Assert.Equal(StepDisposition.UnhandledContinued, session.LastStep.Disposition);
        Assert.Equal(2, session.Frames.Count);                         // STILL in the callee (fact 23-H)
        Assert.Equal(5, session.Current!.Span.StartLine);              // SET @a = 32 runs next

        await session.StepAsync();                                     // SET @a = 32
        await session.StepAsync();                                     // RETURN 9 → COMPLETED pop

        Assert.Equal(StepDisposition.FrameCompleted, session.LastStep.Disposition);
        Assert.Single(session.Frames);
        // Fact 23-H: completed-despite-errors → copy-back and @rc DO happen.
        var copyBack = Assert.Single(executor.ReceivedBatches, b => b.Contains("(SELECT [a] FROM #__dbg_s1)"));
        Assert.Contains("UPDATE #__dbg_s0 SET [x] = (SELECT [a] FROM #__dbg_s1)", copyBack);
        await session.TeardownAsync();
    }

    // ---- facts 1b/6/23-F: compile-class skips the callee's OWN TRY --------------------

    [Fact]
    public async Task CompileClassFault_SkipsCalleeOwnTry_RoutesToCallerCatch()
    {
        const string script = """
            DECLARE @x int, @rc int;
            BEGIN TRY
            EXEC @rc = dbo.child @a = @x OUTPUT;
            END TRY
            BEGIN CATCH
            SELECT ERROR_NUMBER() AS n;
            END CATCH
            """;
        const string calleeDef = """
            CREATE PROCEDURE dbo.child @a int OUTPUT AS
            BEGIN
            BEGIN TRY
            SELECT * FROM dbo.missing;
            END TRY
            BEGIN CATCH
            SELECT 1 AS never;
            END CATCH
            END
            """;
        var executor = Init(new FakeStatementExecutor());
        QueuePush(executor, calleeDef, null)
            .Then(_ => throw new StatementExecutionException("Invalid object name 'dbo.missing'.", 16, 208))
            .ThenEmpty();                                              // abnormal pop cleanup
        var session = ScriptSession(script, executor);
        await session.InitializeAsync();
        await session.StepAsync();
        await session.StepAsync(StepKind.Into);

        await session.StepAsync();                                     // callee SELECT → no-control-row 208

        // Same-scope-uncatchable: the callee's own CATCH ("never") must NOT run; the
        // caller's does (fact 23-F), with the callee popped abnormally.
        Assert.Equal(StepDisposition.RoutedToCatch, session.LastStep.Disposition);
        Assert.False(session.IsBroken);
        Assert.Single(session.Frames);
        Assert.Equal(6, session.Current!.Span.StartLine);              // caller's CATCH
        Assert.Equal(208, session.ActiveErrorContext!.Values.Number);
        await session.TeardownAsync();
    }

    // ---- fact 23-G + fact 22: a dooming callee fault → abnormal pop, no server work ---

    [Fact]
    public async Task DoomedCalleeFault_RoutesToCallerCatch_PopDoesNoServerWork()
    {
        const string script = """
            DECLARE @x int, @rc int;
            BEGIN TRY
            EXEC @rc = dbo.child @a = @x OUTPUT;
            END TRY
            BEGIN CATCH
            SELECT XACT_STATE() AS xs;
            END CATCH
            """;
        const string calleeDef = """
            CREATE PROCEDURE dbo.child @a int OUTPUT AS
            BEGIN
            SELECT CONVERT(int, 'x') AS boom;
            RETURN 0;
            END
            """;
        var executor = Init(new FakeStatementExecutor());
        QueuePush(executor, calleeDef, null)
            .Then(_ => Fault(245, "Conversion failed.", xactState: -1, trancount: 1));
        var session = ScriptSession(script, executor);
        await session.InitializeAsync();
        await session.StepAsync();
        await session.StepAsync(StepKind.Into);
        var batchesBeforeFault = executor.ReceivedBatches.Count;

        await session.StepAsync();                                     // dooming fault

        Assert.Equal(StepDisposition.RoutedToCatch, session.LastStep.Disposition);
        Assert.True(session.IsDoomed);
        Assert.Single(session.Frames);
        // D2: a doomed pop is pure bookkeeping — the fact-22 forced rollback already
        // reaped every mid-transaction object; only the fault batch itself was sent.
        Assert.Equal(batchesBeforeFault + 1, executor.ReceivedBatches.Count);
        Assert.DoesNotContain(executor.ReceivedBatches, b => b.Contains("DROP TABLE #__dbg_s1"));
        await session.TeardownAsync();
    }

    // ---- §10.4 A9 across frames: the detached edge reseeds EVERY frame ----------------

    [Fact]
    public async Task DebuggeeRollback_InCallee_ReseedsBothFrames_AndRecreatesCalleeTable()
    {
        const string script = """
            DECLARE @x int, @rc int;
            EXEC @rc = dbo.child @a = @x OUTPUT;
            SELECT @rc AS rc;
            """;
        const string calleeDef = """
            CREATE PROCEDURE dbo.child @a int OUTPUT AS
            BEGIN
            ROLLBACK;
            SET @a = 1;
            RETURN 0;
            END
            """;
        var executor = Init(new FakeStatementExecutor());
        QueuePush(executor, calleeDef, null)
            .Then(_ => Ok(trancount: 0, state: new object?[] { null }))    // ROLLBACK → edge
            .ThenEmpty()                                                   // create-if-missing #__dbg_s1
            .ThenEmpty();                                                  // reseed #__dbg_s1
        var session = ScriptSession(script, executor);
        await session.InitializeAsync();
        await session.StepAsync();
        await session.StepAsync(StepKind.Into);

        await session.StepAsync();                                     // ROLLBACK

        Assert.True(session.IsTransactionDetached);
        Assert.Equal(2, session.Frames.Count);                         // still stepping in the callee
        Assert.Contains(executor.ReceivedBatches,
            b => b.Contains("IF OBJECT_ID('tempdb..#__dbg_s1') IS NULL BEGIN CREATE TABLE #__dbg_s1"));
        Assert.Contains(executor.ReceivedBatches.Skip(executor.ReceivedBatches.Count - 2),
            b => b.Contains("UPDATE #__dbg_s1 SET"));
        await session.TeardownAsync();
    }

    // ---- §9 registry through R2 (temp tables): create / chain reference / drop --------

    [Fact]
    public async Task TempTableLifecycle_RenamesRegistersAndMarksDead()
    {
        const string script = """
            CREATE TABLE #w (a int);
            INSERT INTO #w VALUES (1);
            DROP TABLE #w;
            SELECT 1 AS x;
            """;
        var executor = Init(new FakeStatementExecutor())
            .Then(_ => Ok()).Then(_ => Ok()).Then(_ => Ok());
        var session = ScriptSession(script, executor);
        await session.InitializeAsync();

        // A20 (ratified 2026-07-06): a non-colliding create keeps its ORIGINAL name —
        // no rename, no reference patches (revised from always-rename as the ratified
        // change, docs/archive/reviews/m5-a20-r2-collision-rename-fable.md).
        await session.StepAsync();                                     // CREATE
        Assert.Contains("CREATE TABLE #w (a int);", executor.ReceivedBatches[^1]);
        Assert.DoesNotContain("__f0", executor.ReceivedBatches[^1].Split('\n').First(l => l.Contains("CREATE TABLE #w")));
        var entry = Assert.Single(session.TopFrame!.TempObjects.All);
        Assert.Equal("#w", entry.OriginalName);
        Assert.Equal("#w", entry.PhysicalName);
        Assert.False(entry.IsDead);
        Assert.Equal(1, entry.CreatedAtTrancount);

        await session.StepAsync();                                     // INSERT — resolves to itself, unpatched
        Assert.Contains("INSERT INTO #w VALUES (1);", executor.ReceivedBatches[^1]);

        await session.StepAsync();                                     // DROP — unpatched AND marks dead
        Assert.Contains("DROP TABLE #w;", executor.ReceivedBatches[^1]);
        Assert.True(entry.IsDead);
        await session.TeardownAsync();
    }

    // A20's collision arm end-to-end: a stepped-into callee creating a #temp the
    // caller already holds live gets the minted rename (the flattened connection
    // cannot hold two same-named temps); the caller's own references stay original.
    [Fact]
    public async Task TempTableCollision_CalleeCreateRenames_CallerStaysOriginal()
    {
        const string script = """
            CREATE TABLE #w (a int);
            EXEC dbo.child @a = 2;
            INSERT INTO #w VALUES (1);
            """;
        const string calleeDef = """
            CREATE PROCEDURE dbo.child @a int AS
            BEGIN
            CREATE TABLE #w (b int);
            INSERT INTO #w VALUES (@a);
            RETURN 0;
            END
            """;
        var executor = Init(new FakeStatementExecutor())
            .Then(_ => Ok());                                          // caller CREATE #w
        QueuePush(executor, calleeDef, 2)
            .Then(_ => Ok())                                           // callee CREATE #w → renamed
            .Then(_ => Ok())                                           // callee INSERT → renamed ref
            .Then(_ => Ok(userSets: new[] { Scalar("p", 0) }))         // RETURN 0 eval
            .ThenEmpty()                                               // pop: cleanup (drop s1 + callee #w__f1)
            .Then(_ => Ok());                                          // caller INSERT — original ref
        var session = ScriptSession(script, executor);
        await session.InitializeAsync();

        await session.StepAsync();                                     // caller CREATE — original name
        Assert.Contains("CREATE TABLE #w (a int);", executor.ReceivedBatches[^1]);
        Assert.Equal("#w", Assert.Single(session.TopFrame!.TempObjects.All).PhysicalName);

        await session.StepAsync(StepKind.Into);                        // push
        var calleeOrdinal = session.TopFrame!.Ordinal;

        await session.StepAsync();                                     // callee CREATE — COLLISION → renamed
        Assert.Contains($"CREATE TABLE [#w__f{calleeOrdinal}]", executor.ReceivedBatches[^1]);
        var calleeEntry = Assert.Single(session.TopFrame.TempObjects.All);
        Assert.Equal("#w", calleeEntry.OriginalName);
        Assert.Equal($"#w__f{calleeOrdinal}", calleeEntry.PhysicalName);

        await session.StepAsync();                                     // callee INSERT — innermost wins, patched
        Assert.Contains($"INSERT INTO [#w__f{calleeOrdinal}]", executor.ReceivedBatches[^1]);

        await session.StepAsync();                                     // RETURN → completed pop
        Assert.Equal(StepDisposition.FrameCompleted, session.LastStep.Disposition);

        await session.StepAsync();                                     // caller INSERT — original, unpatched
        Assert.Contains("INSERT INTO #w VALUES (1);", executor.ReceivedBatches[^1]);
        await session.TeardownAsync();
    }

    // M5-gate regression (finding E1, A20 line-read): the ratified §7.4 R2 text renames
    // ONLY on collision with a live OUTER entry — a live entry in the CREATING frame
    // itself is not a rename trigger. Keeping the original name lets the server raise
    // the native 2714 (duplicate #temp within one scope); a rename would make the
    // duplicate CREATE silently succeed — an infidelity manufactured by the debugger,
    // and a regression from pre-A20 (both creates then targeted the same minted name,
    // so 2714 still surfaced).
    [Fact]
    public async Task TempTableDuplicateCreate_SameFrame_KeepsOriginalName()
    {
        const string script = """
            CREATE TABLE #w (a int);
            CREATE TABLE #w (b int);
            SELECT 1 AS x;
            """;
        var executor = Init(new FakeStatementExecutor())
            .Then(_ => Ok()).Then(_ => Ok());
        var session = ScriptSession(script, executor);
        await session.InitializeAsync();

        await session.StepAsync();                                     // first CREATE — original name
        Assert.Contains("CREATE TABLE #w (a int);", executor.ReceivedBatches[^1]);

        await session.StepAsync();                                     // second CREATE — same-frame #w is NOT an outer collision
        Assert.Contains("CREATE TABLE #w (b int);", executor.ReceivedBatches[^1]);
        Assert.DoesNotContain("__f0", executor.ReceivedBatches[^1].Split('\n').First(l => l.Contains("CREATE TABLE #w")));
        Assert.Collection(session.TopFrame!.TempObjects.All,
            first => Assert.Equal("#w", first.PhysicalName),
            second =>
            {
                Assert.Equal("#w", second.OriginalName);
                Assert.Equal("#w", second.PhysicalName);               // red pre-fix: recorded as #w__f0
            });
        await session.TeardownAsync();
    }

    // ---- §9/R1 (D7): table-variable realization hoisted at init, refs patched ---------

    [Fact]
    public async Task TableVariable_RealizationHoistedBeforeBeginTran_AndReferencesPatch()
    {
        const string script = """
            DECLARE @t TABLE (a int);
            INSERT INTO @t VALUES (1);
            SELECT * FROM @t;
            """;
        var executor = new FakeStatementExecutor()
            .ThenEmpty().ThenEmpty().ThenEmpty()                       // SET opts, CREATE s0, seed
            .ThenEmpty()                                               // HOIST: CREATE #__dbgtv_0_t
            .ThenEmpty()                                               // BEGIN TRAN
            .Then(_ => Ok()).Then(_ => Ok());
        var session = ScriptSession(script, executor);
        await session.InitializeAsync();

        // D7: hoisted BEFORE BEGIN TRAN — created-at 0, structurally rollback-proof
        // like a native table variable (contents are C25).
        Assert.Contains("CREATE TABLE [#__dbgtv_0_t] (a int)", executor.ReceivedBatches[3]);
        Assert.Equal("BEGIN TRANSACTION;", executor.ReceivedBatches[4]);
        var entry = Assert.Single(session.TopFrame!.TempObjects.All);
        Assert.Equal(0, entry.CreatedAtTrancount);
        Assert.NotNull(entry.RecreateDdl);

        await session.StepAsync();                                     // DECLARE @t TABLE — stoppable no-op
        Assert.Equal(StepDisposition.Performed, session.LastStep.Disposition);
        Assert.Equal(5, executor.ReceivedBatches.Count);               // no server work for the SU

        await session.StepAsync();                                     // INSERT INTO @t
        Assert.Contains("INSERT INTO [#__dbgtv_0_t]", executor.ReceivedBatches[^1]);
        await session.TeardownAsync();
    }

    // ---- §11.2/D6 (fact 9): pops restore runtime SET options the callee changed -------

    [Fact]
    public async Task Pop_RestoresRuntimeSetOptions_TheCalleeChanged()
    {
        const string script = """
            DECLARE @x int;
            EXEC dbo.child @a = @x OUTPUT;
            SELECT 1 AS z;
            """;
        const string calleeDef = """
            CREATE PROCEDURE dbo.child @a int OUTPUT AS
            BEGIN
            SET ARITHABORT ON;
            RETURN 0;
            END
            """;
        var executor = Init(new FakeStatementExecutor());
        QueuePush(executor, calleeDef, null)
            .Then(_ => Ok(state: new object?[] { null }))              // SET ARITHABORT ON
            .Then(_ => Ok(userSets: new[] { Scalar("p", 0) }))         // RETURN 0 expression eval
            .ThenEmpty()                                               // pop: copy-back (OUTPUT pair)
            .Then(_ => Row((object?)null))                             // pop: caller read-back
            .ThenEmpty();                                              // pop: cleanup + restores
        var session = ScriptSession(script, executor);
        await session.InitializeAsync();
        await session.StepAsync();
        await session.StepAsync(StepKind.Into);
        await session.StepAsync();                                     // SET ARITHABORT ON

        await session.StepAsync();                                     // RETURN 0 → completed pop

        Assert.Equal(StepDisposition.FrameCompleted, session.LastStep.Disposition);
        var cleanup = executor.ReceivedBatches[^1];
        Assert.Contains("DROP TABLE #__dbg_s1", cleanup);
        Assert.Contains("SET ARITHABORT OFF;", cleanup);               // fact 9: reverted at module exit
        await session.TeardownAsync();
    }

    // ---- ineligible callee shapes fall back to step-over with a note ------------------

    [Fact]
    public async Task StepInto_DynamicSql_FallsBackToStepOver_WithNote()
    {
        const string script = """
            DECLARE @s nvarchar(100) = N'SELECT 1';
            EXEC (@s);
            SELECT 1 AS z;
            """;
        var executor = Init(new FakeStatementExecutor())
            .Then(_ => Ok(state: new object?[] { "SELECT 1" }))        // DECLARE initializer
            .Then(_ => Ok(state: new object?[] { "SELECT 1" }));       // the EXEC itself, stepped over
        var session = ScriptSession(script, executor);
        await session.InitializeAsync();
        await session.StepAsync();

        var (_, messages) = await session.StepAsync(StepKind.Into);

        Assert.Equal(StepDisposition.Performed, session.LastStep.Disposition);
        Assert.Single(session.Frames);                                 // no push
        Assert.Contains(messages, m => m.Contains("C10"));
        await session.TeardownAsync();
    }

    // ---- O6 (§5.5): the routing walk runs on CancellationToken.None ------------------

    [Fact]
    public async Task PauseRacingTheRoutingWalk_AbnormalPopCleanupRunsOnNone_RouteCompletes()
    {
        // O6 (§5.5, ratified): once the callee's fault control row exists the §10.3
        // routing walk is post-outcome bookkeeping under rider-(i) — a §10.5 pause whose
        // token cancelled DURING the fault must never abort the abnormal-pop cleanup (that
        // would leave a half-popped frame stack). The executor mimics SqlClient's token
        // check (fact 30): the cleanup batch, if issued on the cancelled step token, would
        // throw before reaching the server. O6 dropped the token from PerformRouteAsync
        // entirely (compile-time enforcement), so the whole route runs on None and
        // completes. (Pre-O6 this throws at the DROP #__dbg_s1 cleanup.)
        const string script = """
            DECLARE @x int, @rc int;
            BEGIN TRY
            EXEC @rc = dbo.child @a = @x OUTPUT;
            END TRY
            BEGIN CATCH
            SELECT ERROR_NUMBER() AS n;
            END CATCH
            """;
        const string calleeDef = """
            CREATE PROCEDURE dbo.child @a int OUTPUT AS
            BEGIN
            SELECT 1/0 AS boom;
            END
            """;
        using var pause = new CancellationTokenSource();
        var executor = Init(new FakeStatementExecutor { ThrowOnCancelledToken = true });
        QueuePush(executor, calleeDef, null)
            .Then(_ =>
            {
                pause.Cancel();                                        // the pause races the callee's fault control row
                return Fault(8134, "Divide by zero error encountered.");
            })
            .ThenEmpty();                                              // abnormal-pop cleanup (DROP s1) — must still run
        var session = ScriptSession(script, executor);
        await session.InitializeAsync();
        await session.StepAsync();
        await session.StepAsync(StepKind.Into);                        // push (before the pause)

        await session.StepAsync(pause.Token);                          // callee 1/0 → route under the racing pause

        Assert.Equal(StepDisposition.RoutedToCatch, session.LastStep.Disposition);   // the route completed
        Assert.Single(session.Frames);                                 // callee abnormal-popped — stack consistent
        Assert.Equal(6, session.Current!.Span.StartLine);              // cursor rests at the caller's CATCH
        Assert.Contains(executor.ReceivedBatches, b => b.Contains("DROP TABLE #__dbg_s1"));   // cleanup ran (not starved)
        await session.TeardownAsync();
    }
}
