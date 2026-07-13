// Phase-0 tests (Fable) — DESIGN §6 cursor, §5.1 classification gates, §13 mapping,
// §8/§9/§11 frame primitives. Updated at M2 (Fable): the tests that asserted the M2
// milestone gates were rewritten against the M3 gates when M2 legitimately lifted them
// (their contract — gate-with-line refusal — is unchanged). M2 control-flow behavior
// itself is covered by ExecutionCursorControlFlowTests / ControlFlowValidationTests.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using TsqlDbg.Core.Interpreter;
using Xunit;

namespace TsqlDbg.Core.Tests.Interpreter;

public static class ParseTestHelper
{
    public static (IList<TSqlStatement> Body, string Script) ParseBatch(string sql, bool quotedIdentifiers = true)
    {
        var parser = new TSql150Parser(quotedIdentifiers);
        using var reader = new StringReader(sql);
        var fragment = parser.Parse(reader, out IList<ParseError> errors);
        if (errors is { Count: > 0 })
            throw new InvalidOperationException("Parse errors: " +
                string.Join("; ", errors.Select(e => $"L{e.Line}: {e.Message}")));
        var script = (TSqlScript)fragment;
        var statements = script.Batches.SelectMany(b => b.Statements).ToList();
        return (statements, sql);
    }

    public static TSqlStatement ParseSingle(string sql, bool quotedIdentifiers = true)
        => ParseBatch(sql, quotedIdentifiers).Body.Single();
}

public class ExecutionCursorTests
{
    private static ExecutionCursor Cursor(string sql)
    {
        var (body, script) = ParseTestHelper.ParseBatch(sql);
        return ExecutionCursor.Create(body, script);
    }

    [Fact]
    public void Linear_VisitsStatementsInOrder_AndCompletes()
    {
        var c = Cursor("DECLARE @a int;\nSET @a = 1;\nSELECT @a AS a;");

        // stopOnEntry semantics: Current before any Advance is the first statement.
        Assert.False(c.IsCompleted);
        Assert.Equal(1, c.Current!.Span.StartLine);
        Assert.Equal(SuKind.Declare, c.Current.Kind);

        c.Advance(AdvanceSignal.Normal);
        Assert.Equal(2, c.Current!.Span.StartLine);
        Assert.Equal(SuSubKind.SetVariable, c.Current.SubKind);

        c.Advance(AdvanceSignal.Normal);
        Assert.Equal(3, c.Current!.Span.StartLine);

        c.Advance(AdvanceSignal.Normal);
        Assert.True(c.IsCompleted);
        Assert.Null(c.Current);
    }

    [Fact]
    public void NestedBeginEnd_DescendsTransparently_NoStopOnBlockLines()
    {
        var c = Cursor(string.Join('\n',
            "BEGIN",                    // 1
            "BEGIN",                    // 2
            "SET NOCOUNT ON;",          // 3
            "END",                      // 4
            "UPDATE dbo.t SET c = 1;",  // 5
            "END",                      // 6
            "SELECT 1 AS x;"));         // 7

        Assert.Equal(3, c.Index.Count);                       // structural nodes are not units
        Assert.Equal(3, c.Current!.Span.StartLine);
        Assert.Equal(SuSubKind.SetOption, c.Current.SubKind);

        c.Advance(AdvanceSignal.Normal);
        Assert.Equal(5, c.Current!.Span.StartLine);

        c.Advance(AdvanceSignal.Normal);
        Assert.Equal(7, c.Current!.Span.StartLine);

        c.Advance(AdvanceSignal.Normal);
        Assert.True(c.IsCompleted);
    }

    [Fact]
    public void Declare_PeekYieldsDeclarations_WithExactSlices()
    {
        var c = Cursor("DECLARE @Sum int = 5 * 2, @Name nvarchar(50);");

        var action = Assert.IsType<InterpreterAction.DeclareVariables>(c.Peek());
        Assert.Equal(2, action.Declarations.Count);

        Assert.Equal("@Sum", action.Declarations[0].Name);
        Assert.Equal("int", action.Declarations[0].DataTypeSql);
        Assert.Equal("5 * 2", action.Declarations[0].InitializerSql);

        Assert.Equal("@Name", action.Declarations[1].Name);
        Assert.Equal("nvarchar(50)", action.Declarations[1].DataTypeSql);
        Assert.Null(action.Declarations[1].InitializerSql);
    }

    [Fact]
    public void Peek_IsIdempotent_AndYieldsExecuteForDml()
    {
        var c = Cursor("UPDATE dbo.t SET c = 1;");
        var a1 = Assert.IsType<InterpreterAction.ExecuteUnit>(c.Peek());
        var a2 = Assert.IsType<InterpreterAction.ExecuteUnit>(c.Peek());
        Assert.Same(a1.Unit, a2.Unit);
    }

