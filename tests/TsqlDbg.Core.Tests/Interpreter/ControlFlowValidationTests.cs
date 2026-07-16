// M2 (Fable) — DESIGN §6/§8.2 session-start engine-parity diagnostics, mirrored from
// live-verified behavior (docs/engine-facts.md facts 13/14): duplicate label (132),
// GOTO to undeclared label (133), BREAK/CONTINUE outside WHILE (135/136), GOTO into a
// TRY/CATCH scope (1026), RETURN-with-value in a script frame (178), script-frame
// variable use before its declaration point (137, hoisting), cursor-variable aliasing (C12/A63).
using System;
using System.Linq;
using TsqlDbg.Core.Interpreter;
using Xunit;

namespace TsqlDbg.Core.Tests.Interpreter;

public class ControlFlowValidationTests
{
    private static ParseTimeDiagnosticException Refuses(string sql, FrameKind kind = FrameKind.Procedure)
    {
        var (body, script) = ParseTestHelper.ParseBatch(sql);
        return Assert.Throws<ParseTimeDiagnosticException>(() => ExecutionCursor.Create(body, script, kind));
    }

    private static ExecutionCursor Accepts(string sql, FrameKind kind = FrameKind.Procedure)
    {
        var (body, script) = ParseTestHelper.ParseBatch(sql);
        return ExecutionCursor.Create(body, script, kind);
    }

    [Fact]
    public void DuplicateLabel_IsRefused_CaseInsensitively()
    {
        var ex = Refuses("dup:\nSELECT 1 AS x;\nDUP:\nSELECT 2 AS y;");
        var d = Assert.Single(ex.Diagnostics);
        Assert.Equal(3, d.Line);
        Assert.Contains("already been declared", d.Message);
        Assert.Contains("132", d.Message);
    }

    [Fact]
    public void Goto_UndeclaredLabel_IsRefused()
    {
        var ex = Refuses("GOTO nowhere;\nSELECT 1 AS x;");
        var d = Assert.Single(ex.Diagnostics);
        Assert.Contains("'nowhere'", d.Message);
        Assert.Contains("133", d.Message);
    }

    [Fact]
    public void BreakOutsideLoop_And_ContinueOutsideLoop_AreRefused_SortedByLine()
    {
        var ex = Refuses("SELECT 1 AS x;\nBREAK;\nSELECT 2 AS y;\nCONTINUE;");
        Assert.Equal(2, ex.Diagnostics.Count);
        Assert.Contains("BREAK", ex.Diagnostics[0].Message);
        Assert.Equal(2, ex.Diagnostics[0].Line);
        Assert.Contains("CONTINUE", ex.Diagnostics[1].Message);
        Assert.Equal(4, ex.Diagnostics[1].Line);
    }

    [Fact]
    public void BreakInsideIf_InsideWhile_IsLegal()
    {
        var c = Accepts("WHILE 1 = 1\nBEGIN\nIF 1 = 1\n    BREAK;\nEND");
        Assert.Equal(SuSubKind.While, c.Current!.SubKind);
    }

    // Engine 1026 (fact 13 probe 6). TryCatchStatement itself is still M3-gated, but the
    // engine-parity refusal must win: natively this code cannot compile at all, which
    // outranks "the debugger doesn't support TRY/CATCH yet".
    [Fact]
    public void Goto_IntoTryBlock_IsRefused_BeforeMilestoneGating()
    {
        var ex = Refuses(string.Join('\n',
            "GOTO t1;",
            "BEGIN TRY",
            "t1: SELECT 1 AS x;",
            "END TRY",
            "BEGIN CATCH",
            "SELECT 2 AS y;",
            "END CATCH"));
        var d = Assert.Single(ex.Diagnostics);
        Assert.Contains("TRY or CATCH scope", d.Message);
        Assert.Contains("1026", d.Message);
    }

