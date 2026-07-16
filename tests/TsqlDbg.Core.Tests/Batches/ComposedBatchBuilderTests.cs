using TsqlDbg.Core.Batches;
using TsqlDbg.Core.Interpreter;
using TsqlDbg.Core.Rewrite;
using TsqlDbg.Core.Tests.Interpreter;
using Xunit;

namespace TsqlDbg.Core.Tests.Batches;

// DESIGN §7.1 (exact template) + Appendix A (worked example): frame 1, QI ON / AN ON,
// live vars @Id int, @Name nvarchar(50); statement `UPDATE dbo.T SET n = @Name WHERE
// id = @Id;` -> B = 8 since the F5 preamble hardening (frame-env SET XACT_ABORT as
// the fourth preamble line, ratified 2026-07-06). Structural assertions rather than a
// byte-for-byte string match, so cosmetic appendix edits don't churn tests.
public class ComposedBatchBuilderTests
{
    private static Frame BuildAppendixAFrame(out RewriteContext ctx)
    {
        ctx = new RewriteContext("7f3a");
        var (body, script) = ParseTestHelper.ParseBatch("UPDATE dbo.T SET n = @Name WHERE id = @Id;");
        var cursor = ExecutionCursor.Create(body, script);
        var frame = new Frame(1, ModuleIdentity.Script("uspChild"), cursor, new SetOptionEnvironment(true, true));
        frame.Variables.Register(new VariableDeclaration("@Id", "int", null,
            (Microsoft.SqlServer.TransactSql.ScriptDom.DeclareVariableElement)ParseSingleDeclareElement("DECLARE @Id int;")));
        frame.Variables.Register(new VariableDeclaration("@Name", "nvarchar(50)", null,
            (Microsoft.SqlServer.TransactSql.ScriptDom.DeclareVariableElement)ParseSingleDeclareElement("DECLARE @Name nvarchar(50);")));
        return frame;
    }

    private static Microsoft.SqlServer.TransactSql.ScriptDom.DeclareVariableElement ParseSingleDeclareElement(string sql)
    {
        var stmt = (Microsoft.SqlServer.TransactSql.ScriptDom.DeclareVariableStatement)ParseTestHelper.ParseSingle(sql);
        return stmt.Declarations[0];
    }

    [Fact]
    public void AppendixA_BLineArithmetic_Is8()
    {
        var frame = BuildAppendixAFrame(out var ctx);
        var unit = frame.Cursor.Index.All[0];
        var batch = ComposedBatchBuilder.BuildForUnit(frame, RewriteEngine.CreateDefault(), ctx, unit, frame.Cursor.Index.FullScript, ShadowValues.Initial());

        Assert.Equal(8, batch.B);
        var lines = batch.Text.Split('\n');
        Assert.Equal("SET XACT_ABORT OFF;", lines[3]);      // F5: frame env re-asserted every batch
        Assert.Equal("BEGIN TRY", lines[6]);
        Assert.StartsWith("UPDATE dbo.T", lines[7]);
    }

    [Fact]
    public void AppendixA_PreambleContainsExpectedDeclarations()
    {
        var frame = BuildAppendixAFrame(out var ctx);
        var unit = frame.Cursor.Index.All[0];
        var batch = ComposedBatchBuilder.BuildForUnit(frame, RewriteEngine.CreateDefault(), ctx, unit, frame.Cursor.Index.FullScript, ShadowValues.Initial());

        Assert.Contains("SET QUOTED_IDENTIFIER ON;", batch.Text);
        Assert.Contains("SET ANSI_NULLS ON;", batch.Text);
        Assert.Contains("SET NOCOUNT ON;", batch.Text);
        Assert.Contains("@Id int", batch.Text);
        Assert.Contains("@Name nvarchar(50)", batch.Text);
        Assert.Contains("@__dbg7f3a_rc int", batch.Text);
        Assert.Contains("@__dbg7f3a_err int", batch.Text);
        Assert.Contains("@__dbg7f3a_scopeid numeric(38,0)", batch.Text);
        Assert.Contains("SELECT @Id = [Id], @Name = [Name] FROM #__dbg_s1;", batch.Text);
    }

    // C13 (§11.2): the default composition (RowCount "0") emits NO ROWCOUNT lines — the resting
    // invariant costs nothing and the byte template (and B) is unchanged when no limit is in force.
    [Fact]
    public void RowCount_Default_EmitsNoRowCountLines_AndBUnchanged()
    {
        var frame = BuildAppendixAFrame(out var ctx);
        var unit = frame.Cursor.Index.All[0];
        var batch = ComposedBatchBuilder.BuildForUnit(
            frame, RewriteEngine.CreateDefault(), ctx, unit, frame.Cursor.Index.FullScript, ShadowValues.Initial());

        Assert.DoesNotContain("SET ROWCOUNT", batch.Text);
        Assert.Equal(8, batch.B);
    }