    [Fact]
    public void EmptyBody_IsCompletedImmediately()
    {
        var c = Cursor("");
        Assert.True(c.IsCompleted);
        Assert.Null(c.Current);
    }

    [Fact]
    public void AdvanceOrPeek_AfterCompleted_Throws()
    {
        var c = Cursor("SELECT 1 AS x;");
        c.Advance(AdvanceSignal.Normal);
        Assert.True(c.IsCompleted);
        Assert.Throws<InvalidOperationException>(() => c.Advance(AdvanceSignal.Normal));
        Assert.Throws<InvalidOperationException>(() => c.Peek());
    }

    // M1's PredicateSignal_IsRejectedUntilM2 asserted the M2 gate; the gate is lifted —
    // the signal is now rejected only where it is meaningless (not at a predicate stop).
    [Fact]
    public void PredicateSignal_AtNonPredicateStop_Throws()
    {
        var c = Cursor("SELECT 1 AS x;");
        Assert.Throws<InvalidOperationException>(() => c.Advance(new AdvanceSignal.PredicateEvaluated(true)));
    }

    // The gate-refusal lineage ends here: M1's IfStatement gate test asserted the M2
    // gate, M2's TryCatchStatement variant asserted the M3 gate, and the M3-era
    // versions of the tests below asserted the M4 gate (table variables / cursors) —
    // each lifted by its milestone, replaced by positive coverage of the construct.
    // M4 lifts the LAST gates: no statement type classifies Unsupported any more (the
    // MilestoneValidator machinery stays for future gates). Milestone progression per
    // the M2-gate precedent, not test weakening.
    [Fact]
    public void DeclareTableVariable_IsAStoppableNoOp_FromM4()
    {
        var c = Cursor("SET NOCOUNT ON;\nDECLARE @t TABLE (a int);\nSELECT 1 AS x;");
        c.Advance(AdvanceSignal.Normal);                     // past SET NOCOUNT
        Assert.Equal(SuKind.Declare, c.Current!.Kind);
        Assert.Equal(SuSubKind.TableVarDeclare, c.Current.SubKind);
        Assert.Equal(2, c.Current.Span.StartLine);
        // D7: the realization is hoisted to frame init/push; the SU itself performs
        // nothing — its action carries no declarations to initialize.
        Assert.IsType<InterpreterAction.TableVarDeclare>(c.Peek());
        c.Advance(AdvanceSignal.Normal);
        Assert.Equal(3, c.Current!.Span.StartLine);          // resumes at the SELECT
    }

    [Fact]
    public void DeclareCursor_AndCursorOps_AreExecutables_FromM4()
    {
        var c = Cursor("DECLARE c CURSOR FOR SELECT 1;\nOPEN c;\nFETCH NEXT FROM c;\nCLOSE c;\nDEALLOCATE c;");
        Assert.Equal(SuSubKind.CursorDeclare, c.Current!.SubKind);
        Assert.Equal(SuKind.Executable, c.Current.Kind);
        foreach (var expectedLine in new[] { 2, 3, 4, 5 })
        {
            c.Advance(AdvanceSignal.Normal);
            Assert.Equal(expectedLine, c.Current!.Span.StartLine);
            Assert.Equal(SuSubKind.CursorOp, c.Current.SubKind);
        }
    }

    [Fact]
    public void TableVarDeclare_NestedInsideBlock_IsIndexedAndStoppable()
    {
        var c = Cursor("BEGIN\nDECLARE @t TABLE (a int);\nEND");
        Assert.Equal(SuSubKind.TableVarDeclare, c.Current!.SubKind);
        Assert.Equal(2, c.Current.Span.StartLine);
    }

    // The M1 milestone visitor over-descended into module-creating DDL: a script frame
    // CREATE PROCEDURE whose body contains M3/M4 constructs would have been refused,
    // even though that body is a separate scope the server compiles whole (fact 13).
    // M2 fixed the descent discipline (InterpreterScopes).
    [Fact]
    public void ModuleCreatingDdl_BodyIsNotGatedOrScanned()
    {
        var c = Cursor(
            "CREATE PROCEDURE dbo.tmp_logger @p int AS\nBEGIN TRY\nSELECT @p AS p;\nEND TRY\nBEGIN CATCH\nTHROW;\nEND CATCH");
        Assert.Equal(1, c.Index.Count);
        Assert.Equal(SuKind.Executable, c.Current!.Kind);
    }

    [Fact]
    public void UnknownStatementType_DefaultsToExecutableOther()
    {
        var c = Cursor("TRUNCATE TABLE dbo.t;");
        Assert.Equal(SuKind.Executable, c.Current!.Kind);
        Assert.Equal(SuSubKind.Other, c.Current.SubKind);
    }