    [Fact]
    public void Goto_IntoCatchBlock_IsRefused()
    {
        var ex = Refuses(string.Join('\n',
            "GOTO c1;",
            "BEGIN TRY",
            "SELECT 1 AS x;",
            "END TRY",
            "BEGIN CATCH",
            "c1: SELECT 2 AS y;",
            "END CATCH"));
        Assert.Contains("1026", Assert.Single(ex.Diagnostics).Message);
    }

    // Jumping OUT of (or within) a TRY scope is legal (no 1026). Under M2 this test
    // proved it indirectly via the then-gated TryCatchStatement (the milestone gate
    // fired INSTEAD of a diagnostic); M3 lifted that gate, so legality is now assertable
    // directly — and the validation-order contract (engine parity before milestone
    // gates) is re-proven with an M4-gated construct in the same TRY.
    [Fact]
    public void Goto_OutOfTry_IsLegal()
    {
        var (body, script) = ParseTestHelper.ParseBatch(string.Join('\n',
            "BEGIN TRY",
            "GOTO after;",
            "END TRY",
            "BEGIN CATCH",
            "SELECT 1 AS x;",
            "END CATCH",
            "after:",
            "SELECT 2 AS y;"));
        var cursor = ExecutionCursor.Create(body, script);
        Assert.NotNull(cursor.Current);
    }

    [Fact]
    public void ValidationOrder_EngineParityDiagnostics_OutrankMilestoneGates()
    {
        // The body has BOTH a compile-parity error (GOTO into TRY, 1026) and an
        // M4-gated construct (table variable): the ParseTimeDiagnosticException must
        // win — natively this code could never start executing.
        var (body, script) = ParseTestHelper.ParseBatch(string.Join('\n',
            "GOTO inside;",
            "DECLARE @t TABLE (a int);",
            "BEGIN TRY",
            "inside: SELECT 1 AS x;",
            "END TRY",
            "BEGIN CATCH",
            "END CATCH"));
        var ex = Assert.Throws<ParseTimeDiagnosticException>(() => ExecutionCursor.Create(body, script));
        Assert.Contains("1026", Assert.Single(ex.Diagnostics).Message);
    }

    [Fact]
    public void ReturnWithValue_ScriptFrame_IsRefused_ProcedureFrame_IsLegal()
    {
        var ex = Refuses("RETURN 5;", FrameKind.Script);
        Assert.Contains("178", Assert.Single(ex.Diagnostics).Message);

        Accepts("RETURN 5;", FrameKind.Procedure);
        Accepts("RETURN;", FrameKind.Script);                  // bare RETURN legal anywhere
    }

    // ---- fact 14: hoisting's flip side — use-before-declare (137), script frames only ----

    [Fact]
    public void ScriptFrame_UseBeforeDeclare_IsRefused()
    {
        var ex = Refuses("SELECT @a AS a;\nDECLARE @a int;", FrameKind.Script);
        var d = Assert.Single(ex.Diagnostics);
        Assert.Equal(1, d.Line);
        Assert.Contains("@a", d.Message);
        Assert.Contains("137", d.Message);
    }

    [Fact]
    public void ScriptFrame_UndeclaredVariable_IsRefused()
    {
        var ex = Refuses("SELECT @nope AS a;", FrameKind.Script);
        Assert.Contains("@nope", Assert.Single(ex.Diagnostics).Message);
    }

    // Fact 14 probe G: sibling declarators cannot see each other — the declaration
    // point is the END of the whole DECLARE statement.
    [Fact]
    public void ScriptFrame_SiblingDeclaratorReference_IsRefused()
    {
        var ex = Refuses("DECLARE @g1 int = 5, @g2 int = @g1 + 1;", FrameKind.Script);
        Assert.Contains("@g1", Assert.Single(ex.Diagnostics).Message);
    }

    [Fact]
    public void ScriptFrame_DeclareThenUse_IsLegal_IncludingInsideSkippableBranches()
    {
        // Fact 14 cases A/B: visibility is textual, not execution-path-dependent — a
        // DECLARE inside a branch is visible after it; the validator must accept this.
        Accepts("IF 1 = 0\nBEGIN\n    DECLARE @b int = 42;\nEND\nSELECT @b AS b;", FrameKind.Script);
    }

