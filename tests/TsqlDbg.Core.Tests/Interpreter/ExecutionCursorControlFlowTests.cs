// M2 (Fable) — DESIGN §6 phase machines: IF/WHILE predicate stops, GOTO/BREAK/CONTINUE
// jumps, RETURN, WAITFOR, JumpTo. The fake driver below answers predicate stops from a
// callback (§20.2: cursor phase machines tested with a fake driver — the cursor never
// evaluates predicates itself). Jump/loop semantics mirror live-verified engine
// behavior: docs/engine-facts.md facts 11 (GOTO into WHILE body), 13 (label rules),
// 14 (declaration hoisting).
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using TsqlDbg.Core.Interpreter;
using Xunit;

namespace TsqlDbg.Core.Tests.Interpreter;

public class ExecutionCursorControlFlowTests
{
    private static ExecutionCursor Cursor(string sql, FrameKind kind = FrameKind.Procedure)
    {
        var (body, script) = ParseTestHelper.ParseBatch(sql);
        return ExecutionCursor.Create(body, script, kind);
    }

    /// <summary>
    /// Drives the cursor to completion, answering predicate stops via
    /// <paramref name="predicate"/> (called once per predicate stop, in order) and
    /// everything else with Normal. Returns the stop trace as "L{line}:{SubKind}".
    /// </summary>
    private static List<string> Run(ExecutionCursor cursor, Func<StatementUnit, bool>? predicate = null, int maxSteps = 200)
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

    private static List<int> Lines(IEnumerable<string> stops) =>
        stops.Select(s => int.Parse(s.Split(':')[0][1..])).ToList();

    // ---- IF/ELSE -----------------------------------------------------------------

    [Fact]
    public void If_TruePredicate_EntersThenBranch()
    {
        var c = Cursor(string.Join('\n',
            "DECLARE @x int = 1;",     // 1
            "IF @x = 1",               // 2 — predicate stop
            "    SET @x = 2;",         // 3 — then branch
            "SELECT @x AS x;"));       // 4

        var stops = Run(c, _ => true);
        Assert.Equal(new[] { "L1:General", "L2:If", "L3:SetVariable", "L4:General" }, stops);
    }

    [Fact]
    public void If_FalsePredicate_WithElse_EntersElseBranch()
    {
        var c = Cursor(string.Join('\n',
            "DECLARE @x int = 5;",     // 1
            "IF @x = 1",               // 2
            "    SET @x = 2;",         // 3 — skipped
            "ELSE",                    // 4
            "    SET @x = 3;",         // 5 — taken
            "SELECT @x AS x;"));       // 6

        var stops = Run(c, _ => false);
        Assert.Equal(new[] { "L1:General", "L2:If", "L5:SetVariable", "L6:General" }, stops);
    }

    [Fact]
    public void If_FalsePredicate_NoElse_SkipsToNextStatement()
    {
        var c = Cursor("DECLARE @x int = 5;\nIF @x = 1\n    SET @x = 2;\nSELECT @x AS x;");
        var stops = Run(c, _ => false);
        Assert.Equal(new[] { "L1:General", "L2:If", "L4:General" }, stops);
    }

    [Fact]
    public void ElseIfChain_EachPredicateIsItsOwnVisibleStop()
    {
        var c = Cursor(string.Join('\n',
            "DECLARE @x int = 3;",     // 1
            "IF @x = 1",               // 2
            "    SELECT 1 AS a;",      // 3
            "ELSE IF @x = 2",          // 4 — ElseStatement is itself an IfStatement
            "    SELECT 2 AS a;",      // 5
            "ELSE",                    // 6
            "    SELECT 3 AS a;",      // 7
            "SELECT 99 AS z;"));       // 8

        var stops = Run(c, _ => false);                       // every predicate false
        Assert.Equal(new[] { "L1:General", "L2:If", "L4:If", "L7:General", "L8:General" }, stops);
    }

    [Fact]
    public void If_SingleStatementBranchOnSameLine_TwoStopsSameLine()
    {
        var c = Cursor("IF 1 = 1 SELECT 1 AS x;");
        var stops = Run(c, _ => true);
        // §6: predicate eval is itself one visible step; entering the branch is the next.
        Assert.Equal(new[] { "L1:If", "L1:General" }, stops);
    }

    // ---- WHILE ---------------------------------------------------------------------

