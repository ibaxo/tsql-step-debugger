using Microsoft.SqlServer.TransactSql.ScriptDom;
using TsqlDbg.Core.Batches;
using TsqlDbg.Core.Interpreter;
using TsqlDbg.Core.Rewrite;
using TsqlDbg.Core.Tests.Interpreter;
using Xunit;

namespace TsqlDbg.Core.Tests.Batches;

// DESIGN §9 (A63): BuildForCursorVariableAssign reifies `SET @c = CURSOR <def>` as a generated
// `DECLARE [phys] CURSOR GLOBAL <options> FOR <select>` (its SET…CURSOR prefix is not span-patchable,
// §7.4 invariant 1). Options + FOR are reconstructed deterministically because ScriptDom's
// CursorDefinition span drops the FOR keyword in the no-option case.
public class CursorVariableAssignBuilderTests
{
    private static Frame BuildFrame(string statementText, out RewriteContext ctx)
    {
        ctx = new RewriteContext("7f3a");
        var (body, script) = ParseTestHelper.ParseBatch(statementText);
        var cursor = ExecutionCursor.Create(body, script);
        return new Frame(0, ModuleIdentity.Script("uspChild"), cursor, new SetOptionEnvironment(true, true));
    }

    private static string Generate(string statementText)
    {
        var frame = BuildFrame(statementText, out var ctx);
        var unit = frame.Cursor.Index.All[0];
        var batch = ComposedBatchBuilder.BuildForCursorVariableAssign(
            frame, RewriteEngine.CreateDefault(), ctx, unit, frame.Cursor.Index.FullScript, ShadowValues.Initial());
        // The generated DECLARE is the first line inside BEGIN TRY.
        return batch.Text.Split('\n').First(l => l.Contains("DECLARE [", StringComparison.Ordinal) && l.Contains("CURSOR", StringComparison.Ordinal));
    }

    // The reifying DECLARE is always GLOBAL (persists the cursor across the debugger's per-SU
    // batches) and always guarded by CURSOR_STATUS so a re-SET of the same variable is faithful.
    [Fact]
    public void NoOptions_EmitsGlobalForSelect_WithGuard()
    {
        var line = Generate("SET @c = CURSOR FOR SELECT val FROM dbo.t ORDER BY id;");
        Assert.Contains("IF CURSOR_STATUS('global', N'c__f0_cv') >= -1 DEALLOCATE [c__f0_cv];", line);
        Assert.Contains("DECLARE [c__f0_cv] CURSOR GLOBAL FOR SELECT val FROM dbo.t ORDER BY id;", line);
    }

    // LOCAL is dropped (GLOBAL synthesized); a non-scope option (SCROLL) is preserved byte-exact.
    [Fact]
    public void LocalScroll_DropsLocal_KeepsScroll_KeepsFor()
    {
        var line = Generate("SET @c = CURSOR LOCAL SCROLL FOR SELECT id FROM dbo.t;");
        Assert.Contains("DECLARE [c__f0_cv] CURSOR GLOBAL SCROLL FOR SELECT id FROM dbo.t;", line);
        Assert.DoesNotContain("LOCAL", line);
    }

    // An explicit GLOBAL option is also dropped (GLOBAL is already synthesized) — no double keyword.
    [Fact]
    public void ExplicitGlobal_NotDuplicated()
    {
        var line = Generate("SET @c = CURSOR GLOBAL STATIC FOR SELECT 1 AS v;");
        Assert.Contains("CURSOR GLOBAL STATIC FOR SELECT 1 AS v;", line);
        Assert.DoesNotContain("GLOBAL GLOBAL", line);
    }

    // A63 (F1): every composed batch of a frame that declares a cursor variable emits a real
    // (unallocated) `DECLARE @c CURSOR;` — so an OPEN/FETCH of a never-SET cursor variable faults
    // with native 16950 through the §7.1 oracle instead of a bogus 137 "must declare @c".
    [Fact]
    public void FrameCursorVariables_AreDeclaredInEveryBatch()
    {
        var frame = BuildFrame("SELECT 1 AS v;", out var ctx);
        frame.CursorVariables.Add("@c");
        frame.CursorVariables.Add("@d");
        var unit = frame.Cursor.Index.All[0];
        var batch = ComposedBatchBuilder.BuildForUnit(
            frame, RewriteEngine.CreateDefault(), ctx, unit, frame.Cursor.Index.FullScript, ShadowValues.Initial());

        Assert.Contains("DECLARE @c CURSOR;", batch.Text);
        Assert.Contains("DECLARE @d CURSOR;", batch.Text);
    }
}