    [Fact]
    public void ScriptFrame_ExecNamedArgumentName_IsNotAVariableReference()
    {
        Accepts("DECLARE @v int = 1;\nEXEC dbo.p @calleeParam = @v;", FrameKind.Script);
    }

    [Fact]
    public void ScriptFrame_ModuleBodyReferences_AreNotScanned()
    {
        // The proc body is its own variable scope, compiled by the server when the
        // CREATE executes — @p must not be flagged against the frame's scope.
        Accepts("CREATE PROCEDURE dbo.tmp_m2 @p int AS BEGIN SELECT @p AS p; END", FrameKind.Script);
    }

    // The 137 check is script-frame-only: for procedure frames the server is the
    // compiler-of-record (a proc with use-before-declare cannot exist — CREATE fails),
    // and parameters — invisible to this walker in unit tests — are legitimate
    // references. Decision on record in the M2 design notes.
    [Fact]
    public void ProcedureFrame_SkipsThe137Check()
    {
        Accepts("SELECT @param AS a;", FrameKind.Procedure);
    }

    // A63: a plain cursor variable is SUPPORTED now (reified as a GLOBAL cursor, §9) — no longer
    // refused. The ONE remaining unsupported shape is aliasing a cursor variable from ANOTHER
    // cursor (`SET @c = @d`), which is refused with an honest C12 message.
    [Fact]
    public void CursorVariableDeclaration_IsAccepted_ForBothFrameKinds()
    {
        Accepts("DECLARE @c CURSOR;", FrameKind.Procedure);
        Accepts("DECLARE @c CURSOR;", FrameKind.Script);
    }

    [Fact]
    public void CursorVariableAliasing_FromAnotherCursor_IsRefused_WithC12()
    {
        var ex = Refuses("DECLARE @c CURSOR;\nDECLARE @d CURSOR;\nSET @c = @d;");
        var d = Assert.Single(ex.Diagnostics);
        Assert.Equal(3, d.Line);
        Assert.Contains("C12", d.Message);
    }

    // Creating a cursor variable via SET @c = CURSOR FOR … is NOT aliasing — it is accepted
    // (the reification site, §9). Guards the refusal against a false positive.
    [Fact]
    public void CursorVariable_SetCursorFor_IsAccepted()
        => Accepts("DECLARE @c CURSOR;\nSET @c = CURSOR FOR SELECT 1 AS v;");

    // A63 (F4): passing a cursor variable to a proc is a cursor OUTPUT parameter — unsupported.
    [Fact]
    public void CursorVariable_PassedToProc_IsRefused_WithC12()
    {
        var ex = Refuses("DECLARE @c CURSOR;\nEXEC dbo.SomeProc @cur = @c OUTPUT;");
        var d = Assert.Single(ex.Diagnostics);
        Assert.Equal(2, d.Line);
        Assert.Contains("C12", d.Message);
        Assert.Contains("OUTPUT parameter", d.Message);
    }

    // An EXEC that passes ordinary scalars (no cursor variable) is not refused by the F4 guard.
    [Fact]
    public void Exec_WithoutCursorArgument_IsAccepted()
        => Accepts("DECLARE @x int;\nEXEC dbo.SomeProc @a = @x;");

    [Fact]
    public void MultipleDiagnostics_AreAggregatedIntoOneRefusal()
    {
        var ex = Refuses(string.Join('\n',
            "BREAK;",                  // 1 — 135
            "GOTO missing;",           // 2 — 133
            "dup:",                    // 3
            "SELECT 1 AS x;",          // 4
            "dup:",                    // 5 — 132
            "SELECT 2 AS y;"));
        Assert.Equal(3, ex.Diagnostics.Count);
        Assert.Equal(new[] { 1, 2, 5 }, ex.Diagnostics.Select(d => d.Line));
        Assert.Contains("3 problem(s)", ex.Message);
    }
}