    [Fact]
    public void While_FalseImmediately_BodyNeverEntered()
    {
        var c = Cursor("DECLARE @i int = 0;\nWHILE @i < 0\n    SET @i = @i + 1;\nSELECT @i AS i;");
        var stops = Run(c, _ => false);
        Assert.Equal(new[] { "L1:General", "L2:While", "L4:General" }, stops);
    }

    [Fact]
    public void While_LoopsNTimes_PredicateStopBeforeEveryIterationAndExit()
    {
        var c = Cursor(string.Join('\n',
            "DECLARE @i int = 0;",     // 1
            "WHILE @i < 3",            // 2
            "BEGIN",                   // 3
            "    SET @i = @i + 1;",    // 4
            "END",                     // 5
            "SELECT @i AS i;"));       // 6

        var iterations = 0;
        var stops = Run(c, _ => iterations++ < 3);
        Assert.Equal(
            new[] { "L1:General", "L2:While", "L4:SetVariable", "L2:While", "L4:SetVariable", "L2:While", "L4:SetVariable", "L2:While", "L6:General" },
            stops);
    }

    // ---- BREAK / CONTINUE ------------------------------------------------------------

    [Fact]
    public void Break_ExitsLoop_NoFurtherPredicateStop()
    {
        var c = Cursor(string.Join('\n',
            "DECLARE @i int = 0;",     // 1
            "WHILE 1 = 1",             // 2
            "BEGIN",                   // 3
            "    SET @i = @i + 1;",    // 4
            "    BREAK;",              // 5
            "    SET @i = 99;",        // 6 — never reached
            "END",                     // 7
            "SELECT @i AS i;"));       // 8

        var stops = Run(c, _ => true);
        Assert.Equal(new[] { "L1:General", "L2:While", "L4:SetVariable", "L5:Break", "L8:General" }, stops);
    }

    [Fact]
    public void Continue_SkipsRestOfBody_NextStopIsThePredicate()
    {
        var c = Cursor(string.Join('\n',
            "DECLARE @i int = 0;",     // 1
            "WHILE @i < 2",            // 2
            "BEGIN",                   // 3
            "    SET @i = @i + 1;",    // 4
            "    CONTINUE;",           // 5
            "    SET @i = 99;",        // 6 — never reached
            "END",                     // 7
            "SELECT @i AS i;"));       // 8

        var iterations = 0;
        var stops = Run(c, _ => iterations++ < 2);
        Assert.Equal(
            new[] { "L1:General", "L2:While", "L4:SetVariable", "L5:Continue", "L2:While", "L4:SetVariable", "L5:Continue", "L2:While", "L8:General" },
            stops);
    }

    [Fact]
    public void NestedLoops_BreakTargetsInnermostOnly()
    {
        var c = Cursor(string.Join('\n',
            "DECLARE @o int = 0, @i int = 0;",  // 1
            "WHILE @o < 2",                     // 2 — outer
            "BEGIN",                            // 3
            "    SET @o = @o + 1;",             // 4
            "    WHILE 1 = 1",                  // 5 — inner
            "    BEGIN",                        // 6
            "        SET @i = @i + 1;",         // 7
            "        BREAK;",                   // 8 — exits INNER only
            "        SET @i = 77;",             // 9
            "    END",                          // 10
            "    SET @i = @i + 10;",            // 11 — proof: still inside outer body
            "END",                              // 12
            "SELECT @o AS o, @i AS i;"));       // 13

        var outerIterations = 0;
        var stops = Run(c, u => u.Span.StartLine == 2 ? outerIterations++ < 2 : true);
        Assert.Equal(new[]
        {
            "L1:General",
            "L2:While", "L4:SetVariable", "L5:While", "L7:SetVariable", "L8:Break", "L11:SetVariable",
            "L2:While", "L4:SetVariable", "L5:While", "L7:SetVariable", "L8:Break", "L11:SetVariable",
            "L2:While", "L13:General",
        }, stops);
    }

    [Fact]
    public void NestedLoops_ContinueTargetsInnermostOnly()
    {
        var c = Cursor(string.Join('\n',
            "DECLARE @o int = 0, @i int = 0;",  // 1
            "WHILE @o < 1",                     // 2 — outer
            "BEGIN",                            // 3
            "    SET @o = @o + 1;",             // 4
            "    WHILE @i < 1",                 // 5 — inner
            "    BEGIN",                        // 6
            "        SET @i = @i + 1;",         // 7
            "        CONTINUE;",                // 8 — back to INNER predicate
            "    END",                          // 9
            "    SET @i = 100;",                // 10
            "END",                              // 11
            "SELECT 1 AS z;"));                 // 12

        int outer = 0, inner = 0;
        var stops = Run(c, u => u.Span.StartLine == 2 ? outer++ < 1 : inner++ < 1);
        // The stop right after L8:Continue is the INNER predicate (L5), not the outer.
        Assert.Equal(new[]
        {
            "L1:General", "L2:While", "L4:SetVariable", "L5:While", "L7:SetVariable", "L8:Continue",
            "L5:While", "L10:SetVariable", "L2:While", "L12:General",
        }, stops);
    }

