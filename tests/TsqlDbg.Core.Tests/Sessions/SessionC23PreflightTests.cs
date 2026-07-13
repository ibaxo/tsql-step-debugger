// M4 A14 (Fable, ratified 2026-07-06 — docs/archive/reviews/m4-c23-doom-temp-severity-fable.md
// §4.3): the §10.4 pre-flight C23 diagnostic, driven end-to-end through
// Session.StepAsync with a fake executor. While doomed, a composed batch that
// resolved a reference through a live §9 user-#temp registry entry names an object
// the fact-22 forced rollback destroyed with certainty — the first step at the SU
// stops BEFORE any server work (two-phase, FaultAtSite shape); the next step executes
// anyway, and a terminal fault carries the C23 citation + original names. Genuine,
// unrelated 208s (never resolved through the registry) take none of this path.
using TsqlDbg.Core.Execution;
using TsqlDbg.Core.Sessions;
using TsqlDbg.Core.Tests.Fakes;
using Xunit;

namespace TsqlDbg.Core.Tests.Sessions;

public sealed class SessionC23PreflightTests
{
    // The C23 archetype: a #temp created pre-doom, read from the CATCH while doomed.
    private const string DoomedReadScript = """
        CREATE TABLE #t (a int);
        BEGIN TRY
            SELECT 1 / 0 AS a;
        END TRY
        BEGIN CATCH
            SELECT COUNT(*) AS c FROM #t;
            SELECT 1 AS tail;
        END CATCH
        """;

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

    private static Session ScriptSession(string script, FakeStatementExecutor executor)
        => new(new SessionOptions("DEVSQL01", "SalesDb", LaunchMode.Script, null, null, script), executor);

    private static FakeStatementExecutor Init(FakeStatementExecutor executor)
        => executor.ThenEmpty().ThenEmpty().ThenEmpty().ThenEmpty();   // SET opts, CREATE state, seed, BEGIN TRAN

    // Steps a fresh session up to the doomed CATCH: CREATE #t (registry entry,
    // created-at trancount 1) then the dooming fault (xact_state -1, routed).
    private static async Task<(Session Session, FakeStatementExecutor Executor)> ArriveDoomedAtCatchAsync(
        string script = DoomedReadScript)
    {
        var executor = Init(new FakeStatementExecutor())
            .Then(_ => Ok())                                           // CREATE TABLE #t
            .Then(_ => DoomFault());                                   // 1/0 dooms + routes
        var session = ScriptSession(script, executor);
        await session.InitializeAsync();
        await session.StepAsync();                                     // CREATE #t → registry Add
        await session.StepAsync();                                     // fault → doomed, cursor at CATCH
        Assert.True(session.IsDoomed);
        Assert.Equal(StepDisposition.RoutedToCatch, session.LastStep.Disposition);
        return (session, executor);
    }

    [Fact]
    public async Task DoomedTempReference_FirstStep_StopsBeforeAnyServerWork()
    {
        var (session, executor) = await ArriveDoomedAtCatchAsync();
        var lineBefore = session.Current!.Span.StartLine;
        var batchesBefore = executor.ReceivedBatches.Count;

        var (sets, messages) = await session.StepAsync();              // SELECT ... FROM #t → pre-flight

        Assert.Equal(StepDisposition.DoomedTempPreflight, session.LastStep.Disposition);
        Assert.Equal(batchesBefore, executor.ReceivedBatches.Count);   // NOTHING was sent
        Assert.Equal(lineBefore, session.Current!.Span.StartLine);     // cursor stays ON the SU
        Assert.False(session.IsBroken);
        Assert.Empty(sets);
        var diagnostic = Assert.Single(messages);
        Assert.Contains("C23", diagnostic);
        Assert.Contains("#t", diagnostic);
        Assert.Contains("208", diagnostic);
        Assert.Contains("#t", session.LastStep.Error!.Message);        // DAP exceptionInfo surface
        await session.TeardownAsync();
    }

    [Fact]
    public async Task SecondStep_ExecutesAnyway_TerminalFaultCarriesC23Citation()
    {
        var (session, executor) = await ArriveDoomedAtCatchAsync();
        executor.Then(_ => throw new StatementExecutionException("Invalid object name '#t__f0'.", 16, 208));

        await session.StepAsync();                                     // phase 1: pre-flight stop
        var (_, messages) = await session.StepAsync();                 // phase 2: execute → real 208, terminal

        Assert.Equal(StepDisposition.FrameFaulted, session.LastStep.Disposition);
        Assert.True(session.IsBroken);
        Assert.Equal(208, session.LastStep.Error!.Number);
        Assert.Contains("C23", session.LastStep.Error.Message);        // A14: citation rides the terminal fault
        Assert.Contains("#t", session.LastStep.Error.Message);         // ... with the ORIGINAL name
        Assert.Contains(messages, m => m.Contains("Caveat C23"));
        Assert.Contains("SELECT COUNT(*)", executor.ReceivedBatches[^1]);  // the batch genuinely went out
        await session.TeardownAsync();
    }

