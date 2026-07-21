using TsqlDbg.Core.Execution;
using TsqlDbg.Core.Sessions;
using TsqlDbg.Core.Tests.Fakes;

namespace TsqlDbg.Core.Tests.Sessions;

// DESIGN §4 session init sequence + §6/§7.1/§8 per-SU driver loop (M1). Driven
// entirely through IStatementExecutor per DESIGN §3. Composed-batch *text* formatting
// is covered by ComposedBatchBuilderTests; these tests cover the driver's control
// flow (parameter seeding, DECLARE handling, fault handling, teardown).
public sealed class SessionTests
{
    // Builds a control-row-only BatchResult (§7.3), optionally preceded by user
    // result sets, matching what a composed batch built by ComposedBatchBuilder
    // actually returns.
    private static BatchResult OkControlRow(
        int? rc = null, decimal? scopeIdentity = null, int trancount = 1, int xactState = 0,
        IReadOnlyList<ResultSet>? userSets = null, IReadOnlyList<(string? Text, bool IsNull)>? display = null)
    {
        var columns = new List<string> { "__dbg_ctl", "ok", "rc", "scope_identity", "trancount", "xact_state" };
        var values = new List<object?> { 1, true, rc, scopeIdentity, trancount, xactState };
        if (display is not null)
        {
            for (var i = 0; i < display.Count; i++)
            {
                columns.Add($"v_{i}");
                columns.Add($"v_{i}_isnull");
                values.Add(display[i].Text);
                values.Add(display[i].IsNull);
            }
        }

        var controlSet = new ResultSet(columns, new IReadOnlyList<object?>[] { values });
        var sets = new List<ResultSet>(userSets ?? Array.Empty<ResultSet>()) { controlSet };
        return new BatchResult(sets, Array.Empty<string>());
    }

    private static BatchResult FaultedControlRow(int errNumber = 547, string errMessage = "simulated fault", int xactState = 1)
    {
        var columns = new[] { "__dbg_ctl", "ok", "err_number", "err_severity", "err_message", "trancount", "xact_state" };
        var values = new object?[] { 1, false, errNumber, 16, errMessage, 1, xactState };
        return new BatchResult(new[] { new ResultSet(columns, new IReadOnlyList<object?>[] { values }) }, Array.Empty<string>());
    }

    [Fact]
    public async Task RunToEndAsync_ScriptMode_ZeroVariables_RunsSingleStatement_AndRollsBack()
    {
        const string script = "SELECT 1 AS x;";
        var userSet = new ResultSet(new[] { "x" }, new IReadOnlyList<object?>[] { new object?[] { 1 } });

        var executor = new FakeStatementExecutor()
            .ThenEmpty()                                   // SET XACT_ABORT/NOCOUNT
            .ThenEmpty()                                   // CREATE TABLE #__dbg_s0
            .ThenEmpty()                                   // INSERT seed
            .ThenEmpty()                                   // BEGIN TRANSACTION
            .Then(_ => OkControlRow(userSets: new[] { userSet }))  // the SELECT itself
            .ThenEmpty();                                  // ROLLBACK

        var session = new Session(
            new SessionOptions("DEVSQL01", "SalesDb", LaunchMode.Script, null, null, script), executor);

        var result = await session.RunToEndAsync();

        Assert.Single(result.StatementUnits);
        var resultSet = Assert.Single(result.Execution.ResultSets);
        Assert.Equal(1, resultSet.Rows[0][0]);
        Assert.Equal("IF @@TRANCOUNT > 0 ROLLBACK;", executor.ReceivedBatches[^1]);
        Assert.Contains(executor.ReceivedBatches, b => b.StartsWith("CREATE TABLE #__dbg_s0"));
    }