    // ---- GOTO ------------------------------------------------------------------------

    [Fact]
    public void Goto_Forward_SkipsStatements_LabelIsNeverAStop_CaseInsensitive()
    {
        var c = Cursor(string.Join('\n',
            "DECLARE @x int = 0;",     // 1
            "GOTO PAST;",              // 2 — label declared lower-case (fact 13 probe 9)
            "SET @x = 1;",             // 3 — skipped
            "past:",                   // 4 — never a stop
            "SELECT @x AS x;"));       // 5

        var stops = Run(c);
        Assert.Equal(new[] { "L1:General", "L2:Goto", "L5:General" }, stops);
    }

    [Fact]
    public void Goto_Backward_FormsHandRolledLoop()
    {
        var c = Cursor(string.Join('\n',
            "DECLARE @i int = 0;",     // 1
            "again:",                  // 2
            "SET @i = @i + 1;",        // 3
            "IF @i < 3",               // 4
            "    GOTO again;",         // 5
            "SELECT @i AS i;"));       // 6

        var passes = 0;
        var stops = Run(c, _ => ++passes < 3);
        Assert.Equal(new[]
        {
            "L1:General", "L3:SetVariable", "L4:If", "L5:Goto",
            "L3:SetVariable", "L4:If", "L5:Goto",
            "L3:SetVariable", "L4:If", "L6:General",
        }, stops);
    }

    [Fact]
    public void Goto_IntoIfBranch_FromOutside_NoPredicateStop()
    {
        var c = Cursor(string.Join('\n',
            "DECLARE @x int = 0;",     // 1
            "GOTO inside;",            // 2
            "IF @x = 999",             // 3 — never evaluated
            "BEGIN",                   // 4
            "    SET @x = 1;",         // 5 — not reached (before the label)
            "inside:",                 // 6
            "    SET @x = @x + 10;",   // 7 — landing point
            "END",                     // 8
            "SELECT @x AS x;"));       // 9

        var stops = Run(c, _ => throw new InvalidOperationException("no predicate may be evaluated"));
        Assert.Equal(new[] { "L1:General", "L2:Goto", "L7:SetVariable", "L9:General" }, stops);
    }

    // Fact 11 case A: predicate false at jump time — the body still runs once from the
    // label; the loop exits via a normal predicate check at the body's natural end.
    [Fact]
    public void Goto_IntoWhileBody_LandsWithoutPredicateCheck_PredicateStopAtBodyEnd()
    {
        var c = Cursor(string.Join('\n',
            "DECLARE @i int = 10;",    // 1
            "GOTO inside;",            // 2
            "WHILE @i < 3",            // 3
            "BEGIN",                   // 4
            "    SET @i = 99;",        // 5 — before the label: not executed on entry
            "inside:",                 // 6
            "    SET @i = @i + 1;",    // 7 — landing point
            "END",                     // 8
            "SELECT @i AS i;"));       // 9

        var stops = Run(c, _ => false);
        Assert.Equal(new[] { "L1:General", "L2:Goto", "L7:SetVariable", "L3:While", "L9:General" }, stops);
    }

    // Fact 11 case B: predicate true at body end — normal looping resumes with FULL
    // body passes (the pre-label statement now runs).
    [Fact]
    public void Goto_IntoWhileBody_NormalLoopingResumesAfterJumpedIteration()
    {
        var c = Cursor(string.Join('\n',
            "DECLARE @i int = 0;",     // 1
            "GOTO inside;",            // 2
            "WHILE @i < 2",            // 3
            "BEGIN",                   // 4
            "    SET @i = @i + 0;",    // 5
            "inside:",                 // 6
            "    SET @i = @i + 1;",    // 7
            "END",                     // 8
            "SELECT @i AS i;"));       // 9

        var predicateResults = new Queue<bool>(new[] { true, false });
        var stops = Run(c, _ => predicateResults.Dequeue());
        Assert.Equal(new[]
        {
            "L1:General", "L2:Goto", "L7:SetVariable",               // jumped-into partial iteration
            "L3:While", "L5:SetVariable", "L7:SetVariable",          // full pass
            "L3:While", "L9:General",                                // exit
        }, stops);
    }

