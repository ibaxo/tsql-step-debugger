// M5 D5/A13 (Fable) — the oracle-free stepped-over EXEC (§10.1, facts 23-H + 24
// Group A), driven end-to-end through Session.StepAsync with a fake executor. A
// stepped-over EXEC with no armed TRY in ANY frame composes WITHOUT the §10.1 oracle
// (whose wrapper TRY imposes transfer semantics native only has when a TRY is armed)
// and executes under client-side absorption; with an armed TRY — or while doomed —
// the M3/M4 oracle composition is byte-for-byte unchanged. Ground truth:
// docs/engine-facts.md fact 24 Group A shapes (a)-(d).
using TsqlDbg.Core.Execution;
using TsqlDbg.Core.Sessions;
using TsqlDbg.Core.Tests.Fakes;
using Xunit;

namespace TsqlDbg.Core.Tests.Sessions;

public sealed class SessionD5OracleFreeExecTests
{
    private static BatchResult Ok(int trancount = 1, int xactState = 0)
        => new(new List<ResultSet>
        {
            new(new[] { "__dbg_ctl", "ok", "rc", "scope_identity", "trancount", "xact_state" },
                new IReadOnlyList<object?>[] { new object?[] { 1, true, null, null, trancount, xactState } }),
        }, Array.Empty<string>());

    // The oracle-free Ok row: err_after present (D5's add-only §7.3 column).
    private static BatchResult OkErrAfter(
        int errAfter, IReadOnlyList<BatchTrailingError>? absorbed = null, IReadOnlyList<string>? messages = null)
        => new(new List<ResultSet>
        {
            new(new[] { "__dbg_ctl", "ok", "rc", "scope_identity", "err_after", "trancount", "xact_state" },
                new IReadOnlyList<object?>[] { new object?[] { 1, true, null, null, errAfter, 1, 0 } }),
        }, messages ?? Array.Empty<string>(), AbsorbedErrors: absorbed);

    private static BatchResult RoutedFault(int xactState)
        => new(new List<ResultSet>
        {
            new(new[] { "__dbg_ctl", "ok", "err_number", "err_severity", "err_state", "err_line", "err_procedure", "err_message", "trancount", "xact_state" },
                new IReadOnlyList<object?>[] { new object?[] { 1, false, 8134, 16, 1, 9, null, "Divide by zero error encountered.", 1, xactState } }),
        }, Array.Empty<string>());

    private static Session ScriptSession(string script, FakeStatementExecutor executor)
        => new(new SessionOptions("DEVSQL01", "SalesDb", LaunchMode.Script, null, null, script), executor);

    private static FakeStatementExecutor Init(FakeStatementExecutor executor)
        => executor.ThenEmpty().ThenEmpty().ThenEmpty().ThenEmpty();   // SET opts, CREATE state, seed, BEGIN TRAN

    [Fact]
    public async Task SteppedOverExec_NoArmedTry_ComposesOracleFree_AndAbsorbs()
    {
        const string script = "EXEC dbo.child;\nSELECT @@ERROR AS e;";
        var executor = Init(new FakeStatementExecutor())
            .Then(_ => OkErrAfter(0))
            .Then(_ => Ok());
        var session = ScriptSession(script, executor);
        await session.InitializeAsync();

        await session.StepAsync();                                     // EXEC dbo.child

        Assert.Equal(StepDisposition.Performed, session.LastStep.Disposition);
        var execBatch = executor.ReceivedBatches[^1];
        Assert.DoesNotContain("BEGIN TRY", execBatch);                 // no oracle
        Assert.DoesNotContain("BEGIN CATCH", execBatch);
        Assert.Contains("AS err_after", execBatch);                    // fact 24 (d) capture
        Assert.Contains(executor.ReceivedBatches.Count - 1, executor.AbsorbingCalls);

        await session.StepAsync();                                     // SELECT @@ERROR AS e

        // fact 24 (d): the callee completed (past any internal absorbed error) — the
        // caller-scope @@ERROR after the EXEC is 0, and the R5 shadow reads it.
        Assert.Contains("_sh_error int = 0", executor.ReceivedBatches[^1]);
        Assert.DoesNotContain(executor.AbsorbingCalls, i => i == executor.ReceivedBatches.Count - 1);
        await session.TeardownAsync();
    }

    [Fact]
    public async Task SteppedOverExec_WithArmedTry_KeepsTheOracle()
    {
        const string script = """
            BEGIN TRY
                EXEC dbo.child;
            END TRY
            BEGIN CATCH
                SELECT 1 AS c;
            END CATCH
            """;
        var executor = Init(new FakeStatementExecutor()).Then(_ => Ok());
        var session = ScriptSession(script, executor);
        await session.InitializeAsync();

        await session.StepAsync();                                     // EXEC under an armed TRY

        var execBatch = executor.ReceivedBatches[^1];
        Assert.Contains("BEGIN TRY", execBatch);                       // oracle present: transfer is native (fact 23 C/D)
        Assert.DoesNotContain("err_after", execBatch);
        Assert.Empty(executor.AbsorbingCalls);
        await session.TeardownAsync();
    }