    // C13 (§11.2): a non-zero limit resets ROWCOUNT to 0 in the preamble (protecting bookkeeping),
    // re-applies the debuggee's value on the line IMMEDIATELY before the user statement, and resets
    // to 0 again after — so the connection is left at rest (0) for the next between-statement op.
    [Fact]
    public void RowCount_NonZero_ResetsInPreamble_AppliesBeforeStatement_ResetsAfter()
    {
        var frame = BuildAppendixAFrame(out var ctx);
        var unit = frame.Cursor.Index.All[0];
        var batch = ComposedBatchBuilder.BuildForUnit(
            frame, RewriteEngine.CreateDefault(), ctx, unit, frame.Cursor.Index.FullScript, ShadowValues.Initial(),
            BatchComposition.Default with { RowCount = "2" });
        var lines = batch.Text.Split('\n');

        Assert.Equal("SET ROWCOUNT 2;", lines[batch.B - 2]);   // apply is the line just before the statement
        Assert.StartsWith("UPDATE dbo.T", lines[batch.B - 1]);
        Assert.Equal(1, lines.Count(l => l == "SET ROWCOUNT 2;"));
        Assert.Equal(2, lines.Count(l => l == "SET ROWCOUNT 0;"));   // preamble reset + trailing reset
        // the preamble reset precedes the statement, the trailing reset follows it
        Assert.True(Array.IndexOf(lines, "SET ROWCOUNT 0;") < batch.B - 1);
        Assert.True(Array.LastIndexOf(lines, "SET ROWCOUNT 0;") > batch.B - 1);
    }

    // C13: the debuggee's OWN `SET ROWCOUNT n` (RowCount still "0" at build time) gets ONLY the
    // trailing reset — its user-statement slot set a non-zero limit the resting invariant must
    // neutralize; there is no preamble reset or pre-statement apply (nothing was in force yet).
    [Fact]
    public void ResetRowCountAfterStatement_WithZeroLimit_EmitsOnlyTheTrailingReset()
    {
        var frame = BuildAppendixAFrame(out var ctx);
        var unit = frame.Cursor.Index.All[0];
        var batch = ComposedBatchBuilder.BuildForUnit(
            frame, RewriteEngine.CreateDefault(), ctx, unit, frame.Cursor.Index.FullScript, ShadowValues.Initial(),
            BatchComposition.Default with { ResetRowCountAfterStatement = true });
        var lines = batch.Text.Split('\n');

        Assert.Equal(1, lines.Count(l => l == "SET ROWCOUNT 0;"));
        Assert.DoesNotContain("SET ROWCOUNT 2;", batch.Text);
        Assert.True(Array.IndexOf(lines, "SET ROWCOUNT 0;") > batch.B - 1);   // strictly after the statement
    }

    [Fact]
    public void AppendixA_StateWrite_IsObjectIdGuarded_BothBranches()
    {
        var frame = BuildAppendixAFrame(out var ctx);
        var unit = frame.Cursor.Index.All[0];
        var batch = ComposedBatchBuilder.BuildForUnit(frame, RewriteEngine.CreateDefault(), ctx, unit, frame.Cursor.Index.FullScript, ShadowValues.Initial());

        // M3 (fact 5 + §10.4): BOTH state writes carry BOTH guards — the XACT_STATE()
        // guard moved onto the success path too, because a read-only debuggee
        // statement succeeds inside a doomed transaction and the debugger's own
        // bookkeeping write would otherwise fault the batch with 3930 (an infidelity
        // manufactured by the debugger). §7.1-template deviation flagged in the M3
        // design notes.
        var guardCount = System.Text.RegularExpressions.Regex.Matches(
            batch.Text, System.Text.RegularExpressions.Regex.Escape(
                "IF XACT_STATE() <> -1 AND OBJECT_ID('tempdb..#__dbg_s1') IS NOT NULL")).Count;
        Assert.Equal(2, guardCount);
        Assert.Contains("UPDATE #__dbg_s1 SET [Id]=@Id, [Name]=@Name;", batch.Text);
    }