    [Fact]
    public async Task GenuineUnrelated208_WhileDoomed_TakesTheOldPath_NoPreflightNoCitation()
    {
        // #never is not in any registry (never created) — its reference stays
        // unpatched and must behave exactly as before A14: direct execution, real
        // 208, terminal, no C23 annotation. By-construction non-ambiguity.
        const string script = """
            CREATE TABLE #t (a int);
            BEGIN TRY
                SELECT 1 / 0 AS a;
            END TRY
            BEGIN CATCH
                SELECT COUNT(*) AS c FROM #never;
            END CATCH
            """;
        var (session, executor) = await ArriveDoomedAtCatchAsync(script);
        executor.Then(_ => throw new StatementExecutionException("Invalid object name '#never'.", 16, 208));
        var batchesBefore = executor.ReceivedBatches.Count;

        await session.StepAsync();                                     // executes IMMEDIATELY (no pre-flight)

        Assert.Equal(StepDisposition.FrameFaulted, session.LastStep.Disposition);
        Assert.Equal(batchesBefore + 1, executor.ReceivedBatches.Count);
        Assert.DoesNotContain("C23", session.LastStep.Error!.Message);
        await session.TeardownAsync();
    }

    [Fact]
    public async Task JumpToCursor_AbandonsTheArmedPreflight_AndSkipsTheStatement()
    {
        var (session, executor) = await ArriveDoomedAtCatchAsync();

        await session.StepAsync();                                     // phase 1: pre-flight stop, armed
        Assert.Equal(StepDisposition.DoomedTempPreflight, session.LastStep.Disposition);

        Assert.True(session.Index!.TryMapBreakpointLine(7, out var target));   // SELECT 1 AS tail
        session.JumpTo(target);                                        // documented skip path

        executor.Then(_ => Ok(trancount: 1, xactState: -1));           // tail runs under re-established doom
        await session.StepAsync();

        Assert.Equal(StepDisposition.Performed, session.LastStep.Disposition);
        Assert.Contains("SELECT 1 AS tail", executor.ReceivedBatches[^1]);     // the SKIPPED unit never executed
        Assert.DoesNotContain(executor.ReceivedBatches, b => b.Contains("SELECT COUNT(*)"));
        await session.TeardownAsync();
    }

    [Fact]
    public async Task DebuggerInitiatedEval_NeverArmsThePreflight()
    {
        // A watch/breakpoint-condition eval referencing the dead #temp is composed
        // through the same name scope but must not arm a stop for the NEXT debuggee
        // step (the capture gate) — the eval itself fails honestly via the F5 path.
        var (session, executor) = await ArriveDoomedAtCatchAsync();

        executor.Then(_ => throw new StatementExecutionException("Invalid object name '#t__f0'.", 16, 208));
        var (_, faultMessage) = await session.EvaluateConditionAsync("(SELECT COUNT(*) FROM #t) > 0");
        Assert.NotNull(faultMessage);                                  // eval fails, session healthy

        var batchesBefore = executor.ReceivedBatches.Count;
        await session.StepAsync();                                     // debuggee SU: its OWN pre-flight fires

        // The stop must be the SU's own first-phase stop (fresh arm), proving the
        // eval neither pre-consumed the phase nor left stale resolutions behind.
        Assert.Equal(StepDisposition.DoomedTempPreflight, session.LastStep.Disposition);
        Assert.Equal(batchesBefore, executor.ReceivedBatches.Count);
        await session.TeardownAsync();
    }

    [Fact]
    public async Task HealthySession_TempReference_NeverPreflights()
    {
        const string script = """
            CREATE TABLE #t (a int);
            SELECT COUNT(*) AS c FROM #t;
            """;
        var executor = Init(new FakeStatementExecutor())
            .Then(_ => Ok())
            .Then(_ => Ok());
        var session = ScriptSession(script, executor);
        await session.InitializeAsync();

        await session.StepAsync();                                     // CREATE
        await session.StepAsync();                                     // SELECT — executes directly

        Assert.Equal(StepDisposition.Performed, session.LastStep.Disposition);
        // A20: frame-0 temps keep original names — resolved-to-itself, unpatched.
        Assert.Contains("FROM #t;", executor.ReceivedBatches[^1]);
        Assert.DoesNotContain("__f0", executor.ReceivedBatches[^1]);
        Assert.True(session.IsCompleted);
        await session.TeardownAsync();
    }
}