    // C5 (§7.2/§21, M7): the debuggee's OWN SET NOCOUNT fires the one-time cosmetic
    // note when the session-init @@OPTIONS probe did NOT already (here: NOCOUNT was
    // already ON at connect, so the init-time trigger stays quiet) — and never
    // repeats once latched, regardless of how many more SET NOCOUNT statements run.
    [Fact]
    public async Task StepAsync_DebuggeeSetNoCount_FiresTheOneTimeNote_OnlyOnce()
    {
        const string script = "SET NOCOUNT OFF;\nSET NOCOUNT ON;\n";
        var nocountOnSet = new ResultSet(new[] { "nocount_on" }, new IReadOnlyList<object?>[] { new object?[] { 1 } });

        var executor = new FakeStatementExecutor()
            .Then(_ => new BatchResult(new[] { nocountOnSet }, Array.Empty<string>()))  // init probe: already ON
            .ThenEmpty()                                   // CREATE TABLE #__dbg_s0
            .ThenEmpty()                                   // INSERT seed
            .ThenEmpty()                                   // BEGIN TRANSACTION
            .Then(_ => OkControlRow())                     // SET NOCOUNT OFF (debuggee's own — fires the note)
            .Then(_ => OkControlRow())                     // SET NOCOUNT ON (already latched — no repeat)
            .ThenEmpty();                                  // ROLLBACK

        var session = new Session(
            new SessionOptions("DEVSQL01", "SalesDb", LaunchMode.Script, null, null, script), executor);

        var result = await session.RunToEndAsync();

        Assert.Single(result.Execution.Messages, m => m.Contains("NOCOUNT is forced ON"));
    }

    [Fact]
    public async Task RunToEndAsync_UnhandledStatementFault_ContinuesLikeNative_AndStillRollsBack()
    {
        // M3 supersedes M1's "any fault is session-fatal" interim rule: fact 18
        // (verified) — an unhandled STATEMENT-LEVEL error does not end the batch
        // natively; execution resumes at the next statement and the client sees the
        // native "Msg N, Level S, ..." text. Terminal behavior moved to the
        // batch-aborting classes (doomed test below).
        const string script = "SELECT 1 / 0;\nSELECT 2 AS after;";
        var afterSet = new ResultSet(new[] { "after" }, new IReadOnlyList<object?>[] { new object?[] { 2 } });
        var executor = new FakeStatementExecutor()
            .ThenEmpty().ThenEmpty().ThenEmpty().ThenEmpty()
            .Then(_ => FaultedControlRow(8134, "Divide by zero error encountered."))
            .Then(_ => OkControlRow(userSets: new[] { afterSet }));

        var session = new Session(
            new SessionOptions("DEVSQL01", "SalesDb", LaunchMode.Script, null, null, script), executor);

        var result = await session.RunToEndAsync();

        Assert.Contains(result.Execution.Messages, m => m.Contains("Msg 8134") && m.Contains("Divide by zero"));
        Assert.Contains(result.Execution.ResultSets, rs => rs.Columns.Contains("after"));
        Assert.Equal("IF @@TRANCOUNT > 0 ROLLBACK;", executor.ReceivedBatches[^1]);
    }

    [Fact]
    public async Task RunToEndAsync_DoomedUnhandledFault_Terminal_ThrowsAndStillRollsBack()
    {
        // The batch-aborting half of §10.3 step 4: xact_state -1 with no eligible TRY
        // mirrors native XACT_ABORT abort-and-rollback — the frame is terminated.
        const string script = "SELECT 1 / 0;\nSELECT 2 AS never;";
        var executor = new FakeStatementExecutor()
            .ThenEmpty().ThenEmpty().ThenEmpty().ThenEmpty()
            .Then(_ => FaultedControlRow(245, "Conversion failed.", xactState: -1));

        var session = new Session(
            new SessionOptions("DEVSQL01", "SalesDb", LaunchMode.Script, null, null, script), executor);

        var ex = await Assert.ThrowsAsync<SessionFaultException>(() => session.RunToEndAsync());
        Assert.Contains("Conversion failed", ex.Message);
        Assert.True(session.IsBroken);
        Assert.Equal("IF @@TRANCOUNT > 0 ROLLBACK;", executor.ReceivedBatches[^1]);
    }