    [Fact]
    public void AppendixA_ControlRowColumns_MatchFixedContract()
    {
        var frame = BuildAppendixAFrame(out var ctx);
        var unit = frame.Cursor.Index.All[0];
        var batch = ComposedBatchBuilder.BuildForUnit(frame, RewriteEngine.CreateDefault(), ctx, unit, frame.Cursor.Index.FullScript, ShadowValues.Initial());

        Assert.Contains("SELECT 1 AS __dbg_ctl, 1 AS ok,", batch.Text);
        Assert.Contains("AS rc, ", batch.Text);
        Assert.Contains("AS scope_identity,", batch.Text);
        Assert.Contains("@@TRANCOUNT AS trancount, XACT_STATE() AS xact_state", batch.Text);
        Assert.Contains("AS v_0, ", batch.Text);
        Assert.Contains("AS v_0_isnull,", batch.Text);
        Assert.Contains("AS v_1, ", batch.Text);
        Assert.Contains("AS v_1_isnull", batch.Text);

        Assert.Contains("SELECT 1 AS __dbg_ctl, 0 AS ok,", batch.Text);
        Assert.Contains("ERROR_NUMBER() AS err_number", batch.Text);
        Assert.Contains("ERROR_MESSAGE() AS err_message", batch.Text);
    }

    [Fact]
    public void ShadowSubstitute_DeclaredWithInitializer_OnlyWhenRequired()
    {
        var (body, script) = ParseTestHelper.ParseBatch("SET @miss = @@ROWCOUNT;");
        var cursor = ExecutionCursor.Create(body, script);
        var frame = new Frame(0, ModuleIdentity.Script(), cursor, SetOptionEnvironment.Default);
        frame.Variables.Register(new VariableDeclaration("@miss", "int", null,
            (Microsoft.SqlServer.TransactSql.ScriptDom.DeclareVariableElement)ParseSingleDeclareElement("DECLARE @miss int;")));

        var shadows = ShadowValues.Initial();
        var ctx = new RewriteContext("ab12");
        var batch = ComposedBatchBuilder.BuildForUnit(frame, RewriteEngine.CreateDefault(), ctx, frame.Cursor.Index.All[0], script, shadows);

        Assert.Contains("@__dbgab12_sh_rowcount int = NULL", batch.Text);
        Assert.Contains("SET @miss = @__dbgab12_sh_rowcount;", batch.Text);
    }

    [Fact]
    public void ZeroVariables_OmitsStateReadAndWrite()
    {
        var (body, script) = ParseTestHelper.ParseBatch("SELECT 1 AS x;");
        var cursor = ExecutionCursor.Create(body, script);
        var frame = new Frame(0, ModuleIdentity.Script(), cursor, SetOptionEnvironment.Default);
        var ctx = new RewriteContext("zzzz");

        var batch = ComposedBatchBuilder.BuildForUnit(frame, RewriteEngine.CreateDefault(), ctx, frame.Cursor.Index.All[0], script, ShadowValues.Initial());

        Assert.DoesNotContain("FROM #__dbg_s0;", batch.Text);
        Assert.DoesNotContain("OBJECT_ID", batch.Text);
        Assert.DoesNotContain("UPDATE #__dbg_s0", batch.Text);
    }

    // ---- M2: predicate / scalar-eval shells (§6 "through the normal pipeline") ----

    private static Frame FrameWithVar(string bodySql, string declareSql, string name, string type, out string script)
    {
        var (body, s) = ParseTestHelper.ParseBatch(bodySql);
        script = s;
        var cursor = ExecutionCursor.Create(body, s);
        var frame = new Frame(0, ModuleIdentity.Script(), cursor, SetOptionEnvironment.Default);
        frame.Variables.Register(new VariableDeclaration(name, type, null, ParseSingleDeclareElement(declareSql)));
        return frame;
    }

    [Fact]
    public void BuildForPredicate_WrapsCaseWhen_KeepsStateRead_OmitsStateWrite()
    {
        var frame = FrameWithVar("IF @a = 1 SELECT 1 AS x;", "DECLARE @a int;", "@a", "int", out var script);
        var predicate = ((Microsoft.SqlServer.TransactSql.ScriptDom.IfStatement)frame.Cursor.Index.All[0].Fragment).Predicate;

        var batch = ComposedBatchBuilder.BuildForPredicate(
            frame, RewriteEngine.CreateDefault(), new RewriteContext("ab12"), predicate, script, ShadowValues.Initial());

        // §6 verbatim wrapper; NULL/UNKNOWN → 0 = native IF/WHILE falsy semantics.
        Assert.Contains("SELECT CASE WHEN @a = 1 THEN 1 ELSE 0 END AS p;", batch.Text);
        // Full §7.1 shell: state READ + oracle TRY/CATCH + control row…
        Assert.Contains("SELECT @a = [a] FROM #__dbg_s0;", batch.Text);
        Assert.Contains("BEGIN CATCH", batch.Text);
        Assert.Contains("__dbg_ctl", batch.Text);
        // …but NO state write: evaluation is side-effect-free by construction (§12.3 rule).
        Assert.DoesNotContain("UPDATE #__dbg_s0", batch.Text);
        Assert.DoesNotContain("OBJECT_ID", batch.Text);
    }

