// M5 I7 (§12.4 watch, design note §2 — docs/archive/reviews/m5-inspection-design-notes-fable.md):
// Session.EvaluateWatchAsync driven through the same §10.4 state matrix REPL uses for
// its read column (I6): healthy/doomed reuse the existing DebuggerInitiated scalar-eval
// shell (with frame-variable access, unlike REPL); broken renders "session terminated";
// a fault renders as the watch's value string (never thrown — F5). Also the I9 pin
// mirroring SessionC23PreflightTests/SessionReplTests' NeverArmsThePreflight tests.
using TsqlDbg.Core.Execution;
using TsqlDbg.Core.Sessions;
using TsqlDbg.Core.Tests.Fakes;
using Xunit;

namespace TsqlDbg.Core.Tests.Sessions;

public sealed class SessionWatchTests
{
    private static BatchResult Ok(int trancount = 1, int xactState = 0)
        => new(new List<ResultSet>
        {
            new(new[] { "__dbg_ctl", "ok", "rc", "scope_identity", "trancount", "xact_state" },
                new IReadOnlyList<object?>[] { new object?[] { 1, true, null, null, trancount, xactState } }),
        }, Array.Empty<string>());

    private static BatchResult DoomFault()
        => new(new List<ResultSet>
        {
            new(new[] { "__dbg_ctl", "ok", "err_number", "err_severity", "err_state", "err_line", "err_procedure", "err_message", "trancount", "xact_state" },
                new IReadOnlyList<object?>[] { new object?[] { 1, false, 8134, 16, 1, 1, null, "Divide by zero error encountered.", 1, -1 } }),
        }, Array.Empty<string>());

    private static BatchResult ScalarOk(object? value, int trancount = 1, int xactState = 0)
        => new(new List<ResultSet>
        {
            new(new[] { "p" }, new IReadOnlyList<object?>[] { new object?[] { value } }),
            new(new[] { "__dbg_ctl", "ok", "rc", "scope_identity", "trancount", "xact_state" },
                new IReadOnlyList<object?>[] { new object?[] { 1, true, null, null, trancount, xactState } }),
        }, Array.Empty<string>());

    private static BatchResult ScalarFault(int errNumber, string errMessage)
        => new(new List<ResultSet>
        {
            new(new[] { "__dbg_ctl", "ok", "err_number", "err_severity", "err_state", "err_line", "err_procedure", "err_message", "trancount", "xact_state" },
                new IReadOnlyList<object?>[] { new object?[] { 1, false, errNumber, 16, 1, 1, null, errMessage, 1, 0 } }),
        }, Array.Empty<string>());

    private static Session ScriptSession(string script, FakeStatementExecutor executor)
        => new(new SessionOptions("DEVSQL01", "SalesDb", LaunchMode.Script, null, null, script), executor);

    private static FakeStatementExecutor Init(FakeStatementExecutor executor)
        => executor.ThenEmpty().ThenEmpty().ThenEmpty().ThenEmpty();   // SET opts, CREATE state, seed, BEGIN TRAN

    [Fact]
    public async Task Healthy_ScalarExpression_ReturnsValue()
    {
        var executor = Init(new FakeStatementExecutor()).Then(_ => ScalarOk(42));
        var session = ScriptSession("DECLARE @x int = 1;", executor);
        await session.InitializeAsync();
        var frame = session.Frames[0];

        var display = await session.EvaluateWatchAsync(frame, "@x + 41");

        Assert.Equal("42", display);
        await session.TeardownAsync();
    }

    [Fact]
    public async Task Healthy_NullValue_RendersNULL()
    {
        var executor = Init(new FakeStatementExecutor()).Then(_ => ScalarOk(null));
        var session = ScriptSession("SELECT 1;", executor);
        await session.InitializeAsync();
        var frame = session.Frames[0];

        var display = await session.EvaluateWatchAsync(frame, "NULL");

        Assert.Equal("NULL", display);
        await session.TeardownAsync();
    }

    [Fact]
    public async Task Fault_RendersAsWatchValue_NeverThrows()
    {
        var executor = Init(new FakeStatementExecutor()).Then(_ => ScalarFault(207, "Invalid column name 'nope'."));
        var session = ScriptSession("SELECT 1;", executor);
        await session.InitializeAsync();
        var frame = session.Frames[0];

        var display = await session.EvaluateWatchAsync(frame, "nope");

        Assert.Contains("207", display);
        Assert.Contains("Invalid column name", display);
        await session.TeardownAsync();
    }

    [Fact]
    public async Task ExecutorLevelFailure_RendersAsWatchValue_NeverThrows()
    {
        var executor = Init(new FakeStatementExecutor())
            .Then(_ => throw new StatementExecutionException("Timeout expired.", 0, -2));
        var session = ScriptSession("SELECT 1;", executor);
        await session.InitializeAsync();
        var frame = session.Frames[0];

        var display = await session.EvaluateWatchAsync(frame, "1");

        Assert.Contains("faulted", display);
        await session.TeardownAsync();
    }

    [Fact]
    public async Task UnparseableExpression_RendersAsWatchValue()
    {
        var executor = Init(new FakeStatementExecutor());
        var session = ScriptSession("SELECT 1;", executor);
        await session.InitializeAsync();
        var frame = session.Frames[0];

        var display = await session.EvaluateWatchAsync(frame, "1 +");

        Assert.Contains("could not parse", display);
        await session.TeardownAsync();
    }

