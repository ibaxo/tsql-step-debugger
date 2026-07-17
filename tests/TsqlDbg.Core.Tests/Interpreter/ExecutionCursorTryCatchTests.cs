// M3 (Fable) — DESIGN §10.3 routing mechanics on the cursor: TRY/CATCH phase machine,
// RouteError (incl. the consumed-TRY rule), ContinueAfterUnhandledFault (facts 18/21 native
// statement-level continuation), bare-THROW classification/refusals (10704, verified
// compile-time + lexical), and §13's Jump-to-Cursor TRY-nesting policy. Live-verified
// behavior: docs/engine-facts.md facts 15-19 + the 10704/THROW-abort probes recorded in
// docs/archive/reviews/m3-error-model-design-notes-fable.md.
using System;
using System.Collections.Generic;
using System.Linq;
using TsqlDbg.Core.Interpreter;
using Xunit;

namespace TsqlDbg.Core.Tests.Interpreter;

public class ExecutionCursorTryCatchTests
{
    private static ExecutionCursor Cursor(string sql, FrameKind kind = FrameKind.Procedure)
    {
        var (body, script) = ParseTestHelper.ParseBatch(sql);
        return ExecutionCursor.Create(body, script, kind);
    }

    /// <summary>Advances (predicates answered by callback, default true) until Current
    /// starts on <paramref name="line"/>.</summary>
    private static void StepTo(ExecutionCursor cursor, int line, Func<StatementUnit, bool>? predicate = null)
    {
        var guard = 100;
        while (!cursor.IsCompleted && cursor.Current!.Span.StartLine != line)
        {
            if (--guard < 0) throw new InvalidOperationException($"Never reached line {line}.");
            if (cursor.Peek() is InterpreterAction.EvaluatePredicate)
                cursor.Advance(new AdvanceSignal.PredicateEvaluated(predicate?.Invoke(cursor.Current!) ?? true));
            else
                cursor.Advance(AdvanceSignal.Normal);
        }

        Assert.False(cursor.IsCompleted, $"Cursor completed before reaching line {line}.");
    }

    private static List<string> Run(ExecutionCursor cursor, Func<StatementUnit, bool>? predicate = null, int maxSteps = 100)
    {
        var stops = new List<string>();
        while (!cursor.IsCompleted)
        {
            if (--maxSteps < 0)
                throw new InvalidOperationException("Runaway cursor; trace so far: " + string.Join(" ", stops));
            var unit = cursor.Current!;
            stops.Add($"L{unit.Span.StartLine}:{unit.SubKind}");
            if (cursor.Peek() is InterpreterAction.EvaluatePredicate)
                cursor.Advance(new AdvanceSignal.PredicateEvaluated(predicate!(unit)));
            else
                cursor.Advance(AdvanceSignal.Normal);
        }

        return stops;
    }

    private const string OneTryCatch = """
        SET NOCOUNT ON;
        BEGIN TRY
            SELECT 1 AS a;
            SELECT 2 AS b;
        END TRY
        BEGIN CATCH
            SELECT 3 AS c;
            SELECT 4 AS d;
        END CATCH
        SELECT 5 AS e;
        """;                                            // lines: 1, 3, 4 (try), 7, 8 (catch), 10

    // ---- descent / no-fault path ---------------------------------------------------

    [Fact]
    public void TryBody_NoFault_CatchNeverRuns_NoStopOnBeginTry()
    {
        var c = Cursor(OneTryCatch);
        var stops = Run(c);
        Assert.Equal(new[] { "L1:SetOption", "L3:General", "L4:General", "L10:General" }, stops);
        Assert.Equal(0, c.CatchDepth);
    }

    [Fact]
    public void BreakpointLine_OnBeginTry_BindsForwardToFirstTryStatement()
    {
        var c = Cursor(OneTryCatch);
        Assert.True(c.Index.TryMapBreakpointLine(2, out var unit));
        Assert.Equal(3, unit.Span.StartLine);
    }

    // ---- RouteError ------------------------------------------------------------------

