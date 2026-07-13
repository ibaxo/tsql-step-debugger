// M3 (Fable) — the §10 additions to the §7.1 shell: §10.7 re-materialization (with the
// fact-19 XACT_ABORT sandwich, %% escaping, 2047 truncation, MIN(sev,18)), §10.4
// doomed-mode parameter seeding, the __dbg_state snapshot piggyback, the universal
// XACT_STATE() write guard, R7 shadow declarations, and the debugger-initiated eval
// sandwich. Live-verified mechanics: docs/engine-facts.md facts 15-19.
using System.Linq;
using TsqlDbg.Core.Batches;
using TsqlDbg.Core.Interpreter;
using TsqlDbg.Core.Rewrite;
using TsqlDbg.Core.Tests.Interpreter;
using Xunit;

namespace TsqlDbg.Core.Tests.Batches;

public class ComposedBatchM3Tests
{
    private static Frame OneVarFrame(out RewriteContext ctx, string statement = "SET @a = @a + 1;")
    {
        ctx = new RewriteContext("ab12");
        var (body, script) = ParseTestHelper.ParseBatch(statement);
        var cursor = ExecutionCursor.Create(body, script);
        var frame = new Frame(0, ModuleIdentity.Script(), cursor, new SetOptionEnvironment(true, true));
        var declare = (Microsoft.SqlServer.TransactSql.ScriptDom.DeclareVariableStatement)ParseTestHelper.ParseSingle("DECLARE @a int;");
        frame.Variables.Register(new VariableDeclaration("@a", "int", null, declare.Declarations[0]));
        return frame;
    }

    private static ComposedBatch Build(Frame frame, RewriteContext ctx, BatchComposition? composition = null)
        => ComposedBatchBuilder.BuildForUnit(
            frame, RewriteEngine.CreateDefault(), ctx, frame.Cursor.Index.All[0],
            frame.Cursor.Index.FullScript, ShadowValues.Initial(), composition);

    private static readonly ErrorContextValues SampleContext =
        new(8134, 16, 1, 12, null, "Divide by zero error encountered.");

    // ---- §10.7 re-materialization -----------------------------------------------------

    [Fact]
    public void Rematerialize_NestsInnerTryCatch_WithRaiserrorAndUserStatementInInnerCatch()
    {
        var frame = OneVarFrame(out var ctx);
        var batch = Build(frame, ctx, new BatchComposition { Rematerialize = SampleContext });

        Assert.Contains("RAISERROR(N'Divide by zero error encountered.', 16, 1);", batch.Text);
        // Inner TRY re-raises; the user statement sits inside the inner CATCH; the
        // outer oracle wrapper is unchanged (two TRYs, two CATCHes).
        var lines = batch.Text.Split('\n');
        Assert.Equal(2, lines.Count(l => l.Trim() == "BEGIN TRY"));
        Assert.Equal(2, lines.Count(l => l.Trim() == "BEGIN CATCH"));
        Assert.StartsWith("SET @a =", lines[batch.B - 1]);     // B tracks the statement inside the inner CATCH
    }

    [Fact]
    public void Rematerialize_SandwichesXactAbort_OnlyWhenFrameEnvSaysOn()
    {
        var frame = OneVarFrame(out var ctx);

        var withOn = Build(frame, ctx, new BatchComposition { Rematerialize = SampleContext, XactAbortOn = true });
        Assert.Equal("SET XACT_ABORT ON;", withOn.Text.Split('\n')[3]);            // F5: env re-asserted up front
        var offIndex = withOn.Text.IndexOf("SET XACT_ABORT OFF;");
        Assert.True(offIndex > 0);                                                 // before the re-raise (fact 19)
        var restoreIndex = withOn.Text.IndexOf("SET XACT_ABORT ON;", offIndex);    // the restore AFTER the OFF
        Assert.True(restoreIndex > withOn.Text.IndexOf("RAISERROR("));             // inside the inner CATCH
        Assert.True(restoreIndex < withOn.Text.IndexOf("SET @a ="));               // before the user statement

        var withOff = Build(frame, ctx, new BatchComposition { Rematerialize = SampleContext, XactAbortOn = false });
        Assert.Equal("SET XACT_ABORT OFF;", withOff.Text.Split('\n')[3]);          // F5 line carries the env (OFF)
        Assert.DoesNotContain("SET XACT_ABORT ON;", withOff.Text);                 // nothing to restore
    }