    [Fact]
    public void Goto_OutOfLoop_DissolvesLoopContext()
    {
        var c = Cursor(string.Join('\n',
            "DECLARE @i int = 0;",     // 1
            "WHILE 1 = 1",             // 2
            "BEGIN",                   // 3
            "    SET @i = 1;",         // 4
            "    GOTO done;",          // 5
            "END",                     // 6
            "SET @i = @i + 1;",        // 7 — skipped (label is past it)
            "done:",                   // 8
            "SELECT @i AS i;"));       // 9

        var asked = 0;
        var stops = Run(c, _ => ++asked == 1);                 // only the initial predicate
        Assert.Equal(1, asked);                                // no re-evaluation after the jump
        Assert.Equal(new[] { "L1:General", "L2:While", "L4:SetVariable", "L5:Goto", "L9:General" }, stops);
    }

    [Fact]
    public void Goto_ToTrailingLabel_CompletesFrame()
    {
        var c = Cursor("DECLARE @x int = 1;\nGOTO fin;\nSET @x = 2;\nfin:");   // fact 13: trailing label is legal
        var stops = Run(c);
        Assert.Equal(new[] { "L1:General", "L2:Goto" }, stops);
    }

    // ---- RETURN / WAITFOR --------------------------------------------------------------

    [Fact]
    public void Return_MidLoop_CompletesFrame_BareReturnHasNullExpression()
    {
        var c = Cursor(string.Join('\n',
            "DECLARE @i int = 0;",     // 1
            "WHILE 1 = 1",             // 2
            "BEGIN",                   // 3
            "    RETURN;",             // 4
            "END",                     // 5
            "SELECT 1 AS never;"));    // 6

        Run(c, u => u.Span.StartLine == 2);                    // drive to the RETURN and past it
        Assert.True(c.IsCompleted);

        var c2 = Cursor("RETURN;");
        var action = Assert.IsType<InterpreterAction.ReturnFromFrame>(c2.Peek());
        Assert.Null(action.Expression);
    }

    [Fact]
    public void Return_WithExpression_ExposesTheExpressionFragment()
    {
        var sql = "DECLARE @x int = 4;\nRETURN @x + 1;\nSELECT 1 AS never;";
        var c = Cursor(sql);                                   // FrameKind.Procedure: RETURN <value> legal
        c.Advance(AdvanceSignal.Normal);                       // past the DECLARE

        var action = Assert.IsType<InterpreterAction.ReturnFromFrame>(c.Peek());
        Assert.NotNull(action.Expression);
        Assert.Equal("@x + 1", sql.Substring(action.Expression!.StartOffset, action.Expression.FragmentLength));

        c.Advance(AdvanceSignal.Normal);
        Assert.True(c.IsCompleted);                            // L3 never stops
    }

    [Fact]
    public void WaitForDelay_YieldsWaitForAction_AdvancesSequentially()
    {
        var c = Cursor("WAITFOR DELAY '00:00:01';\nSELECT 1 AS x;");
        var action = Assert.IsType<InterpreterAction.WaitFor>(c.Peek());
        Assert.Equal(SuSubKind.WaitFor, action.Unit.SubKind);

        c.Advance(AdvanceSignal.Normal);
        Assert.Equal(2, c.Current!.Span.StartLine);
    }

    [Fact]
    public void WaitForReceive_IsDefaultOpenExecutable()
    {
        var c = Cursor("WAITFOR (RECEIVE * FROM dbo.q), TIMEOUT 100;");
        Assert.Equal(SuKind.Executable, c.Current!.Kind);
        _ = Assert.IsType<InterpreterAction.ExecuteUnit>(c.Peek());
    }

    // ---- predicate protocol guards -----------------------------------------------------

    [Fact]
    public void AdvanceNormal_AtPredicateStop_Throws()
    {
        var c = Cursor("IF 1 = 1 SELECT 1 AS x;");
        Assert.Equal(SuSubKind.If, c.Current!.SubKind);
        var ex = Assert.Throws<InvalidOperationException>(() => c.Advance(AdvanceSignal.Normal));
        Assert.Contains("PredicateEvaluated", ex.Message);
    }