    [Theory]                                                     // A53
    [InlineData("SET DATEFIRST 1;")]                             // SetCommandStatement / GeneralSetCommand
    [InlineData("SET DEADLOCK_PRIORITY LOW;")]                   // SetCommandStatement / GeneralSetCommand
    [InlineData("SET TEXTSIZE 2048;")]                           // SetTextSizeStatement (distinct type)
    [InlineData("SET ROWCOUNT 100;")]                            // SetRowCountStatement (distinct type)
    public void ValueSetCommands_ClassifyAsSetOption_SoTheTrackerRecordsThem(string sql)
    {
        var c = Cursor(sql);
        Assert.Equal(SuKind.Executable, c.Current!.Kind);
        Assert.Equal(SuSubKind.SetOption, c.Current.SubKind);
    }

    [Fact]
    public void ExecStatement_IsExecutableStepOver_InM1()
    {
        var c = Cursor("EXEC dbo.p 1;");
        Assert.Equal(SuSubKind.Execute, c.Current!.SubKind);
        _ = Assert.IsType<InterpreterAction.ExecuteUnit>(c.Peek());
    }

    [Fact]
    public void BreakpointMapping_BindsForwardToNextUnit()
    {
        var c = Cursor("DECLARE @a int;\n\nSET @a = 1;\nSELECT @a AS a;");   // lines 1,3,4

        Assert.True(c.Index.TryMapBreakpointLine(1, out var u1));
        Assert.Equal(1, u1.Span.StartLine);

        Assert.True(c.Index.TryMapBreakpointLine(2, out var u2));            // blank line → next unit
        Assert.Equal(3, u2.Span.StartLine);

        Assert.True(c.Index.TryMapBreakpointLine(4, out var u4));
        Assert.Equal(4, u4.Span.StartLine);

        Assert.False(c.Index.TryMapBreakpointLine(5, out _));                // past the last unit
    }

    [Fact]
    public void UnitText_IsByteExactOriginalSlice()
    {
        var c = Cursor("SET   @a =\t1;");                                     // odd whitespace preserved (§5.3)
        Assert.Equal("SET   @a =\t1;", c.Current!.Text);
    }
}

public class FramePrimitivesTests
{
    private static VariableDeclaration Decl(string sql, int index = 0)
    {
        var stmt = (DeclareVariableStatement)ParseTestHelper.ParseSingle(sql);
        return VariableDeclaration.Extract(stmt, sql)[index];
    }

    [Fact]
    public void VariableCatalog_OrdinalsAreOrdered_DuplicateIsCaseInsensitiveError()
    {
        var cat = new VariableCatalog();
        Assert.Equal(0, cat.Register(Decl("DECLARE @A int;")).Ordinal);
        Assert.Equal(1, cat.Register(Decl("DECLARE @B int;")).Ordinal);
        Assert.Throws<DuplicateVariableException>(() => cat.Register(Decl("DECLARE @a int;")));
        Assert.True(cat.TryGet("@b", out var slot));
        Assert.Equal(1, slot.Ordinal);
    }

    [Fact]
    public void FrameStack_TempObjectResolution_InnermostWins_AndRespectsDeath()
    {
        var (body, script) = ParseTestHelper.ParseBatch("SELECT 1 AS x;");
        Frame MakeFrame(int ordinal) => new(
            ordinal, ModuleIdentity.Script($"f{ordinal}"),
            ExecutionCursor.Create(body, script), SetOptionEnvironment.Default);

        var root = MakeFrame(0);
        var stack = FrameStack.CreateRoot(root);
        root.TempObjects.Add(new TempObjectEntry
        { OriginalName = "#work", PhysicalName = "#work__f0", Kind = TempObjectKind.TempTable, CreatedAtTrancount = 1 });

        var child = MakeFrame(stack.NextOrdinal());
        stack.Push(child);
        child.TempObjects.Add(new TempObjectEntry
        { OriginalName = "#work", PhysicalName = "#work__f1", Kind = TempObjectKind.TempTable, CreatedAtTrancount = 2 });

        Assert.Equal("#work__f1", stack.ResolveTempObject("#WORK")!.PhysicalName);   // innermost, case-insensitive

        child.TempObjects.MarkDeadAbove(survivingTrancount: 1);                       // §10.4 reconciliation
        Assert.Equal("#work__f0", stack.ResolveTempObject("#work")!.PhysicalName);    // falls back to caller's
    }

    [Fact]
    public void FrameStack_RootPopThrows_StateTableNameFollowsOrdinal()
    {
        var (body, script) = ParseTestHelper.ParseBatch("SELECT 1 AS x;");
        var root = new Frame(0, ModuleIdentity.Script(), ExecutionCursor.Create(body, script), SetOptionEnvironment.Default);
        var stack = FrameStack.CreateRoot(root);

        Assert.Equal("#__dbg_s0", root.StateTableName);
        Assert.Throws<InvalidOperationException>(() => stack.Pop());
    }
}