    [Fact]
    public void EveryBatch_ReassertsFrameEnvXactAbort_AsFourthPreambleLine()
    {
        // F5 hardening (ratified 2026-07-06): the frame's tracked XACT_ABORT is
        // re-asserted per batch like QI/ANSI_NULLS/NOCOUNT — the session self-heals
        // even if an earlier batch died mid-sandwich with the connection left OFF.
        var frame = OneVarFrame(out var ctx);

        var off = Build(frame, ctx);                                               // Default composition: env OFF
        Assert.Equal("SET XACT_ABORT OFF;", off.Text.Split('\n')[3]);

        var on = Build(frame, ctx, new BatchComposition { XactAbortOn = true });
        Assert.Equal("SET XACT_ABORT ON;", on.Text.Split('\n')[3]);
    }

    [Fact]
    public void Rematerialize_EscapesPercent_CapsSeverity_TruncatesAt2047_PassesStateZero()
    {
        var frame = OneVarFrame(out var ctx);
        var longMessage = "50% done " + new string('x', 2100);
        var batch = Build(frame, ctx, new BatchComposition
        {
            Rematerialize = new ErrorContextValues(50001, 19, 0, null, null, longMessage),
        });

        Assert.Contains("RAISERROR(N'50%% done", batch.Text);                      // fact 19d: %% escaping
        Assert.Contains(", 18, 0);", batch.Text);                                  // fact 19c cap + 19b state 0
        Assert.Contains(new string('x', 2038), batch.Text);                        // 2047 total incl. "50% done "
        Assert.DoesNotContain(new string('x', 2039), batch.Text);                  // fact 19e truncation
    }

    [Fact]
    public void Rematerialize_EscapesQuotesInMessage()
    {
        var frame = OneVarFrame(out var ctx);
        var batch = Build(frame, ctx, new BatchComposition
        {
            Rematerialize = new ErrorContextValues(547, 16, 1, null, null, "conflict with 'FK_x'"),
        });
        Assert.Contains("RAISERROR(N'conflict with ''FK_x''', 16, 1);", batch.Text);
    }

    // ---- §10.4 doomed-mode seeding ------------------------------------------------------

    [Fact]
    public void DoomedSeed_ReplacesTableRead_WithConvertedParameters()
    {
        var frame = OneVarFrame(out var ctx);
        var batch = Build(frame, ctx, new BatchComposition
        {
            DoomedSeedValues = new object?[] { 7 },
            IncludeStateWrite = false,
        });

        Assert.Contains("SELECT @a = CONVERT(int, @__dbgab12_p0);", batch.Text);
        Assert.DoesNotContain("FROM #__dbg_s0", batch.Text);
        Assert.DoesNotContain("UPDATE #__dbg_s0", batch.Text);
        var parameter = Assert.Single(batch.Parameters!);
        Assert.Equal("@__dbgab12_p0", parameter.Name);
        Assert.Equal(7, parameter.Value);
    }

    [Fact]
    public void DoomedSeed_MisalignedWithCatalog_Throws()
    {
        var frame = OneVarFrame(out var ctx);
        Assert.Throws<ArgumentException>(() => Build(frame, ctx, new BatchComposition
        {
            DoomedSeedValues = new object?[] { 7, 8 },
        }));
    }

    // ---- §10.4 / fact 22 doom re-materialization ----------------------------------------