    [Fact]
    public void BuildForPredicate_RewritesShadowIntrinsics_InsideThePredicate()
    {
        var (body, script) = ParseTestHelper.ParseBatch("IF @@ROWCOUNT = 0 SELECT 1 AS x;");
        var cursor = ExecutionCursor.Create(body, script);
        var frame = new Frame(0, ModuleIdentity.Script(), cursor, SetOptionEnvironment.Default);
        var predicate = ((Microsoft.SqlServer.TransactSql.ScriptDom.IfStatement)body[0]).Predicate;

        var batch = ComposedBatchBuilder.BuildForPredicate(
            frame, RewriteEngine.CreateDefault(), new RewriteContext("ab12"), predicate, script, ShadowValues.Initial());

        Assert.Contains("@__dbgab12_sh_rowcount int = NULL", batch.Text);
        Assert.Contains("CASE WHEN @__dbgab12_sh_rowcount = 0 THEN 1 ELSE 0 END AS p;", batch.Text);
    }

    [Fact]
    public void BuildForScalarEval_ParenthesizesExpression_OmitsStateWrite()
    {
        var frame = FrameWithVar("RETURN @a + 1;", "DECLARE @a int;", "@a", "int", out var script);
        var expression = ((Microsoft.SqlServer.TransactSql.ScriptDom.ReturnStatement)frame.Cursor.Index.All[0].Fragment).Expression!;

        var batch = ComposedBatchBuilder.BuildForScalarEval(
            frame, RewriteEngine.CreateDefault(), new RewriteContext("cd34"), expression, script, ShadowValues.Initial());

        Assert.Contains("SELECT (@a + 1) AS p;", batch.Text);
        Assert.Contains("SELECT @a = [a] FROM #__dbg_s0;", batch.Text);
        Assert.DoesNotContain("UPDATE #__dbg_s0", batch.Text);
    }

    // Engine fact 12: a debuggee predicate eval resets @@ROWCOUNT/@@ERROR to 0 and
    // leaves SCOPE_IDENTITY() alone — the shadows must mirror exactly that, never the
    // predicate batch's own control row.
    [Fact]
    public void ShadowValues_ObservePredicateEvaluation_ZeroesRowcountAndError_KeepsScopeIdentity()
    {
        var shadows = ShadowValues.Initial();
        shadows.ObserveSuccess(new ControlRow(
            Ok: true, Rc: 7, ScopeIdentity: 42m, Trancount: 1, XactState: 0,
            ErrNumber: null, ErrSeverity: null, ErrState: null, ErrLine: null,
            ErrProcedure: null, ErrMessage: null,
            DisplayValues: new Dictionary<int, DisplayValue>()));

        shadows.ObservePredicateEvaluation();

        Assert.Equal("0", shadows.Literal(TsqlDbg.Core.Rewrite.ShadowKind.Rowcount));
        Assert.Equal("0", shadows.Literal(TsqlDbg.Core.Rewrite.ShadowKind.Error));
        Assert.Equal("42", shadows.Literal(TsqlDbg.Core.Rewrite.ShadowKind.ScopeIdentity));
    }

    [Fact]
    public void SyntheticAssignment_UsesDeclareLineForRewrite()
    {
        var (body, script) = ParseTestHelper.ParseBatch("DECLARE @x int = @@ROWCOUNT;");
        var declareStmt = (Microsoft.SqlServer.TransactSql.ScriptDom.DeclareVariableStatement)body[0];
        var decl = VariableDeclaration.Extract(declareStmt, script)[0];

        var (bodyForCursor, scriptForCursor) = ParseTestHelper.ParseBatch("SELECT 1 AS x;");
        var cursor = ExecutionCursor.Create(bodyForCursor, scriptForCursor);
        var frame = new Frame(0, ModuleIdentity.Script(), cursor, SetOptionEnvironment.Default);
        frame.Variables.Register(decl);

        var ctx = new RewriteContext("s7n0");
        var batch = ComposedBatchBuilder.BuildSyntheticAssignment(frame, RewriteEngine.CreateDefault(), ctx, decl, script, ShadowValues.Initial());

        Assert.Contains("SET @x = @__dbgs7n0_sh_rowcount;", batch.Text);
    }
}