    [Fact]
    public void RouteError_FromInsideTry_LandsOnFirstCatchStatement()
    {
        var c = Cursor(OneTryCatch);
        StepTo(c, 3);
        Assert.Equal(0, c.CatchDepth);

        Assert.Equal(RouteOutcome.EnteredCatch, c.RouteError());

        Assert.Equal(7, c.Current!.Span.StartLine);
        Assert.Equal(1, c.CatchDepth);

        var rest = Run(c);
        Assert.Equal(new[] { "L7:General", "L8:General", "L10:General" }, rest);
        Assert.Equal(0, c.CatchDepth);
    }

    [Fact]
    public void RouteError_NoEnclosingTry_ReturnsNoEligibleCatch_CursorUnchanged()
    {
        var c = Cursor("SELECT 1 AS a;\nSELECT 2 AS b;");
        var before = c.Current;
        Assert.Equal(RouteOutcome.NoEligibleCatch, c.RouteError());
        Assert.Same(before, c.Current);
    }

    [Fact]
    public void RouteError_FromInsideCatch_ConsumedTryIneligible_FindsOuter()
    {
        var c = Cursor("""
            BEGIN TRY
                BEGIN TRY
                    SELECT 1 AS x;
                END TRY
                BEGIN CATCH
                    SELECT 2 AS y;
                END CATCH
            END TRY
            BEGIN CATCH
                SELECT 3 AS z;
            END CATCH
            """);                                       // x=3, y=6, z=10
        StepTo(c, 3);
        Assert.Equal(RouteOutcome.EnteredCatch, c.RouteError());   // inner try → inner catch
        Assert.Equal(6, c.Current!.Span.StartLine);
        Assert.Equal(1, c.CatchDepth);

        Assert.Equal(RouteOutcome.EnteredCatch, c.RouteError());   // fault inside inner CATCH: inner
        Assert.Equal(10, c.Current!.Span.StartLine);    // try is consumed → outer catch
        Assert.Equal(1, c.CatchDepth);                  // (inner catch entry truncated)

        Assert.Equal(RouteOutcome.NoEligibleCatch, c.RouteError()); // fault inside outer CATCH: nothing left
        Assert.Equal(10, c.Current!.Span.StartLine);
    }

    [Fact]
    public void RouteError_TryNestedInsideCatch_IsEligible()
    {
        // Verified live: a fault (or bare THROW) inside a TRY nested in a CATCH is
        // caught by that TRY's own CATCH — the enclosing CATCH being "consumed" does
        // not shadow an armed TRY nested within it.
        var c = Cursor("""
            BEGIN TRY
                SELECT 1 AS x;
            END TRY
            BEGIN CATCH
                BEGIN TRY
                    SELECT 2 AS y;
                END TRY
                BEGIN CATCH
                    SELECT 3 AS z;
                END CATCH
            END CATCH
            """);                                       // x=2, y=6, z=9
        StepTo(c, 2);
        Assert.Equal(RouteOutcome.EnteredCatch, c.RouteError());
        Assert.Equal(6, c.Current!.Span.StartLine);
        Assert.Equal(1, c.CatchDepth);

        Assert.Equal(RouteOutcome.EnteredCatch, c.RouteError());   // nested try (inside catch) is armed
        Assert.Equal(9, c.Current!.Span.StartLine);
        Assert.Equal(2, c.CatchDepth);                  // two catch regions occupied
    }

    [Fact]
    public void RouteError_EmptyCatch_PopsThroughToAfterConstruct()
    {
        var c = Cursor("""
            BEGIN TRY
                SELECT 1 AS x;
            END TRY
            BEGIN CATCH
            END CATCH
            SELECT 2 AS after;
            """);
        StepTo(c, 2);
        Assert.Equal(RouteOutcome.TransitedEmptyCatch, c.RouteError());   // ran THROUGH the empty CATCH
        Assert.Equal(6, c.Current!.Span.StartLine);     // nothing to stop on in CATCH
        Assert.Equal(0, c.CatchDepth);
    }

    [Fact]
    public void RouteError_EmptyCatch_LastConstruct_TransitsAndCompletes()
    {
        // §10.3/§11.5 empty-CATCH transit: an empty CATCH that is the LAST construct exhausts the
        // body — RouteError reports TransitedEmptyCatch AND the cursor completes (no phantom stop).
        var c = Cursor("""
            BEGIN TRY
                SELECT 1 AS x;
            END TRY
            BEGIN CATCH
            END CATCH
            """);
        StepTo(c, 2);
        Assert.Equal(RouteOutcome.TransitedEmptyCatch, c.RouteError());
        Assert.True(c.IsCompleted);
        Assert.Equal(0, c.CatchDepth);
    }