    [Fact]
    public void Redoom_ForcesXactAbortOn_ReestablishesTrancount_Dooms_ThenRestoresEnv()
    {
        var frame = OneVarFrame(out var ctx);
        var batch = Build(frame, ctx, new BatchComposition { RedoomTrancount = 2, XactAbortOn = false });

        var lines = batch.Text.Split('\n').Select(l => l.Trim()).ToArray();
        var on = Array.IndexOf(lines, "SET XACT_ABORT ON;");
        Assert.True(on >= 0, "forced SET XACT_ABORT ON missing (doom needs it regardless of frame env)");
        Assert.Equal("BEGIN TRANSACTION;", lines[on + 1]);
        Assert.Equal("BEGIN TRANSACTION;", lines[on + 2]);                        // logical trancount 2 re-established
        Assert.StartsWith("DECLARE @__dbgab12_doom", lines[on + 3]);
        Assert.Equal("BEGIN TRY SET @__dbgab12_doom = 1/0; END TRY BEGIN CATCH END CATCH;", lines[on + 4]);
        Assert.Equal("SET XACT_ABORT OFF;", lines[on + 5]);                       // frame env (OFF here) restored
        Assert.StartsWith("SET @a =", lines[batch.B - 1]);                        // B accounting survives the prefix
    }

    [Fact]
    public void Redoom_ComposesUnderRematerialization_DoomPrefixFirst()
    {
        var frame = OneVarFrame(out var ctx);
        var batch = Build(frame, ctx, new BatchComposition
        {
            RedoomTrancount = 1,
            Rematerialize = SampleContext,
            XactAbortOn = true,
        });

        // The doom prefix runs first; the §10.7 re-raise then executes inside the
        // already-doomed transaction, which fact 15 verified works and keeps the doom.
        Assert.True(batch.Text.IndexOf("_doom = 1/0") < batch.Text.IndexOf("RAISERROR("));
        Assert.StartsWith("SET @a =", batch.Text.Split('\n')[batch.B - 1]);
    }

    [Fact]
    public void Redoom_BelowOne_Throws()
    {
        var frame = OneVarFrame(out var ctx);
        Assert.Throws<ArgumentException>(() => Build(frame, ctx, new BatchComposition { RedoomTrancount = 0 }));
    }

    // ---- __dbg_state snapshot piggyback -------------------------------------------------

    [Fact]
    public void StateSnapshotSet_EmittedOnBothBranches_ByDefault()
    {
        var frame = OneVarFrame(out var ctx);
        var batch = Build(frame, ctx);
        var count = System.Text.RegularExpressions.Regex.Matches(
            batch.Text, System.Text.RegularExpressions.Regex.Escape("SELECT 1 AS __dbg_state, @a AS [a];")).Count;
        Assert.Equal(2, count);                                                    // TRY + CATCH paths
    }

    [Fact]
    public void PredicateShell_HasNoStateWrite_AndNoSnapshot()
    {
        var frame = OneVarFrame(out var ctx);
        var (body, script) = ParseTestHelper.ParseBatch("IF @a = 1 SELECT 1 AS x;");
        var predicate = ((Microsoft.SqlServer.TransactSql.ScriptDom.IfStatement)body[0]).Predicate;
        var batch = ComposedBatchBuilder.BuildForPredicate(
            frame, RewriteEngine.CreateDefault(), ctx, predicate, script, ShadowValues.Initial());

        Assert.DoesNotContain("UPDATE #__dbg_s0", batch.Text);
        Assert.DoesNotContain("__dbg_state", batch.Text);
    }

    // ---- debugger-initiated eval sandwich (fact 19 / D3's transactional half) ----------

    [Fact]
    public void DebuggerInitiatedEval_SandwichesXactAbort_RestoreAfterEndCatch()
    {
        var frame = OneVarFrame(out var ctx);
        var (conditionBody, script) = ParseTestHelper.ParseBatch("IF @a = 1 SELECT 1 AS x;");
        var predicate = ((Microsoft.SqlServer.TransactSql.ScriptDom.IfStatement)conditionBody[0]).Predicate;
        var batch = ComposedBatchBuilder.BuildForPredicate(
            frame, RewriteEngine.CreateDefault(), ctx, predicate, script, ShadowValues.Initial(),
            new BatchComposition { DebuggerInitiated = true, XactAbortOn = true });

        Assert.Contains("SET XACT_ABORT OFF;", batch.Text);
        Assert.EndsWith("SET XACT_ABORT ON;\n", batch.Text);                       // restore is the final line
    }