    [Fact]
    public async Task NextValueFor_AlwaysRefused_ReadOnlyByConstruction()
    {
        var executor = Init(new FakeStatementExecutor());
        var session = ScriptSession("SELECT 1;", executor);
        await session.InitializeAsync();
        var frame = session.Frames[0];
        var batchesBefore = executor.ReceivedBatches.Count;

        var display = await session.EvaluateWatchAsync(frame, "NEXT VALUE FOR dbo.MySeq");

        Assert.Contains("NEXT VALUE FOR", display);
        Assert.Equal(batchesBefore, executor.ReceivedBatches.Count);   // never sent
        await session.TeardownAsync();
    }

    [Fact]
    public async Task Broken_RendersSessionTerminated_NoRoundTrip()
    {
        var executor = Init(new FakeStatementExecutor())
            .Then(_ => throw new StatementExecutionException("compile error", 16, 999)); // sameScopeUncatchable, terminal
        var session = ScriptSession("SELECT 1/0;", executor);
        await session.InitializeAsync();
        await session.StepAsync();
        Assert.True(session.IsBroken);
        var frame = session.Frames[0];
        var batchesBefore = executor.ReceivedBatches.Count;

        var display = await session.EvaluateWatchAsync(frame, "1");

        Assert.Equal("session terminated", display);
        Assert.Equal(batchesBefore, executor.ReceivedBatches.Count);
        await session.TeardownAsync();
    }

    private const string DoomedScript = """
        BEGIN TRY
            SELECT 1/0;
        END TRY
        BEGIN CATCH
            SELECT 1;
        END CATCH
        """;

    [Fact]
    public async Task Doomed_ReusesTheDoomedDebuggerInitiatedShape()
    {
        var executor = Init(new FakeStatementExecutor())
            .Then(_ => DoomFault())
            .Then(_ => ScalarOk(1, trancount: 1, xactState: -1));
        var session = ScriptSession(DoomedScript, executor);
        await session.InitializeAsync();
        await session.StepAsync();
        Assert.True(session.IsDoomed);
        var frame = session.Frames[0];

        var display = await session.EvaluateWatchAsync(frame, "1");

        Assert.Equal("1", display);
        var lastBatch = executor.ReceivedBatches[^1];
        Assert.Contains("BEGIN TRANSACTION;", lastBatch);   // redoom prefix, same shape as EvaluateConditionAsync
        await session.TeardownAsync();
    }

    // Mirrors SessionC23PreflightTests/SessionReplTests: a watch referencing a
    // doomed-and-destroyed #temp fails honestly and must NOT arm/consume the A14
    // pre-flight for the NEXT debuggee step (watch composes via BuildForScalarEval
    // directly — it never calls Session.ComposeDebuggeeBatch at all).
    [Fact]
    public async Task DoomedTempRead_NeverArmsThePreflight()
    {
        const string script = """
            CREATE TABLE #t (a int);
            BEGIN TRY
                SELECT 1 / 0 AS a;
            END TRY
            BEGIN CATCH
                SELECT COUNT(*) AS c FROM #t;
            END CATCH
            """;
        var executor = Init(new FakeStatementExecutor())
            .Then(_ => Ok())          // CREATE TABLE #t
            .Then(_ => DoomFault());  // 1/0 dooms + routes
        var session = ScriptSession(script, executor);
        await session.InitializeAsync();
        await session.StepAsync();   // CREATE #t -> registry Add
        await session.StepAsync();   // fault -> doomed, cursor at CATCH
        Assert.True(session.IsDoomed);
        var frame = session.Frames[0];

        executor.Then(_ => throw new StatementExecutionException("Invalid object name '#t__f0'.", 16, 208));
        var display = await session.EvaluateWatchAsync(frame, "(SELECT COUNT(*) FROM #t)");
        Assert.Contains("faulted", display);

        var batchesBefore = executor.ReceivedBatches.Count;
        await session.StepAsync();   // the debuggee SU: its OWN pre-flight fires fresh

        Assert.Equal(StepDisposition.DoomedTempPreflight, session.LastStep.Disposition);
        Assert.Equal(batchesBefore, executor.ReceivedBatches.Count);
        await session.TeardownAsync();
    }

    [Fact]
    public async Task TempTableReference_ResolvesThroughTheSelectedFramesChain()
    {
        // A20: a session-created frame-0 temp keeps its original name, which would
        // make "resolved" and "literal passthrough" indistinguishable here — so the
        // chain-resolution proof uses a hand-registered COLLIDED-shape entry
        // (physical ≠ original), the shape a callee's colliding create records.
        var executor = Init(new FakeStatementExecutor())
            .Then(_ => ScalarOk(0));    // the watch itself
        var session = ScriptSession("SELECT 1;", executor);
        await session.InitializeAsync();
        var frame = session.Frames[0];
        frame.TempObjects.Add(new Core.Interpreter.TempObjectEntry
        {
            OriginalName = "#work",
            PhysicalName = "#work__f9",
            Kind = Core.Interpreter.TempObjectKind.TempTable,
            CreatedAtTrancount = 1,
        });

        var display = await session.EvaluateWatchAsync(frame, "(SELECT COUNT(*) FROM #work)");

        Assert.Equal("0", display);
        Assert.Contains("[#work__f9]", executor.ReceivedBatches[^1]);   // R2-resolved, not the literal name
        await session.TeardownAsync();
    }
}