    [Fact]
    public async Task AbsorbedExecStatementFailure_ErrAfterFeedsTheErrorShadow()
    {
        // fact 24 (d)'s counterpart: the EXEC statement ITSELF failed statement-level
        // (2812 missing proc), was absorbed, and the batch continued — @@ERROR after
        // the EXEC is 2812, not 0, and "the batch succeeded" must not zero the shadow.
        const string script = "EXEC dbo.missing;\nSELECT @@ERROR AS e;";
        var executor = Init(new FakeStatementExecutor())
            .Then(_ => OkErrAfter(2812,
                absorbed: new[] { new BatchTrailingError(2812, 16, "Could not find stored procedure 'dbo.missing'.") },
                messages: new[] { "Msg 2812, Level 16, State 62, Line 1\nCould not find stored procedure 'dbo.missing'." }))
            .Then(_ => Ok());
        var session = ScriptSession(script, executor);
        await session.InitializeAsync();

        var (_, messages) = await session.StepAsync();                 // EXEC dbo.missing — absorbed, continues

        Assert.Equal(StepDisposition.Performed, session.LastStep.Disposition);
        Assert.False(session.IsBroken);
        Assert.Contains(messages, m => m.Contains("2812"));            // native client text forwarded

        await session.StepAsync();
        Assert.Contains("_sh_error int = 2812", executor.ReceivedBatches[^1]);
        await session.TeardownAsync();
    }

    [Fact]
    public async Task ShapeB_NoControlRow_AbsorbedTail_IsTerminalWithThatError()
    {
        // fact 24 (b): a batch-aborting error inside the callee (XACT_ABORT doom)
        // kills the physical batch under absorption — no exception, no control row,
        // and per (c) no 3998 epilogue. Native = the whole batch dies; no armed TRY
        // exists by precondition, so the outcome is terminal with the absorbed tail's
        // final error as the fault.
        const string script = "EXEC dbo.child;\nSELECT 1 AS never;";
        var executor = Init(new FakeStatementExecutor())
            .Then(_ => new BatchResult(
                Array.Empty<ResultSet>(),
                new[] { "Msg 8134, Level 16, State 1, Procedure dbo.child, Line 4\nDivide by zero error encountered." },
                AbsorbedErrors: new[] { new BatchTrailingError(8134, 16, "Divide by zero error encountered.") }));
        var session = ScriptSession(script, executor);
        await session.InitializeAsync();

        var (_, messages) = await session.StepAsync();

        Assert.Equal(StepDisposition.FrameFaulted, session.LastStep.Disposition);
        Assert.True(session.IsBroken);
        Assert.Equal(8134, session.LastStep.Error!.Number);
        Assert.Contains(messages, m => m.Contains("Divide by zero"));
        Assert.DoesNotContain(executor.ReceivedBatches, b => b.Contains("SELECT 1 AS never"));
        await session.TeardownAsync();
    }

    [Fact]
    public async Task NoControlRow_NoAbsorbedErrors_IsAnInternalFault()
    {
        const string script = "EXEC dbo.child;";
        var executor = Init(new FakeStatementExecutor()).ThenEmpty();  // neither row nor tail
        var session = ScriptSession(script, executor);
        await session.InitializeAsync();

        await Assert.ThrowsAsync<SessionFaultException>(() => session.StepAsync());
        await session.TeardownAsync();
    }

    [Fact]
    public async Task Doomed_SteppedOverExec_KeepsTheOracle()
    {
        // A13's documented residual: while doomed, EXEC step-over keeps the M3/M4
        // composition (redoom prefix + oracle) — D5's trigger requires a healthy session.
        const string script = """
            BEGIN TRY
                SELECT 1 / 0 AS a;
            END TRY
            BEGIN CATCH
                EXEC dbo.cleanup;
            END CATCH
            """;
        var executor = Init(new FakeStatementExecutor())
            .Then(_ => RoutedFault(xactState: -1))                     // dooms + routes
            .Then(_ => Ok(trancount: 1, xactState: -1));
        var session = ScriptSession(script, executor);
        await session.InitializeAsync();

        await session.StepAsync();                                     // 1/0 → doomed, cursor at CATCH
        Assert.True(session.IsDoomed);

        await session.StepAsync();                                     // EXEC dbo.cleanup while doomed

        var execBatch = executor.ReceivedBatches[^1];
        Assert.Contains("BEGIN TRANSACTION;", execBatch);              // redoom prefix
        Assert.Contains("BEGIN TRY", execBatch);                       // oracle kept
        Assert.DoesNotContain("err_after", execBatch);
        Assert.Empty(executor.AbsorbingCalls);
        await session.TeardownAsync();
    }

    [Fact]
    public async Task InsideConsumedCatch_NoOuterTry_IsOracleFree_WithRematerialization()
    {
        // A consumed CATCH is NOT an armed TRY (RouteError's own eligibility rule):
        // stepping an EXEC inside a CATCH with no outer TRY anywhere goes oracle-free,
        // while the §10.7 re-materialization block (C21 — the callee's ERROR_*() reads)
        // still composes: its own TRY is consumed before the user statement runs.
        const string script = """
            BEGIN TRY
                SELECT 1 / 0 AS a;
            END TRY
            BEGIN CATCH
                EXEC dbo.log_error;
            END CATCH
            """;
        var executor = Init(new FakeStatementExecutor())
            .Then(_ => RoutedFault(xactState: 1))                      // healthy routed fault
            .Then(_ => OkErrAfter(0));
        var session = ScriptSession(script, executor);
        await session.InitializeAsync();

        await session.StepAsync();                                     // 1/0 → routed (healthy)
        Assert.False(session.IsDoomed);
        Assert.Equal(StepDisposition.RoutedToCatch, session.LastStep.Disposition);

        await session.StepAsync();                                     // EXEC dbo.log_error in CATCH

        var execBatch = executor.ReceivedBatches[^1];
        Assert.Contains("RAISERROR", execBatch);                       // §10.7 re-mat present
        Assert.Contains("AS err_after", execBatch);                    // oracle-free ok row
        Assert.DoesNotContain("err_number", execBatch);                // no CATCH control row arm
        Assert.Contains(executor.ReceivedBatches.Count - 1, executor.AbsorbingCalls);
        await session.TeardownAsync();
    }
}