    [Fact]
    public async Task RunToEndAsync_ProcedureMode_SeedsParameterFromArgs_AndStepsThroughDeclareAndSelect()
    {
        const string procDef = "CREATE PROCEDURE dbo.uspAdd @A int AS BEGIN DECLARE @Sum int = @A + 1; SELECT @Sum AS Sum; END";
        var defResultSet = new ResultSet(
            new[] { "def", "qi", "ansi_nulls" },
            new IReadOnlyList<object?>[] { new object?[] { procDef, true, true } });

        var sumUserSet = new ResultSet(new[] { "Sum" }, new IReadOnlyList<object?>[] { new object?[] { 2 } });

        var executor = new FakeStatementExecutor()
            .ThenEmpty()                                                          // SET options
            .Then(_ => new BatchResult(new[] { defResultSet }, Array.Empty<string>()))  // OBJECT_DEFINITION + flags
            .ThenEmpty()                                                          // CREATE TABLE #__dbg_s0 (@A + hoisted @Sum, fact 14)
            .ThenEmpty()                                                          // INSERT seed (@A = 1, Sum NULL)
            .ThenEmpty()                                                          // BEGIN TRANSACTION
            .Then(_ => OkControlRow())                                            // synthetic SET @Sum = @A + 1 (DECLARE SU = initializer only)
            .Then(_ => OkControlRow(userSets: new[] { sumUserSet }))              // SELECT @Sum AS Sum
            .ThenEmpty();                                                        // ROLLBACK

        var session = new Session(
            new SessionOptions(
                "DEVSQL01", "SalesDb", LaunchMode.Procedure, "dbo.uspAdd",
                new Dictionary<string, string> { ["@A"] = "1" }, null),
            executor);

        var result = await session.RunToEndAsync();

        Assert.Contains(executor.ReceivedBatches, b => b.Contains("OBJECT_DEFINITION"));
        // Fact-14 hoisting: the DECLAREd @Sum is a CREATE TABLE column from init — no
        // mid-flow ALTER; the DECLARE SU runs only its initializer.
        Assert.Contains(executor.ReceivedBatches,
            b => b.StartsWith("CREATE TABLE #__dbg_s0") && b.Contains("[A] int") && b.Contains("[Sum] int"));
        Assert.Contains(executor.ReceivedBatches, b => b.StartsWith("INSERT INTO #__dbg_s0") && b.Contains("VALUES (1"));
        Assert.DoesNotContain(executor.ReceivedBatches, b => b.StartsWith("ALTER TABLE"));
        Assert.Contains(executor.ReceivedBatches, b => b.Contains("SET @Sum = @A + 1;"));

        var resultSet = Assert.Single(result.Execution.ResultSets);
        Assert.Equal(2, resultSet.Rows[0][0]);
    }


    // Fact-14 hoisting in script mode: a mid-script DECLARE's column exists from the
    // CREATE TABLE at init (before BEGIN TRANSACTION), seeded NULL alongside parameters.
    [Fact]
    public async Task RunToEndAsync_ScriptMode_HoistsMidScriptDeclareIntoCreateTable()
    {
        const string script = "DECLARE @a int = 7;\nSELECT @a AS a;";
        var userSet = new ResultSet(new[] { "a" }, new IReadOnlyList<object?>[] { new object?[] { 7 } });

        var executor = new FakeStatementExecutor()
            .ThenEmpty()                                   // SET XACT_ABORT/NOCOUNT
            .ThenEmpty()                                   // CREATE TABLE #__dbg_s0 ([a] int NULL)
            .ThenEmpty()                                   // INSERT seed (NULL)
            .ThenEmpty()                                   // BEGIN TRANSACTION
            .Then(_ => OkControlRow())                     // synthetic SET @a = 7
            .Then(_ => OkControlRow(userSets: new[] { userSet }))  // SELECT @a AS a
            .ThenEmpty();                                  // ROLLBACK

        var session = new Session(
            new SessionOptions("DEVSQL01", "SalesDb", LaunchMode.Script, null, null, script), executor);

        await session.RunToEndAsync();

        var createIndex = executor.ReceivedBatches.FindIndex(b => b.StartsWith("CREATE TABLE #__dbg_s0"));
        var beginTranIndex = executor.ReceivedBatches.FindIndex(b => b.StartsWith("BEGIN TRANSACTION"));
        Assert.True(createIndex >= 0 && createIndex < beginTranIndex, "state table must be created before BEGIN TRAN (§4 step 4)");
        Assert.Contains("[a] int", executor.ReceivedBatches[createIndex]);
        Assert.DoesNotContain(executor.ReceivedBatches, b => b.StartsWith("ALTER TABLE"));
        Assert.Contains(executor.ReceivedBatches, b => b.Contains("SET @a = 7;"));
    }

