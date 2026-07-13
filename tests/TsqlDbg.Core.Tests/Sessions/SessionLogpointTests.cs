// M6 G2 (Sonnet, design note §4, A23): Session.EvaluateLogExpressionsAsync — the
// logpoint {expr} evaluation primitive. Happy path is ONE round trip for every
// expression (SELECT (e1) AS v0, (e2) AS v1, …); a combined-batch fault falls back to
// evaluating each expression separately (fail-toward-logging) via the same
// DebuggerInitiated shape §12.4 watch already uses (NEXT VALUE FOR refusal, doomed
// reuse, broken -> "session terminated", never throws).
using TsqlDbg.Core.Execution;
using TsqlDbg.Core.Sessions;
using TsqlDbg.Core.Tests.Fakes;
using Xunit;

namespace TsqlDbg.Core.Tests.Sessions;

public sealed class SessionLogpointTests
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

    private static BatchResult MultiScalarOk(int trancount = 1, int xactState = 0, params object?[] values)
    {
        var columns = new string[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            columns[i] = $"v{i}";
        }

        return new BatchResult(new List<ResultSet>
        {
            new(columns, new IReadOnlyList<object?>[] { values }),
            new(new[] { "__dbg_ctl", "ok", "rc", "scope_identity", "trancount", "xact_state" },
                new IReadOnlyList<object?>[] { new object?[] { 1, true, null, null, trancount, xactState } }),
        }, Array.Empty<string>());
    }

    private static BatchResult MultiScalarFault(int errNumber, string errMessage)
        => new(new List<ResultSet>
        {
            new(new[] { "__dbg_ctl", "ok", "err_number", "err_severity", "err_state", "err_line", "err_procedure", "err_message", "trancount", "xact_state" },
                new IReadOnlyList<object?>[] { new object?[] { 1, false, errNumber, 16, 1, 1, null, errMessage, 1, 0 } }),
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
    public async Task Healthy_MultipleExpressions_OneRoundTrip_ReturnsValuesInOrder()
    {
        var executor = Init(new FakeStatementExecutor())
            .Then(_ => MultiScalarOk(values: new object?[] { 1, "hi", null }));
        var session = ScriptSession("DECLARE @x int = 1;", executor);
        await session.InitializeAsync();
        var batchesBefore = executor.ReceivedBatches.Count;

        var values = await session.EvaluateLogExpressionsAsync(new[] { "@x", "'hi'", "NULL" });

        Assert.Equal(new[] { "1", "hi", "NULL" }, values);
        Assert.Equal(batchesBefore + 1, executor.ReceivedBatches.Count);   // ONE round trip
        Assert.Contains(", (", executor.ReceivedBatches[^1]);             // multiple columns in one SELECT
        await session.TeardownAsync();
    }

    [Fact]
    public async Task EmptyList_ReturnsEmpty_NoRoundTrip()
    {
        var executor = Init(new FakeStatementExecutor());
        var session = ScriptSession("SELECT 1;", executor);
        await session.InitializeAsync();
        var batchesBefore = executor.ReceivedBatches.Count;

        var values = await session.EvaluateLogExpressionsAsync(Array.Empty<string>());

        Assert.Empty(values);
        Assert.Equal(batchesBefore, executor.ReceivedBatches.Count);
        await session.TeardownAsync();
    }

    [Fact]
    public async Task CombinedBatchFaults_FallsBackToPerExpression_FailTowardLogging()
    {
        // The combined batch faults (e.g. one bad column reference among several
        // expressions); the fallback evaluates each separately so only the bad one
        // renders as an error while the others still log.
        var executor = Init(new FakeStatementExecutor())
            .Then(_ => MultiScalarFault(207, "Invalid column name 'nope'."))
            .Then(_ => ScalarOk(1))
            .Then(_ => ScalarFault(207, "Invalid column name 'nope'."))
            .Then(_ => ScalarOk(2));
        var session = ScriptSession("SELECT 1;", executor);
        await session.InitializeAsync();

        var values = await session.EvaluateLogExpressionsAsync(new[] { "1", "nope", "2" });

        Assert.Equal("1", values[0]);
        Assert.Contains("207", values[1]);
        Assert.Contains("Invalid column name", values[1]);
        Assert.Equal("2", values[2]);
        await session.TeardownAsync();
    }

    [Fact]
    public async Task UnparseableExpression_SkipsCombinedAttempt_OthersStillEvaluateLive()
    {
        // One segment doesn't parse at all -- the combined batch is never attempted
        // (it can't be, there's no valid AST for that column), but the OTHER
        // expressions still get their own live round trip via the fallback.
        var executor = Init(new FakeStatementExecutor())
            .Then(_ => ScalarOk(1));
        var session = ScriptSession("SELECT 1;", executor);
        await session.InitializeAsync();

        var values = await session.EvaluateLogExpressionsAsync(new[] { "1 +", "1" });

        Assert.Contains("could not parse", values[0]);
        Assert.Equal("1", values[1]);
        await session.TeardownAsync();
    }

    [Fact]
    public async Task NextValueFor_AlwaysRefused_NeverReachesCombinedOrFallback()
    {
        var executor = Init(new FakeStatementExecutor());
        var session = ScriptSession("SELECT 1;", executor);
        await session.InitializeAsync();
        var batchesBefore = executor.ReceivedBatches.Count;

        var values = await session.EvaluateLogExpressionsAsync(new[] { "NEXT VALUE FOR dbo.MySeq" });

        Assert.Contains("NEXT VALUE FOR", values[0]);
        Assert.Equal(batchesBefore, executor.ReceivedBatches.Count);   // never sent
        await session.TeardownAsync();
    }

    [Fact]
    public async Task Broken_RendersSessionTerminated_ForEveryExpression_NoRoundTrip()
    {
        var executor = Init(new FakeStatementExecutor())
            .Then(_ => throw new StatementExecutionException("compile error", 16, 999)); // sameScopeUncatchable, terminal
        var session = ScriptSession("SELECT 1/0;", executor);
        await session.InitializeAsync();
        await session.StepAsync();
        Assert.True(session.IsBroken);
        var batchesBefore = executor.ReceivedBatches.Count;

        var values = await session.EvaluateLogExpressionsAsync(new[] { "1", "2" });

        Assert.Equal(new[] { "session terminated", "session terminated" }, values);
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
            .Then(_ => MultiScalarOk(trancount: 1, xactState: -1, values: new object?[] { 1 }));
        var session = ScriptSession(DoomedScript, executor);
        await session.InitializeAsync();
        await session.StepAsync();
        Assert.True(session.IsDoomed);

        var values = await session.EvaluateLogExpressionsAsync(new[] { "1" });

        Assert.Equal("1", values[0]);
        var lastBatch = executor.ReceivedBatches[^1];
        Assert.Contains("BEGIN TRANSACTION;", lastBatch);   // redoom prefix, same shape as EvaluateConditionAsync/watch
        await session.TeardownAsync();
    }

    // I9 pin (mirrors SessionWatchTests.DoomedTempRead_NeverArmsThePreflight): a
    // logpoint referencing a doomed-and-destroyed #temp fails honestly and must NOT
    // arm/consume the A14 pre-flight for the NEXT debuggee step.
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

        executor
            .Then(_ => MultiScalarFault(208, "Invalid object name '#t__f0'."))
            .Then(_ => throw new StatementExecutionException("Invalid object name '#t__f0'.", 16, 208));
        var values = await session.EvaluateLogExpressionsAsync(new[] { "(SELECT COUNT(*) FROM #t)" });
        Assert.Contains("208", values[0]);

        var batchesBefore = executor.ReceivedBatches.Count;
        await session.StepAsync();   // the debuggee SU: its OWN pre-flight fires fresh

        Assert.Equal(StepDisposition.DoomedTempPreflight, session.LastStep.Disposition);
        Assert.Equal(batchesBefore, executor.ReceivedBatches.Count);
        await session.TeardownAsync();
    }
}