    [Fact]
    public void RouteError_ThrowIntoEmptyOuterCatch_TransitsEmptyCatch()
    {
        // A bare THROW re-raising out of an inner CATCH into an EMPTY outer CATCH: the route
        // truncates the inner CATCH occupancy AND transits the empty outer — RouteOutcome must
        // report the transit (a frame-level CatchDepth delta would misread this as a plain route,
        // the §10 re-review's finding). The outer CATCH is last → the cursor completes.
        var c = Cursor("""
            BEGIN TRY
                BEGIN TRY
                    SELECT 1 AS x;
                END TRY
                BEGIN CATCH
                    THROW;
                END CATCH
            END TRY
            BEGIN CATCH
            END CATCH
            """);
        StepTo(c, 3);
        Assert.Equal(RouteOutcome.EnteredCatch, c.RouteError());          // inner try → inner CATCH (THROW)
        Assert.Equal(SuSubKind.Rethrow, c.Current!.SubKind);
        Assert.Equal(RouteOutcome.TransitedEmptyCatch, c.RouteError());   // THROW → empty outer CATCH, transits
        Assert.True(c.IsCompleted);
        Assert.Equal(0, c.CatchDepth);
    }

    // ---- ContinueAfterUnhandledFault (facts 18/21: native statement-level continuation) --

    [Fact]
    public void UnhandledFault_Executable_MovesToNextStatement()
    {
        var c = Cursor("SELECT 1 AS a;\nSELECT 2 AS b;");
        c.ContinueAfterUnhandledFault();
        Assert.Equal(2, c.Current!.Span.StartLine);
    }

    [Fact]
    public void UnhandledFaultedIfPredicate_TakesTheElseBranch()
    {
        // Fact 21 P1/P6 (probed live at the §10 line review): a faulted IF predicate
        // takes the FALSE path — the ELSE branch RUNS; it is not a whole-conditional skip.
        var c = Cursor("""
            IF 1 = 1
                SELECT 1 AS a;
            ELSE
                SELECT 2 AS b;
            SELECT 3 AS c;
            """);
        Assert.Equal(SuSubKind.If, c.Current!.SubKind);
        c.ContinueAfterUnhandledFault();
        Assert.Equal(4, c.Current!.Span.StartLine);     // the ELSE statement
    }

    [Fact]
    public void UnhandledFaultedIfPredicate_NoElse_ResumesAfterTheIf()
    {
        var c = Cursor("""
            IF 1 = 1
                SELECT 1 AS a;
            SELECT 2 AS c;
            """);
        Assert.Equal(SuSubKind.If, c.Current!.SubKind);
        c.ContinueAfterUnhandledFault();
        Assert.Equal(3, c.Current!.Span.StartLine);     // false path with no ELSE = after the IF
    }

    [Fact]
    public void UnhandledFaultedWhilePredicate_ExitsTheLoop()
    {
        // False path for a WHILE = loop exit — observably identical to a skip here,
        // and confirmed live (fact 21 P2: mid-loop predicate fault → after-loop runs).
        var c = Cursor("""
            WHILE 1 = 1
                SELECT 1 AS a;
            SELECT 2 AS b;
            """);
        Assert.Equal(SuSubKind.While, c.Current!.SubKind);
        c.ContinueAfterUnhandledFault();
        Assert.Equal(3, c.Current!.Span.StartLine);
    }

    // ---- bare THROW (Rethrow) ---------------------------------------------------------

    [Fact]
    public void BareThrow_InsideCatch_PeeksAsRethrow_AdvanceNormalThrows()
    {
        var c = Cursor("""
            BEGIN TRY
                SELECT 1 AS x;
            END TRY
            BEGIN CATCH
                SELECT 2 AS y;
                THROW;
            END CATCH
            """);
        StepTo(c, 2);
        Assert.Equal(RouteOutcome.EnteredCatch, c.RouteError());
        StepTo(c, 6);
        Assert.Equal(SuSubKind.Rethrow, c.Current!.SubKind);
        Assert.IsType<InterpreterAction.Rethrow>(c.Peek());
        var ex = Assert.Throws<InvalidOperationException>(() => c.Advance(AdvanceSignal.Normal));
        Assert.Contains("RouteError", ex.Message);
    }