    // A70 (§8.2): an omitted no-default OUTPUT parameter seeds NULL + a launch warning —
    // the native caller's own `DECLARE @o int; EXEC proc @o OUTPUT` state — instead of erroring.
    [Fact]
    public async Task RunToEndAsync_ProcedureMode_OutputParamOmitted_SeedsNullAndWarns()
    {
        const string procDef = "CREATE PROCEDURE dbo.uspOut @In int, @O int OUTPUT AS BEGIN SET @O = @In; END";
        var defResultSet = new ResultSet(
            new[] { "def", "qi", "ansi_nulls" },
            new IReadOnlyList<object?>[] { new object?[] { procDef, true, true } });

        var executor = new FakeStatementExecutor()
            .ThenEmpty()                                                          // SET options
            .Then(_ => new BatchResult(new[] { defResultSet }, Array.Empty<string>()))  // OBJECT_DEFINITION + flags
            .ThenEmpty()                                                          // CREATE TABLE #__dbg_s0
            .ThenEmpty()                                                          // INSERT seed (@In = 1, @O NULL)
            .ThenEmpty()                                                          // BEGIN TRANSACTION
            .Then(_ => OkControlRow())                                            // SET @O = @In
            .ThenEmpty();                                                        // ROLLBACK

        var session = new Session(
            new SessionOptions(
                "DEVSQL01", "SalesDb", LaunchMode.Procedure, "dbo.uspOut",
                new Dictionary<string, string> { ["@In"] = "1" }, null),
            executor);

        await session.RunToEndAsync();

        Assert.Contains(executor.ReceivedBatches,
            b => b.StartsWith("INSERT INTO #__dbg_s0") && b.Contains("VALUES (1, NULL"));
        Assert.Contains(session.LaunchWarnings, w => w.Contains("@O is an OUTPUT parameter"));
    }

    // A70 guard: an OUTPUT parameter WITH a declared default keeps the default (branch order
    // arg → default → OUTPUT-NULL → error), and produces no warning.
    [Fact]
    public async Task RunToEndAsync_ProcedureMode_OutputParamWithDefault_RunsDefaultInitializer()
    {
        const string procDef = "CREATE PROCEDURE dbo.uspOutDef @O int = 3 OUTPUT AS BEGIN SELECT @O AS v; END";
        var defResultSet = new ResultSet(
            new[] { "def", "qi", "ansi_nulls" },
            new IReadOnlyList<object?>[] { new object?[] { procDef, true, true } });

        var executor = new FakeStatementExecutor()
            .ThenEmpty()                                                          // SET options
            .Then(_ => new BatchResult(new[] { defResultSet }, Array.Empty<string>()))  // OBJECT_DEFINITION + flags
            .ThenEmpty()                                                          // CREATE TABLE #__dbg_s0
            .ThenEmpty()                                                          // INSERT seed (@O NULL, default runs next)
            .ThenEmpty()                                                          // BEGIN TRANSACTION
            .Then(_ => OkControlRow())                                            // synthetic SET @O = 3 (default initializer)
            .Then(_ => OkControlRow())                                            // SELECT @O AS v
            .ThenEmpty();                                                        // ROLLBACK

        var session = new Session(
            new SessionOptions("DEVSQL01", "SalesDb", LaunchMode.Procedure, "dbo.uspOutDef", null, null),
            executor);

        await session.RunToEndAsync();

        Assert.Contains(executor.ReceivedBatches, b => b.Contains("SET @O = 3;"));
        Assert.DoesNotContain(session.LaunchWarnings, w => w.Contains("OUTPUT parameter"));
    }