    [Fact]
    public void RematerializeAndDebuggerInitiated_AreMutuallyExclusive()
    {
        var frame = OneVarFrame(out var ctx);
        Assert.Throws<InvalidOperationException>(() => Build(frame, ctx, new BatchComposition
        {
            Rematerialize = SampleContext,
            DebuggerInitiated = true,
        }));
    }

    // ---- R7 shadows in the preamble ------------------------------------------------------

    [Fact]
    public void R7Shadows_DeclaredWithContextLiterals_WhenContextActive()
    {
        var frame = OneVarFrame(out var ctx, "SET @a = ERROR_NUMBER();");
        ctx.ErrorContextActive = true;
        var shadows = ShadowValues.Initial();
        shadows.SetErrorContext(new ErrorContextValues(547, 16, 3, 42, null, "it's broken"));

        var batch = ComposedBatchBuilder.BuildForUnit(
            frame, RewriteEngine.CreateDefault(), ctx, frame.Cursor.Index.All[0],
            frame.Cursor.Index.FullScript, shadows);

        Assert.Contains("@__dbgab12_sh_err_number int = 547", batch.Text);
        Assert.Contains("SET @a = @__dbgab12_sh_err_number;", batch.Text);
    }

    [Fact]
    public void R7_DoesNotRewrite_WhenNoContextActive()
    {
        var frame = OneVarFrame(out var ctx, "SET @a = ERROR_NUMBER();");
        var batch = Build(frame, ctx);
        // Unrewritten ERROR_NUMBER() inside our synthetic TRY faithfully reads NULL
        // (Appendix C fact 7's confirmed half).
        Assert.Contains("SET @a = ERROR_NUMBER();", batch.Text);
        Assert.DoesNotContain("_sh_err_number", batch.Text);
    }

    [Fact]
    public void R7_ErrMessageLiteral_EscapesQuotes()
    {
        var shadows = ShadowValues.Initial();
        shadows.SetErrorContext(new ErrorContextValues(547, 16, 1, null, "dbo.p", "it's broken"));
        Assert.Equal("N'it''s broken'", shadows.Literal(ShadowKind.ErrMessage));
        Assert.Equal("N'dbo.p'", shadows.Literal(ShadowKind.ErrProcedure));
        shadows.SetErrorContext(null);
        Assert.Equal("NULL", shadows.Literal(ShadowKind.ErrMessage));
    }

    // ---- shadow observation rules (facts 17/18) -----------------------------------------

    [Fact]
    public void ObserveFault_SetsErrorNumber_ZeroesRowcount_KeepsScopeIdentity()
    {
        var shadows = ShadowValues.Initial();
        shadows.ObserveSuccess(new ControlRow(true, 5, 42m, 1, 0, null, null, null, null, null, null,
            new Dictionary<int, DisplayValue>()));

        shadows.ObserveFault(8134);

        Assert.Equal("8134", shadows.Literal(ShadowKind.Error));
        Assert.Equal("0", shadows.Literal(ShadowKind.Rowcount));
        Assert.Equal("42", shadows.Literal(ShadowKind.ScopeIdentity));
    }

    [Fact]
    public void ObserveWaitFor_ZeroesRowcountAndError()
    {
        var shadows = ShadowValues.Initial();
        shadows.ObserveSuccess(new ControlRow(true, 5, null, 1, 0, null, null, null, null, null, null,
            new Dictionary<int, DisplayValue>()));

        shadows.ObserveWaitFor();

        Assert.Equal("0", shadows.Literal(ShadowKind.Rowcount));
        Assert.Equal("0", shadows.Literal(ShadowKind.Error));
    }
}