    [Fact]
    public void BareThrow_OutsideAnyCatch_RefusedAtCreate_10704()
    {
        var ex = Assert.Throws<ParseTimeDiagnosticException>(() => Cursor("SELECT 1 AS a;\nTHROW;"));
        Assert.Contains("10704", ex.Message);
        Assert.Equal(2, ex.Diagnostics[0].Line);
    }

    [Fact]
    public void BareThrow_InsideTryBlock_StillRefused_10704()
    {
        var ex = Assert.Throws<ParseTimeDiagnosticException>(() => Cursor("""
            BEGIN TRY
                THROW;
            END TRY
            BEGIN CATCH
            END CATCH
            """));
        Assert.Contains("10704", ex.Message);
    }

    [Fact]
    public void BareThrow_InsideTryNestedInCatch_IsLegal()
    {
        // Verified live: the 10704 rule is lexical — ANY enclosing CATCH satisfies it.
        var c = Cursor("""
            BEGIN TRY
                SELECT 1 AS x;
            END TRY
            BEGIN CATCH
                BEGIN TRY
                    THROW;
                END TRY
                BEGIN CATCH
                    SELECT 2 AS y;
                END CATCH
            END CATCH
            """);
        Assert.NotNull(c.Current);
    }

    [Fact]
    public void ThrowWithArguments_IsAnOrdinaryExecutable_AnywhereIncludingOutsideCatch()
    {
        var c = Cursor("SELECT 1 AS a;\nTHROW 50001, 'boom', 1;");
        StepTo(c, 2);
        Assert.Equal(SuKind.Executable, c.Current!.Kind);
        Assert.Equal(SuSubKind.Throw, c.Current!.SubKind);
        Assert.IsType<InterpreterAction.ExecuteUnit>(c.Peek());
    }

    // ---- transaction statements (M3 gate lifted, §7.2/§10.4) --------------------------

    [Fact]
    public void TransactionStatements_AreExecutable_WithDistinctSubkinds()
    {
        var c = Cursor("""
            BEGIN TRAN;
            SAVE TRAN sp1;
            ROLLBACK TRAN sp1;
            COMMIT;
            """);
        var kinds = new List<SuSubKind>();
        while (!c.IsCompleted)
        {
            kinds.Add(c.Current!.SubKind);
            Assert.Equal(SuKind.Executable, c.Current!.Kind);
            c.Advance(AdvanceSignal.Normal);
        }

        Assert.Equal(new[] { SuSubKind.BeginTran, SuSubKind.SaveTran, SuSubKind.Rollback, SuSubKind.Commit }, kinds);
    }

    // ---- departures out of CATCH drop CatchDepth (§10.2 context pop points) -----------

    [Fact]
    public void Goto_OutOfCatch_DropsCatchDepth()
    {
        var c = Cursor("""
            BEGIN TRY
                SELECT 1 AS x;
            END TRY
            BEGIN CATCH
                GOTO fin;
            END CATCH
            SELECT 2 AS never;
            fin:
            SELECT 3 AS done;
            """);
        StepTo(c, 2);
        Assert.Equal(RouteOutcome.EnteredCatch, c.RouteError());
        Assert.Equal(5, c.Current!.Span.StartLine);     // the GOTO inside CATCH
        Assert.Equal(1, c.CatchDepth);

        c.Advance(AdvanceSignal.Normal);                // performs the jump
        Assert.Equal(9, c.Current!.Span.StartLine);
        Assert.Equal(0, c.CatchDepth);
    }

    [Fact]
    public void Break_InsideTryInsideWhile_ExitsLoop()
    {
        // Fact 18 probe E: BREAK crosses the TRY boundary like a plain loop exit.
        var c = Cursor("""
            DECLARE @i int = 0;
            WHILE @i < 5
            BEGIN
                SET @i += 1;
                BEGIN TRY
                    BREAK;
                END TRY
                BEGIN CATCH
                    SELECT 1 AS never;
                END CATCH
            END
            SELECT 2 AS after;
            """);
        var stops = Run(c, u => true);                  // predicate true once; BREAK exits
        Assert.Equal(new[] { "L1:General", "L2:While", "L4:SetVariable", "L6:Break", "L12:General" }, stops);
        Assert.Equal(0, c.CatchDepth);
    }

