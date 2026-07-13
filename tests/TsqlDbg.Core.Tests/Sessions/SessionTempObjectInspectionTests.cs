// M5 I4 (§12.2 Temp Tables scope — design note §2, docs/archive/reviews/m5-inspection-design-
// notes-fable.md): the plain, read-only round trips against an already-resolved
// physical name (no rewriting, no ComposeDebuggeeBatch). Reports faults instead of
// throwing (I9); a fault here must never arm the A14 pre-flight or consume its state,
// mirroring SessionC23PreflightTests.DebuggerInitiatedEval_NeverArmsThePreflight.
using TsqlDbg.Core.Execution;
using TsqlDbg.Core.Sessions;
using TsqlDbg.Core.Tests.Fakes;
using Xunit;

namespace TsqlDbg.Core.Tests.Sessions;

public sealed class SessionTempObjectInspectionTests
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
                new IReadOnlyList<object?>[] { new object?[] { 1, false, 8134, 16, 1, 9, null, "Divide by zero error encountered.", 1, -1 } }),
        }, Array.Empty<string>());

    private static Session ScriptSession(string script, FakeStatementExecutor executor, int spid = 55)
        => new(new SessionOptions("DEVSQL01", "SalesDb", LaunchMode.Script, null, null, script), executor, spid: spid);

    private static FakeStatementExecutor Init(FakeStatementExecutor executor)
        => executor.ThenEmpty().ThenEmpty().ThenEmpty().ThenEmpty();   // SET opts, CREATE state, seed, BEGIN TRAN

    [Fact]
    public async Task GetTempObjectRowCountAsync_HappyPath_ReturnsCount()
    {
        var executor = Init(new FakeStatementExecutor())
            .Then(_ => new BatchResult(
                new List<ResultSet> { new(new[] { "c" }, new IReadOnlyList<object?>[] { new object?[] { 7 } }) },
                Array.Empty<string>()));
        var session = ScriptSession("SELECT 1;", executor);
        await session.InitializeAsync();

        var (count, fault) = await session.GetTempObjectRowCountAsync("#work__f0");

        Assert.Equal(7, count);
        Assert.Null(fault);
        Assert.Contains("[#work__f0]", executor.ReceivedBatches[^1]);
        await session.TeardownAsync();
    }

    [Fact]
    public async Task GetTempObjectRowCountAsync_Fault_ReportsInsteadOfThrowing()
    {
        var executor = Init(new FakeStatementExecutor())
            .Then(_ => throw new StatementExecutionException("Invalid object name '#gone__f0'.", 16, 208));
        var session = ScriptSession("SELECT 1;", executor);
        await session.InitializeAsync();

        var (count, fault) = await session.GetTempObjectRowCountAsync("#gone__f0");

        Assert.Null(count);
        Assert.Contains("Invalid object name", fault);
        await session.TeardownAsync();
    }

    [Fact]
    public async Task GetTempObjectPageAsync_HappyPath_UsesOffsetFetchShape()
    {
        var executor = Init(new FakeStatementExecutor())
            .Then(_ => new BatchResult(
                new List<ResultSet> { new(new[] { "id" }, new IReadOnlyList<object?>[] { new object?[] { 1 }, new object?[] { 2 } }) },
                Array.Empty<string>()));
        var session = ScriptSession("SELECT 1;", executor);
        await session.InitializeAsync();

        var (page, fault) = await session.GetTempObjectPageAsync("#work__f0", start: 10, count: 50);

        Assert.Null(fault);
        Assert.Equal(2, page!.Rows.Count);
        Assert.Contains("OFFSET 10 ROWS FETCH NEXT 50 ROWS ONLY", executor.ReceivedBatches[^1]);
        Assert.Contains("ORDER BY (SELECT NULL)", executor.ReceivedBatches[^1]);
        await session.TeardownAsync();
    }

    [Fact]
    public async Task GetCursorStatusAsync_HappyPath_DescribesTheStatusCode()
    {
        var executor = Init(new FakeStatementExecutor())
            .Then(_ => new BatchResult(
                new List<ResultSet> { new(new[] { "s" }, new IReadOnlyList<object?>[] { new object?[] { 1 } }) },
                Array.Empty<string>()));
        var session = ScriptSession("SELECT 1;", executor);
        await session.InitializeAsync();

        var (status, fault) = await session.GetCursorStatusAsync("cur__f0_c");

        Assert.Null(fault);
        Assert.Equal("open, rows present", status);
        Assert.Contains("CURSOR_STATUS('global'", executor.ReceivedBatches[^1]);
        await session.TeardownAsync();
    }

    [Fact]
    public async Task GetCursorStatusAsync_Fault_ReportsInsteadOfThrowing()
    {
        var executor = Init(new FakeStatementExecutor())
            .Then(_ => throw new StatementExecutionException("some transport failure", 20, -2));
        var session = ScriptSession("SELECT 1;", executor);
        await session.InitializeAsync();

        var (status, fault) = await session.GetCursorStatusAsync("cur__f0_c");

        Assert.Null(status);
        Assert.NotNull(fault);
        await session.TeardownAsync();
    }

    [Fact]
    public async Task Spid_ThreadedFromConnectionOpen_NoRoundTrip()
    {
        // M5 I3: @@SPID comes from LiveSession.OpenAsync reading
        // SqlConnection.ServerProcessId — ZERO round trips, not a query Session
        // itself issues. Proven here by an Init sequence with no extra scripted call.
        var executor = Init(new FakeStatementExecutor());
        var session = ScriptSession("SELECT 1;", executor, spid: 55);
        await session.InitializeAsync();

        Assert.Equal(55, session.Spid);
        await session.TeardownAsync();
    }

    [Fact]
    public async Task LastObserved_TrancountAndXactState_TrackEveryControlRow()
    {
        var executor = Init(new FakeStatementExecutor())
            .Then(_ => DoomFault());
        var session = ScriptSession("SELECT 1 / 0;", executor);
        await session.InitializeAsync();
        Assert.Equal(1, session.LastObservedTrancount);
        Assert.Equal(1, session.LastObservedXactState);

        await session.StepAsync();

        Assert.Equal(1, session.LastObservedTrancount);
        Assert.Equal(-1, session.LastObservedXactState);   // I3: System scope's XACT_STATE() source
        await session.TeardownAsync();
    }

    // I9 pin (mirrors SessionC23PreflightTests.DebuggerInitiatedEval_NeverArmsThePreflight):
    // a Temp Tables round trip that resolves the SAME doomed-and-destroyed physical
    // name (a C23 shape) must not arm/consume the A14 pre-flight state — it's not even
    // routed through ComposeDebuggeeBatch, so the next debuggee step still gets its
    // OWN fresh first-phase stop.
    [Fact]
    public async Task DoomedRowCountFault_NeverArmsOrConsumesThePreflight()
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
            .Then(_ => Ok())            // CREATE TABLE #t
            .Then(_ => DoomFault());    // 1/0 dooms + routes
        var session = ScriptSession(script, executor);
        await session.InitializeAsync();
        await session.StepAsync();     // CREATE #t → registry Add
        await session.StepAsync();     // fault → doomed, cursor at CATCH
        Assert.True(session.IsDoomed);

        executor.Then(_ => throw new StatementExecutionException("Invalid object name '#t__f0'.", 16, 208));
        var (count, fault) = await session.GetTempObjectRowCountAsync("#t__f0");
        Assert.Null(count);
        Assert.NotNull(fault);

        var batchesBefore = executor.ReceivedBatches.Count;
        await session.StepAsync();     // the debuggee SU: its OWN pre-flight fires fresh

        Assert.Equal(StepDisposition.DoomedTempPreflight, session.LastStep.Disposition);
        Assert.Equal(batchesBefore, executor.ReceivedBatches.Count);   // nothing sent — pre-flight stopped BEFORE
        await session.TeardownAsync();
    }

    // M6 R5 (M5-gate carry-over): explicit doomed guards, in code, on all three I4
    // probes — a refusal result with ZERO executor round trips (stronger than the
    // C23 preflight pin above, which happened to still send one query and catch its
    // fault). Defense in depth for a future caller who doesn't know today's "display
    // layer never calls these while doomed" invariant.
    [Fact]
    public async Task GetTempObjectRowCountAsync_Doomed_RefusesWithoutRoundTrip()
    {
        var executor = Init(new FakeStatementExecutor()).Then(_ => DoomFault());
        var session = ScriptSession("SELECT 1 / 0;", executor);
        await session.InitializeAsync();
        await session.StepAsync();
        Assert.True(session.IsDoomed);
        var batchesBefore = executor.ReceivedBatches.Count;

        var (count, fault) = await session.GetTempObjectRowCountAsync("#t__f0");

        Assert.Null(count);
        Assert.NotNull(fault);
        Assert.Equal(batchesBefore, executor.ReceivedBatches.Count);
        await session.TeardownAsync();
    }

    [Fact]
    public async Task GetTempObjectPageAsync_Doomed_RefusesWithoutRoundTrip()
    {
        var executor = Init(new FakeStatementExecutor()).Then(_ => DoomFault());
        var session = ScriptSession("SELECT 1 / 0;", executor);
        await session.InitializeAsync();
        await session.StepAsync();
        Assert.True(session.IsDoomed);
        var batchesBefore = executor.ReceivedBatches.Count;

        var (page, fault) = await session.GetTempObjectPageAsync("#t__f0", start: 0, count: 50);

        Assert.Null(page);
        Assert.NotNull(fault);
        Assert.Equal(batchesBefore, executor.ReceivedBatches.Count);
        await session.TeardownAsync();
    }

    [Fact]
    public async Task GetCursorStatusAsync_Doomed_RefusesWithoutRoundTrip()
    {
        var executor = Init(new FakeStatementExecutor()).Then(_ => DoomFault());
        var session = ScriptSession("SELECT 1 / 0;", executor);
        await session.InitializeAsync();
        await session.StepAsync();
        Assert.True(session.IsDoomed);
        var batchesBefore = executor.ReceivedBatches.Count;

        var (status, fault) = await session.GetCursorStatusAsync("cur__f0_c");

        Assert.Null(status);
        Assert.NotNull(fault);
        Assert.Equal(batchesBefore, executor.ReceivedBatches.Count);
        await session.TeardownAsync();
    }
}