    [Fact]
    public async Task RunToEndAsync_ProcedureMode_MissingRequiredArg_ThrowsBeforeAnyBatch()
    {
        const string procDef = "CREATE PROCEDURE dbo.uspNeedsArg @A int AS BEGIN SELECT @A; END";
        var defResultSet = new ResultSet(
            new[] { "def", "qi", "ansi_nulls" },
            new IReadOnlyList<object?>[] { new object?[] { procDef, true, true } });

        var executor = new FakeStatementExecutor()
            .ThenEmpty()
            .Then(_ => new BatchResult(new[] { defResultSet }, Array.Empty<string>()));

        var session = new Session(
            new SessionOptions("DEVSQL01", "SalesDb", LaunchMode.Procedure, "dbo.uspNeedsArg", null, null),
            executor);

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.RunToEndAsync());
    }

    private static BatchResult PredicateControlRow(int value, int? rc = null) =>
        OkControlRow(rc: rc, userSets: new[] { new ResultSet(new[] { "p" }, new IReadOnlyList<object?>[] { new object?[] { value } }) });

    // DESIGN §6 M2: predicate evaluation is a real round trip (not client-side), and
    // its "p" wrapper result set must never reach the debug console.
    [Fact]
    public async Task RunToEndAsync_IfStatement_PredicateTrue_ExecutesThenBranch_NotElse()
    {
        const string script = "IF 1 = 1\n    SELECT 1 AS x;\nELSE\n    SELECT 2 AS x;";
        var thenSet = new ResultSet(new[] { "x" }, new IReadOnlyList<object?>[] { new object?[] { 1 } });

        var executor = new FakeStatementExecutor()
            .ThenEmpty()                                    // SET options
            .ThenEmpty()                                    // CREATE TABLE (placeholder, zero vars)
            .ThenEmpty()                                    // INSERT DEFAULT VALUES
            .ThenEmpty()                                    // BEGIN TRANSACTION
            .Then(_ => PredicateControlRow(1))              // IF predicate: 1 = 1 -> true
            .Then(_ => OkControlRow(userSets: new[] { thenSet }))  // THEN branch
            .ThenEmpty();                                   // ROLLBACK

        var session = new Session(
            new SessionOptions("DEVSQL01", "SalesDb", LaunchMode.Script, null, null, script), executor);

        var result = await session.RunToEndAsync();

        var resultSet = Assert.Single(result.Execution.ResultSets);
        Assert.Equal(1, resultSet.Rows[0][0]);
        Assert.Contains(executor.ReceivedBatches, b => b.Contains("SELECT CASE WHEN 1 = 1 THEN 1 ELSE 0 END AS p;"));
        // The predicate's own "p" result set must not be forwarded as query output.
        Assert.DoesNotContain(result.Execution.ResultSets, rs => rs.Columns.Contains("p"));
    }

    [Fact]
    public async Task RunToEndAsync_WhileLoop_RunsUntilPredicateFalse()
    {
        const string script = "DECLARE @i int = 0;\nWHILE @i < 2\nBEGIN\n    SET @i = @i + 1;\nEND\nSELECT @i AS final;";
        var finalSet = new ResultSet(new[] { "final" }, new IReadOnlyList<object?>[] { new object?[] { 2 } });

        var executor = new FakeStatementExecutor()
            .ThenEmpty()                                    // SET options
            .ThenEmpty()                                    // CREATE TABLE ([i] int)
            .ThenEmpty()                                    // INSERT seed (NULL)
            .ThenEmpty()                                    // BEGIN TRANSACTION
            .Then(_ => OkControlRow())                      // synthetic SET @i = 0
            .Then(_ => PredicateControlRow(1))              // WHILE @i < 2 -> true (iter 1)
            .Then(_ => OkControlRow())                      // SET @i = @i + 1
            .Then(_ => PredicateControlRow(1))              // WHILE @i < 2 -> true (iter 2)
            .Then(_ => OkControlRow())                      // SET @i = @i + 1
            .Then(_ => PredicateControlRow(0))               // WHILE @i < 2 -> false, exit
            .Then(_ => OkControlRow(userSets: new[] { finalSet }))  // SELECT @i AS final
            .ThenEmpty();                                   // ROLLBACK

        var session = new Session(
            new SessionOptions("DEVSQL01", "SalesDb", LaunchMode.Script, null, null, script), executor);

        var result = await session.RunToEndAsync();

        var resultSet = Assert.Single(result.Execution.ResultSets);
        Assert.Equal(2, resultSet.Rows[0][0]);
        // Three predicate evaluations: true, true, false (exit) — each its own round trip.
        Assert.Equal(3, executor.ReceivedBatches.Count(b => b.Contains("CASE WHEN @i < 2 THEN 1 ELSE 0 END AS p")));
    }

    [Fact]
    public async Task RunToEndAsync_PredicateFault_Unhandled_TakesFalsePathAndContinues()
    {
        // §10.3.1 + facts 18/21: a faulted IF predicate with no enclosing TRY is a
        // statement-level error — natively the FALSE path is taken (fact 21 P1/P6;
        // with no ELSE that means nothing runs) and the batch continues.
        const string script = "IF 1 / 0 = 1 SELECT 1 AS x;\nSELECT 2 AS y;";
        var ySet = new ResultSet(new[] { "y" }, new IReadOnlyList<object?>[] { new object?[] { 2 } });
        var executor = new FakeStatementExecutor()
            .ThenEmpty().ThenEmpty().ThenEmpty().ThenEmpty()
            .Then(_ => FaultedControlRow(8134, "Divide by zero error encountered."))
            .Then(_ => OkControlRow(userSets: new[] { ySet }));

        var session = new Session(
            new SessionOptions("DEVSQL01", "SalesDb", LaunchMode.Script, null, null, script), executor);

        var result = await session.RunToEndAsync();

        Assert.Contains(result.Execution.Messages, m => m.Contains("Msg 8134"));
        Assert.Contains(result.Execution.ResultSets, rs => rs.Columns.Contains("y"));
        Assert.DoesNotContain(executor.ReceivedBatches, b => b.Contains("SELECT 1 AS x"));   // branch never ran
    }

    [Fact]
    public async Task RunToEndAsync_Return_SetsReturnCode()
    {
        const string procDef = "CREATE PROCEDURE dbo.uspReturns AS BEGIN RETURN 42; END";
        var defResultSet = new ResultSet(
            new[] { "def", "qi", "ansi_nulls" }, new IReadOnlyList<object?>[] { new object?[] { procDef, true, true } });
        var returnValueSet = new ResultSet(new[] { "p" }, new IReadOnlyList<object?>[] { new object?[] { 42 } });

        var executor = new FakeStatementExecutor()
            .ThenEmpty()
            .Then(_ => new BatchResult(new[] { defResultSet }, Array.Empty<string>()))
            .ThenEmpty()
            .ThenEmpty()
            .ThenEmpty()
            .Then(_ => OkControlRow(userSets: new[] { returnValueSet }))
            .ThenEmpty();

        var session = new Session(
            new SessionOptions("DEVSQL01", "SalesDb", LaunchMode.Procedure, "dbo.uspReturns", null, null), executor);

        var result = await session.RunToEndAsync();

        Assert.Equal(42, result.ReturnCode);
        Assert.Empty(result.Execution.ResultSets);
        Assert.Contains(executor.ReceivedBatches, b => b.Contains("SELECT (42) AS p;"));
    }

    [Fact]
    public async Task RunToEndAsync_BareReturn_DefaultsReturnCodeToZero()
    {
        const string procDef = "CREATE PROCEDURE dbo.uspBareReturn AS BEGIN RETURN; END";
        var defResultSet = new ResultSet(
            new[] { "def", "qi", "ansi_nulls" }, new IReadOnlyList<object?>[] { new object?[] { procDef, true, true } });

        var executor = new FakeStatementExecutor()
            .ThenEmpty()
            .Then(_ => new BatchResult(new[] { defResultSet }, Array.Empty<string>()))
            .ThenEmpty().ThenEmpty().ThenEmpty()
            .ThenEmpty();

        var session = new Session(
            new SessionOptions("DEVSQL01", "SalesDb", LaunchMode.Procedure, "dbo.uspBareReturn", null, null), executor);

        var result = await session.RunToEndAsync();

        Assert.Equal(0, result.ReturnCode);
    }

    // A54 (§6/§11.5): the top-level proc in PROCEDURE mode is a module frame, so its body
    // end PARKS at an implicit-return stop before the session ends — the final in-proc state
    // is inspectable, then the next step finishes. (Frame 0 never pops, so consuming the park
    // just ends the session — ConsumeReturnStopAsync's Depth == 1 branch.)
    [Fact]
    public async Task RunToEndAsync_ProcedureModeRoot_BodyEnd_ParksAtImplicitReturn_ThenEnds()
    {
        const string procDef = "CREATE PROCEDURE dbo.uspBodyEnd AS BEGIN SELECT 1 AS x; END";
        var defResultSet = new ResultSet(
            new[] { "def", "qi", "ansi_nulls" }, new IReadOnlyList<object?>[] { new object?[] { procDef, true, true } });
        var xSet = new ResultSet(new[] { "x" }, new IReadOnlyList<object?>[] { new object?[] { 1 } });

        var executor = new FakeStatementExecutor()
            .ThenEmpty()                                                       // SET opts
            .Then(_ => new BatchResult(new[] { defResultSet }, Array.Empty<string>()))  // module fetch
            .ThenEmpty().ThenEmpty().ThenEmpty()                              // CREATE s0, seed, BEGIN TRAN
            .Then(_ => OkControlRow(userSets: new[] { xSet }))               // SELECT 1 AS x → runs off the body end
            .ThenEmpty();                                                     // teardown ROLLBACK

        var session = new Session(
            new SessionOptions("DEVSQL01", "SalesDb", LaunchMode.Procedure, "dbo.uspBodyEnd", null, null), executor);
        await session.InitializeAsync();

        await session.StepAsync();                                           // SELECT 1 AS x → PARK at the implicit return
        Assert.Equal(StepDisposition.AtImplicitReturn, session.LastStep.Disposition);
        Assert.True(session.AtImplicitReturn);
        Assert.False(session.IsCompleted);                                   // held open by the park (root not yet ended)
        Assert.Single(session.Frames);                                       // frame 0 still present, state inspectable

        await session.StepAsync();                                           // consume the park → session ends (root never pops)
        Assert.False(session.AtImplicitReturn);
        Assert.True(session.IsCompleted);
        await session.TeardownAsync();
    }

    [Fact]
    public async Task RunToEndAsync_WaitForSkip_SendsNoBatch_AndNotesInMessages()
    {
        const string script = "WAITFOR DELAY '00:00:05';\nSELECT 1 AS x;";
        var userSet = new ResultSet(new[] { "x" }, new IReadOnlyList<object?>[] { new object?[] { 1 } });

        var executor = new FakeStatementExecutor()
            .ThenEmpty().ThenEmpty().ThenEmpty().ThenEmpty()
            .Then(_ => OkControlRow(userSets: new[] { userSet }))  // SELECT 1 AS x (WAITFOR itself sends nothing)
            .ThenEmpty();

        var session = new Session(
            new SessionOptions("DEVSQL01", "SalesDb", LaunchMode.Script, null, null, script), executor);

        var result = await session.RunToEndAsync();

        Assert.DoesNotContain(executor.ReceivedBatches, b => b.Contains("WAITFOR"));
        Assert.Contains(result.Execution.Messages, m => m.Contains("WAITFOR") && m.Contains("skipped"));
    }

    [Fact]
    public async Task RunToEndAsync_WaitForHonor_SendsTheStatement()
    {
        const string script = "WAITFOR DELAY '00:00:00';\nSELECT 1 AS x;";
        var userSet = new ResultSet(new[] { "x" }, new IReadOnlyList<object?>[] { new object?[] { 1 } });

        var executor = new FakeStatementExecutor()
            .ThenEmpty().ThenEmpty().ThenEmpty().ThenEmpty()
            .Then(_ => OkControlRow())                              // the WAITFOR statement itself, honored
            .Then(_ => OkControlRow(userSets: new[] { userSet }))    // SELECT 1 AS x
            .ThenEmpty();

        var session = new Session(
            new SessionOptions(
                "DEVSQL01", "SalesDb", LaunchMode.Script, null, null, script,
                WaitFor: WaitForMode.Honor),
            executor);

        await session.RunToEndAsync();

        Assert.Contains(executor.ReceivedBatches, b => b.Contains("WAITFOR DELAY"));
    }

    // DESIGN §13: conditional-breakpoint evaluation is a real round trip, standalone
    // from the frame's own source, and must not disturb shadow state (§6 M2 D3).
    [Fact]
    public async Task EvaluateConditionAsync_EvaluatesStandaloneCondition()
    {
        const string script = "SELECT 1 AS x;";
        var conditionResult = new ResultSet(new[] { "p" }, new IReadOnlyList<object?>[] { new object?[] { 1 } });

        var executor = new FakeStatementExecutor()
            .ThenEmpty().ThenEmpty().ThenEmpty().ThenEmpty()
            .Then(_ => OkControlRow(userSets: new[] { conditionResult }));

        var session = new Session(
            new SessionOptions("DEVSQL01", "SalesDb", LaunchMode.Script, null, null, script), executor);
        await session.InitializeAsync();

        var (value, fault) = await session.EvaluateConditionAsync("1 = 1");

        Assert.True(value);
        Assert.Null(fault);
        Assert.Contains(executor.ReceivedBatches, b => b.Contains("SELECT CASE WHEN 1 = 1 THEN 1 ELSE 0 END AS p;"));

        await session.TeardownAsync();
    }

    [Fact]
    public async Task EvaluateConditionAsync_ServerFault_ReturnsFaultMessage_DoesNotThrow()
    {
        const string script = "SELECT 1 AS x;";
        var executor = new FakeStatementExecutor()
            .ThenEmpty().ThenEmpty().ThenEmpty().ThenEmpty()
            .Then(_ => FaultedControlRow(207, "Invalid column name 'doesnotexist'."));

        var session = new Session(
            new SessionOptions("DEVSQL01", "SalesDb", LaunchMode.Script, null, null, script), executor);
        await session.InitializeAsync();

        var (value, fault) = await session.EvaluateConditionAsync("doesnotexist > 1");

        Assert.Null(value);
        Assert.Contains("faulted", fault);

        await session.TeardownAsync();
    }

    [Fact]
    public async Task EvaluateConditionAsync_UnparsableCondition_ReturnsFaultMessage_DoesNotThrow()
    {
        const string script = "SELECT 1 AS x;";
        var executor = new FakeStatementExecutor().ThenEmpty().ThenEmpty().ThenEmpty().ThenEmpty();

        var session = new Session(
            new SessionOptions("DEVSQL01", "SalesDb", LaunchMode.Script, null, null, script), executor);
        await session.InitializeAsync();

        var (value, fault) = await session.EvaluateConditionAsync("@x >>> 1 !!!");

        Assert.Null(value);
        Assert.Contains("Could not parse", fault);

        await session.TeardownAsync();
    }

    // D3 (docs/archive/reviews/m2-cursor-design-notes-fable.md §2, locked at the M2->M3 gate):
    // debugger-initiated evaluations are invisible to the debuggee. A breakpoint-
    // condition eval between two steps must neither zero the shadows
    // (ObservePredicateEvaluation is for *debuggee* predicates only, fact 12) nor fold
    // its wrapper control row in (ObserveSuccess) — the next debuggee batch must seed
    // the @@ROWCOUNT shadow with the value the previous debuggee statement produced.
    [Fact]
    public async Task EvaluateConditionAsync_BetweenSteps_DoesNotTouchShadowState()
    {
        const string script = "SELECT 1 AS x;\nSELECT @@ROWCOUNT AS r;";
        var xSet = new ResultSet(new[] { "x" }, new IReadOnlyList<object?>[] { new object?[] { 1 } });
        var pSet = new ResultSet(new[] { "p" }, new IReadOnlyList<object?>[] { new object?[] { 1 } });
        var rSet = new ResultSet(new[] { "r" }, new IReadOnlyList<object?>[] { new object?[] { 7 } });

        var executor = new FakeStatementExecutor()
            .ThenEmpty().ThenEmpty().ThenEmpty().ThenEmpty()
            .Then(_ => OkControlRow(rc: 7, userSets: new[] { xSet }))   // SELECT 1 AS x -> shadow rc := 7
            .Then(_ => OkControlRow(userSets: new[] { pSet }))          // condition eval (wrapper rc must be ignored)
            .Then(_ => OkControlRow(rc: 1, userSets: new[] { rSet })); // SELECT @@ROWCOUNT AS r

        var session = new Session(
            new SessionOptions("DEVSQL01", "SalesDb", LaunchMode.Script, null, null, script), executor);
        await session.InitializeAsync();
        await session.StepAsync();

        var (value, fault) = await session.EvaluateConditionAsync("@@ROWCOUNT = 7");

        Assert.True(value);
        Assert.Null(fault);
        // The condition itself reads the live shadow: its batch seeds rc = 7.
        Assert.Contains("_sh_rowcount int = 7", executor.ReceivedBatches[^1]);

        await session.StepAsync();

        // The next debuggee statement still sees 7 — the condition eval touched nothing.
        Assert.Contains("_sh_rowcount int = 7", executor.ReceivedBatches[^1]);

        await session.TeardownAsync();
    }
}