    [Fact]
    public void Continue_InsideCatch_ReturnsToPredicate_AndDropsCatchDepth()
    {
        // Fact 18 probe F shape: each iteration faults, CONTINUE leaves the CATCH — the
        // per-iteration error context must pop every time (session reconciles against
        // CatchDepth, which this asserts drops to 0 on the jump).
        var c = Cursor("""
            DECLARE @j int = 0;
            WHILE @j < 2
            BEGIN
                SET @j += 1;
                BEGIN TRY
                    SELECT 1 AS boom;
                END TRY
                BEGIN CATCH
                    CONTINUE;
                END CATCH
                SELECT 2 AS never;
            END
            SELECT 3 AS after;
            """);
        var iterations = 0;
        StepTo(c, 6, _ => true);                        // first iteration, inside TRY
        while (true)
        {
            iterations++;
            Assert.Equal(RouteOutcome.EnteredCatch, c.RouteError());   // simulate the fault
            Assert.Equal(9, c.Current!.Span.StartLine); // CONTINUE inside CATCH
            Assert.Equal(1, c.CatchDepth);
            c.Advance(AdvanceSignal.Normal);            // performs the CONTINUE
            Assert.Equal(0, c.CatchDepth);
            Assert.Equal(2, c.Current!.Span.StartLine); // back on the WHILE predicate
            c.Advance(new AdvanceSignal.PredicateEvaluated(iterations < 2));
            if (c.Current!.Span.StartLine == 13) break; // predicate false → after the loop
            StepTo(c, 6, _ => true);
        }

        Assert.Equal(2, iterations);
    }

    // ---- §13 Jump-to-Cursor TRY-nesting policy -----------------------------------------

    [Fact]
    public void JumpTo_IntoTry_Refused()
    {
        var c = Cursor(OneTryCatch);                    // stopped at L1, outside the TRY
        Assert.True(c.Index.TryMapBreakpointLine(4, out var target));
        Assert.False(c.CanJumpTo(target, out var reason));
        Assert.Contains("TRY", reason);
        Assert.Throws<InvalidOperationException>(() => c.JumpTo(target));
    }

    [Fact]
    public void JumpTo_OutOfTry_Refused()
    {
        var c = Cursor(OneTryCatch);
        StepTo(c, 3);                                   // inside the TRY
        Assert.True(c.Index.TryMapBreakpointLine(10, out var target));
        Assert.False(c.CanJumpTo(target, out _));
    }

    [Fact]
    public void JumpTo_WithinSameTry_Allowed()
    {
        var c = Cursor(OneTryCatch);
        StepTo(c, 3);
        Assert.True(c.Index.TryMapBreakpointLine(4, out var target));
        Assert.True(c.CanJumpTo(target, out _));
        c.JumpTo(target);
        Assert.Equal(4, c.Current!.Span.StartLine);
        Assert.Equal(0, c.CatchDepth);
    }

    [Fact]
    public void JumpTo_WithinSameCatch_Allowed_DepthPreserved()
    {
        var c = Cursor(OneTryCatch);
        StepTo(c, 3);
        Assert.Equal(RouteOutcome.EnteredCatch, c.RouteError());   // now at L7, inside CATCH
        Assert.True(c.Index.TryMapBreakpointLine(8, out var target));
        Assert.True(c.CanJumpTo(target, out _));
        c.JumpTo(target);
        Assert.Equal(8, c.Current!.Span.StartLine);
        Assert.Equal(1, c.CatchDepth);
    }

    [Fact]
    public void JumpTo_FromCatch_ToOutside_Refused()
    {
        var c = Cursor(OneTryCatch);
        StepTo(c, 3);
        Assert.Equal(RouteOutcome.EnteredCatch, c.RouteError());
        Assert.True(c.Index.TryMapBreakpointLine(10, out var target));
        Assert.False(c.CanJumpTo(target, out var reason));
        Assert.Contains("§13", reason);
    }
}