    [Fact]
    public void Peek_OnPredicateStop_ExposesThePredicateFragment()
    {
        var sql = "DECLARE @a int = 1;\nIF @a >= 10\n    SELECT 1 AS x;";
        var c = Cursor(sql);
        c.Advance(AdvanceSignal.Normal);

        var action = Assert.IsType<InterpreterAction.EvaluatePredicate>(c.Peek());
        Assert.Equal("@a >= 10", sql.Substring(action.Predicate.StartOffset, action.Predicate.FragmentLength));
    }

    // ---- JumpTo (§13 "Jump to Cursor") ---------------------------------------------------

    [Fact]
    public void JumpTo_ForwardAndBackward_StopsOnTargetWithoutExecuting()
    {
        var c = Cursor("DECLARE @x int = 1;\nSET @x = 2;\nSET @x = 3;\nSELECT @x AS x;");
        var target = c.Index.All.Single(u => u.Span.StartLine == 3);

        c.JumpTo(target);
        Assert.Same(target, c.Current);

        c.Advance(AdvanceSignal.Normal);
        Assert.Equal(4, c.Current!.Span.StartLine);

        var back = c.Index.All.Single(u => u.Span.StartLine == 2);
        c.JumpTo(back);
        Assert.Same(back, c.Current);
    }

    [Fact]
    public void JumpTo_IfUnit_RecreatesPredicateStopInvariants()
    {
        var c = Cursor("DECLARE @i int = 0;\nSET @i = 5;\nIF @i = 5\n    SET @i = 6;\nSELECT @i AS i;");
        var ifUnit = c.Index.All.Single(u => u.SubKind == SuSubKind.If);

        c.JumpTo(ifUnit);
        _ = Assert.IsType<InterpreterAction.EvaluatePredicate>(c.Peek());

        c.Advance(new AdvanceSignal.PredicateEvaluated(true));
        Assert.Equal(4, c.Current!.Span.StartLine);
    }

    [Fact]
    public void JumpTo_IntoWhileBody_RejoinsLoopAtBodyEnd()
    {
        var c = Cursor(string.Join('\n',
            "DECLARE @i int = 0;",     // 1
            "WHILE @i < 1",            // 2
            "BEGIN",                   // 3
            "    SET @i = @i + 1;",    // 4
            "    SET @i = @i + 1;",    // 5
            "END",                     // 6
            "SELECT @i AS i;"));       // 7

        c.JumpTo(c.Index.All.Single(u => u.Span.StartLine == 5));
        Assert.Equal(5, c.Current!.Span.StartLine);

        c.Advance(AdvanceSignal.Normal);                       // body end → predicate re-eval (fact 11 semantics)
        Assert.Equal(SuSubKind.While, c.Current!.SubKind);
        c.Advance(new AdvanceSignal.PredicateEvaluated(false));
        Assert.Equal(7, c.Current!.Span.StartLine);
    }

    // ---- index flattening + breakpoint mapping (§13) --------------------------------------

    [Fact]
    public void Index_FlattensBranchAndLoopBodies_InSourceOrder_LabelsExcluded()
    {
        var c = Cursor(string.Join('\n',
            "DECLARE @x int = 1;",     // 1
            "IF @x = 1",               // 2
            "BEGIN",                   // 3
            "    SET @x = 2;",         // 4
            "END",                     // 5
            "ELSE",                    // 6
            "BEGIN",                   // 7
            "    SET @x = 3;",         // 8
            "END",                     // 9
            "WHILE @x < 5",            // 10
            "    SET @x = @x + 1;",    // 11
            "lbl:",                    // 12
            "SELECT @x AS x;"));       // 13

        Assert.Equal(new[] { 1, 2, 4, 8, 10, 11, 13 }, c.Index.All.Select(u => u.Span.StartLine));
        Assert.Equal(Enumerable.Range(0, 7), c.Index.All.Select(u => u.Ordinal));

        Assert.True(c.Index.TryMapBreakpointLine(4, out var inThen));
        Assert.Equal(4, inThen.Span.StartLine);                // inside the THEN branch
        Assert.True(c.Index.TryMapBreakpointLine(7, out var inElse));
        Assert.Equal(8, inElse.Span.StartLine);                // forward into the ELSE body
        Assert.True(c.Index.TryMapBreakpointLine(12, out var pastLabel));
        Assert.Equal(13, pastLabel.Span.StartLine);            // label line binds forward (§13)
    }
}
