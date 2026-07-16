using Microsoft.SqlServer.TransactSql.ScriptDom;
using TsqlDbg.Core.Batches;
using TsqlDbg.Core.Interpreter;
using TsqlDbg.Core.Parsing;
using TsqlDbg.Core.Rewrite;
using TsqlDbg.Core.State;
using TsqlDbg.Core.Tests.Interpreter;
using Xunit;

namespace TsqlDbg.Core.Tests.State;

// DESIGN §8.1/§9 (A59): the two things the generated SQL must get right for a user-defined
// type — store the ALIAS at its base type, and hand a TABLE type to a TVP formal as a real
// variable. Both are pinned here on the generated text (the live proof is P29/P30).
public class StorageTypeAndTvpTests
{
    private static DeclareVariableElement Declarator(string declareSql) =>
        ((DeclareVariableStatement)ParseTestHelper.ParseSingle(declareSql)).Declarations[0];

    private static VariableDeclaration AliasVariable() =>
        new VariableDeclaration("@Name", "dbo.CustomerName", null, Declarator("DECLARE @Name dbo.CustomerName;"))
        {
            StorageTypeSql = "nvarchar(50)",
            StorageCollation = "SQL_Latin1_General_CP1_CI_AS",
        };

    [Fact]
    public void StorageType_FallsBackToTheDeclaredType_ForEveryOrdinaryVariable()
    {
        // The split exists for exactly one case; nothing else may notice it.
        var plain = new VariableDeclaration("@Id", "int", null, Declarator("DECLARE @Id int;"));
        Assert.Equal("int", plain.StorageType);
        Assert.Null(plain.StorageTypeSql);
    }

    [Fact]
    public void StateTable_StoresTheAliasAtItsBaseType_NotItsDeclaredName()
    {
        // The bug A59 fixes: tempdb cannot see dbo.CustomerName (fact 34a, msg 2715), so the
        // state table's column must be the BASE type.
        var ddl = StateTableDdlBuilder.BuildCreateTable(0, new[] { new VariableSlot(0, AliasVariable()) });

        Assert.Equal(
            "CREATE TABLE #__dbg_s0 ([Name] nvarchar(50) COLLATE SQL_Latin1_General_CP1_CI_AS NULL);",
            ddl);
        Assert.DoesNotContain("dbo.CustomerName", ddl);
    }

    [Fact]
    public void ReseedConvert_TargetsTheBareBaseType_NeitherTheAliasNorTheCollation()
    {
        // CONVERT to an alias type is refused outright by the engine (fact 34b, msg 243), so
        // this CONVERT must never name it — and a CONVERT target takes NO collation either
        // (`CONVERT(nvarchar(50) COLLATE …, @p)` is msg 156, found live on the doomed path).
        // The collation belongs to the state-table COLUMN, and only there.
        var update = StateTableDdlBuilder.BuildReseedUpdate(
            0, new[] { new VariableSlot(0, AliasVariable()) }, _ => "@p0");

        Assert.Contains("CONVERT(nvarchar(50), @p0)", update);
        Assert.DoesNotContain("CONVERT(dbo.CustomerName", update);
        Assert.DoesNotContain("COLLATE", update);
    }

    [Fact]
    public void ComposedBatch_DeclaresTheAliasVariableAtItsDECLAREDType()
    {
        // The other half of the split: the debuggee's own variable IS the user's type, so
        // execution stays native. Only the debugger's storage and conversions are base-typed.
        var ctx = new RewriteContext("7f3a");
        var (body, script) = ParseTestHelper.ParseBatch("SELECT @Name;");
        var frame = new Frame(0, ModuleIdentity.Script("s"), ExecutionCursor.Create(body, script),
            SetOptionEnvironment.Default);
        frame.Variables.Register(AliasVariable());

        var batch = ComposedBatchBuilder.BuildForUnit(
            frame, RewriteEngine.CreateDefault(), ctx, frame.Cursor.Index.All[0], script, ShadowValues.Initial());

        Assert.Contains("DECLARE @Name dbo.CustomerName,", batch.Text);
    }

    /// <summary>The frame-chain name scope R1 resolves `@t` through (Session's own is private).</summary>
    private sealed class TableVarScope : ITempNameScope
    {
        public int CurrentFrameOrdinal => 0;

        public string? ResolveReference(string originalName, TempObjectKind kind)
            => kind == TempObjectKind.TableVariable && originalName.Equals("@t", StringComparison.OrdinalIgnoreCase)
                ? "#__dbgtv_0_t"
                : null;

        public bool HasLiveTempTable(string originalName) => false;
    }

    private static Frame TvpFrame(string statementSql, out RewriteContext ctx, string? identityColumn = null)
    {
        ctx = new RewriteContext("7f3a") { TempNames = new TableVarScope() };
        var (body, script) = ParseTestHelper.ParseBatch(statementSql);
        var frame = new Frame(0, ModuleIdentity.Script("s"), ExecutionCursor.Create(body, script),
            SetOptionEnvironment.Default);
        frame.TableTypeVariables.Add("@t", new TableTypeVariable
        {
            Name = "@t",
            Type = new UserTypeEntry("dbo", "OrderRows", UserTypeKind.Table),
            RealizationName = "#__dbgtv_0_t",
            InsertableColumns = new[] { "nm", "qty" },
            IdentityColumn = identityColumn,
        });

        // A table-type variable is a table variable in every respect from here on (§8.2) —
        // including the R1 rename map, which is what rewrites `FROM @t` to the realization.
        frame.TempObjects.Add(new TempObjectEntry
        {
            OriginalName = "@t",
            PhysicalName = "#__dbgtv_0_t",
            Kind = TempObjectKind.TableVariable,
            CreatedAtTrancount = 0,
            SurvivesBatchBoundary = false,
        });
        return frame;
    }

    [Fact]
    public void TvpArgument_IsMaterializedFromTheRealization()
    {
        // A #temp cannot be passed to a table-valued parameter — only a variable of the type
        // can. So an `EXEC p @rows = @t` argument gets the real thing, filled from the
        // realization (§9). IDENTITY/computed columns are excluded (fact 34e -> C28).
        var frame = TvpFrame("EXEC dbo.consume @Rows = @t;", out var ctx);

        var batch = ComposedBatchBuilder.BuildForUnit(
            frame, RewriteEngine.CreateDefault(), ctx, frame.Cursor.Index.All[0],
            frame.Cursor.Index.FullScript, ShadowValues.Initial());

        Assert.Contains("DECLARE @t [dbo].[OrderRows];", batch.Text);
        Assert.Contains("INSERT INTO @t ([nm], [qty]) SELECT [nm], [qty] FROM [#__dbgtv_0_t];", batch.Text);
    }

    [Fact]
    public void TableReference_CostsNoMaterialization()
    {
        // `SELECT … FROM @t` is a VariableTableReference — R1 has already rewritten it to the
        // realization, and materializing there would be a pointless copy (and a needless
        // @@IDENTITY perturbation, C26). Only a SCALAR reference is a TVP argument.
        var frame = TvpFrame("SELECT COUNT(*) FROM @t;", out var ctx);

        var batch = ComposedBatchBuilder.BuildForUnit(
            frame, RewriteEngine.CreateDefault(), ctx, frame.Cursor.Index.All[0],
            frame.Cursor.Index.FullScript, ShadowValues.Initial());

        Assert.DoesNotContain("DECLARE @t [dbo].[OrderRows];", batch.Text);
        Assert.DoesNotContain("INSERT INTO @t", batch.Text);
        Assert.Contains("[#__dbgtv_0_t]", batch.Text);      // R1 rewrote the table reference
    }

    // ---- A59 review F1: EVERY composed-batch shape, not just BuildForUnit. -----------------
    // The first cut materialized TVP arguments in BuildForUnit and BuildForRepl only. A batch
    // that references @t without declaring it dies with error 137 — "Must declare the scalar
    // variable @t" — which is BATCH-ABORTING: the session is over. So `SET @n = dbo.cnt(@t)`
    // worked and `DECLARE @n int = dbo.cnt(@t)` (a synthetic assignment), `IF dbo.cnt(@t) > 1`
    // (a predicate) and `RETURN dbo.cnt(@t)` (a scalar eval) all killed the session, where
    // native runs fine. Confirmed live before the fix; pinned per-shape here.

    private static (Frame Frame, RewriteContext Ctx) EvalFrame(string statementSql, string? identity = null)
    {
        var frame = TvpFrame(statementSql, out var ctx, identity);
        return (frame, ctx);
    }

    [Fact]
    public void SyntheticAssignment_MaterializesTheTvpArgument()
    {
        // DECLARE @n int = dbo.cnt(@t);  — the initializer is its own executable SU (§7.2).
        var (frame, ctx) = EvalFrame("DECLARE @n int = dbo.cnt(@t);");
        var declaration = VariableDeclaration.Extract(
            (DeclareVariableStatement)frame.Cursor.Index.All[0].Fragment, frame.Cursor.Index.FullScript)[0];
        frame.Variables.Register(declaration);

        var batch = ComposedBatchBuilder.BuildSyntheticAssignment(
            frame, RewriteEngine.CreateDefault(), ctx, declaration,
            frame.Cursor.Index.FullScript, ShadowValues.Initial());

        Assert.Contains("DECLARE @t [dbo].[OrderRows];", batch.Text);
        Assert.Contains("INSERT INTO @t ([nm], [qty])", batch.Text);
    }

    [Fact]
    public void Predicate_MaterializesTheTvpArgument()
    {
        // IF dbo.cnt(@t) > 1 …
        var (frame, ctx) = EvalFrame("IF dbo.cnt(@t) > 1 SET @n = 99;");
        var predicate = ((IfStatement)frame.Cursor.Index.All[0].Fragment).Predicate;

        var batch = ComposedBatchBuilder.BuildForPredicate(
            frame, RewriteEngine.CreateDefault(), ctx, predicate,
            frame.Cursor.Index.FullScript, ShadowValues.Initial());

        Assert.Contains("DECLARE @t [dbo].[OrderRows];", batch.Text);
        Assert.Contains("INSERT INTO @t ([nm], [qty])", batch.Text);
    }

    [Fact]
    public void ScalarEval_MaterializesTheTvpArgument()
    {
        // RETURN dbo.cnt(@t);  — and the §12.4 watch / §13 logpoint shells share this builder.
        var (frame, ctx) = EvalFrame("RETURN dbo.cnt(@t);");
        var expression = ((ReturnStatement)frame.Cursor.Index.All[0].Fragment).Expression!;

        var batch = ComposedBatchBuilder.BuildForScalarEval(
            frame, RewriteEngine.CreateDefault(), ctx, expression,
            frame.Cursor.Index.FullScript, ShadowValues.Initial());

        Assert.Contains("DECLARE @t [dbo].[OrderRows];", batch.Text);
    }

    [Fact]
    public void MultiScalarEval_MaterializesEachTvpArgumentExactlyOnce()
    {
        // A23 logpoint shell: N expressions, ONE round trip — and one materialization, however
        // many of them mention @t (a second DECLARE @t would itself be error 134).
        var (frame, ctx) = EvalFrame("SELECT 1;");
        var first = ScriptParser.ParseScalarExpression("dbo.cnt(@t)", true, 150, out _)!;
        var second = ScriptParser.ParseScalarExpression("dbo.cnt(@t) + 1", true, 150, out _)!;

        var batch = ComposedBatchBuilder.BuildForMultiScalarEval(
            frame, RewriteEngine.CreateDefault(), ctx,
            new (ScalarExpression, string)[] { (first, "dbo.cnt(@t)"), (second, "dbo.cnt(@t) + 1") },
            ShadowValues.Initial());

        var declarations = batch.Text.Split("DECLARE @t [dbo].[OrderRows];").Length - 1;
        Assert.Equal(1, declarations);
    }

    // ---- A59 review F6: the ORDER BY that makes C28's promise a guarantee. ------------------

    [Fact]
    public void Materialization_OrdersByTheIdentityColumn_SoIdentityValuesReplayInInsertOrder()
    {
        // Identity is assigned in INSERT order, and INSERT…SELECT only fixes that order under
        // an explicit ORDER BY. Without it, C28's "contiguous rows keep their values" held only
        // when the plan happened to return rows in identity order — true for p30 (whose
        // clustered PK IS the identity column) and false for a type clustered on anything else.
        var frame = TvpFrame("EXEC dbo.consume @Rows = @t;", out var ctx, identityColumn: "id");

        var batch = ComposedBatchBuilder.BuildForUnit(
            frame, RewriteEngine.CreateDefault(), ctx, frame.Cursor.Index.All[0],
            frame.Cursor.Index.FullScript, ShadowValues.Initial());

        Assert.Contains(
            "INSERT INTO @t ([nm], [qty]) SELECT [nm], [qty] FROM [#__dbgtv_0_t] ORDER BY [id];",
            batch.Text);
    }

    [Fact]
    public void Materialization_OmitsTheOrderBy_WhenTheTypeHasNoIdentityColumn()
    {
        var frame = TvpFrame("EXEC dbo.consume @Rows = @t;", out var ctx);

        var batch = ComposedBatchBuilder.BuildForUnit(
            frame, RewriteEngine.CreateDefault(), ctx, frame.Cursor.Index.All[0],
            frame.Cursor.Index.FullScript, ShadowValues.Initial());

        Assert.DoesNotContain("ORDER BY", batch.Text);
    }

    // ---- A59 review: the materialization MOVES the identity chain (fact 34h). --------------

    [Fact]
    public void MaterializingAnIdentityBearingType_FlagsTheBatchAsMovingTheIdentityChain()
    {
        // Probed: an INSERT into a table variable with an IDENTITY column overwrites
        // SCOPE_IDENTITY() — it took a real table's 100 down to the table variable's 2. That
        // capture feeds the R6 shadow, so a debuggee `SELECT SCOPE_IDENTITY()` after an
        // `EXEC p @rows = @t` would read the DEBUGGER's bookkeeping INSERT. The session poisons
        // the A26/D1 scope chain on this flag; the shadow then serves its client-modeled value.
        var identityBearing = TvpFrame("EXEC dbo.consume @Rows = @t;", out var ctx, identityColumn: "id");
        Assert.True(ComposedBatchBuilder.BuildForUnit(
            identityBearing, RewriteEngine.CreateDefault(), ctx, identityBearing.Cursor.Index.All[0],
            identityBearing.Cursor.Index.FullScript, ShadowValues.Initial()).MovesIdentityChain);

        // No identity column -> no identity generated -> the chain is untouched, and poisoning
        // it would needlessly freeze the shadow.
        var plain = TvpFrame("EXEC dbo.consume @Rows = @t;", out var plainCtx);
        Assert.False(ComposedBatchBuilder.BuildForUnit(
            plain, RewriteEngine.CreateDefault(), plainCtx, plain.Cursor.Index.All[0],
            plain.Cursor.Index.FullScript, ShadowValues.Initial()).MovesIdentityChain);

        // And a mere table READ materializes nothing, so it moves nothing.
        var read = TvpFrame("SELECT COUNT(*) FROM @t;", out var readCtx, identityColumn: "id");
        Assert.False(ComposedBatchBuilder.BuildForUnit(
            read, RewriteEngine.CreateDefault(), readCtx, read.Cursor.Index.All[0],
            read.Cursor.Index.FullScript, ShadowValues.Initial()).MovesIdentityChain);
    }

    // ---- A62 (§11.3 step 2 / §9): step-INTO a callee with a TVP formal. The formal's own
    // realization (a #temp, hoisted empty) is seeded from the CALLER's table-type-variable
    // realization (also a #temp) — the v2 of the #temp -> DECLARE @t materialization above,
    // target-shifted to the formal's realization. The READONLY formal is never copied back. ----

    private static TableTypeVariable TvpFormalTarget(string? identityColumn = null) => new TableTypeVariable
    {
        Name = "@rows",
        Type = new UserTypeEntry("dbo", "OrderRows", UserTypeKind.Table),
        RealizationName = "#__dbgtv_2_rows",
        InsertableColumns = new[] { "nm", "qty" },
        IdentityColumn = identityColumn,
    };

    [Fact]
    public void BuildTvpFormalSeed_CopiesCallerRealizationIntoTheFormalRealization()
    {
        // The callee's #temp is filled from the caller's #temp, same table type, same columns —
        // no DECLARE @t (unlike the pass-as-argument case): the formal's realization already exists.
        var seed = ComposedBatchBuilder.BuildTvpFormalSeed(TvpFormalTarget(), "#__dbgtv_1_source");

        Assert.Equal(
            "INSERT INTO [#__dbgtv_2_rows] ([nm], [qty]) SELECT [nm], [qty] FROM [#__dbgtv_1_source];",
            seed);
    }

    [Fact]
    public void BuildTvpFormalSeed_OrdersByTheIdentityColumn_SoIdentityValuesReplayInInsertOrder()
    {
        // Same load-bearing ORDER BY as A59 rider 2: identity is assigned in insert order, so a
        // contiguous source reproduces its identity values (C28), not an artifact of the plan.
        var seed = ComposedBatchBuilder.BuildTvpFormalSeed(TvpFormalTarget(identityColumn: "id"), "#__dbgtv_1_source");

        Assert.Equal(
            "INSERT INTO [#__dbgtv_2_rows] ([nm], [qty]) SELECT [nm], [qty] FROM [#__dbgtv_1_source] ORDER BY [id];",
            seed);
    }

    [Fact]
    public void BuildTvpFormalSeed_ReturnsNull_WhenTheTypeHasNoInsertableColumns()
    {
        // Every column IDENTITY/computed: nothing can be supplied to a table variable (fact 34e),
        // so the realization stays empty (rides C28) rather than emitting an empty column list.
        var allGenerated = TvpFormalTarget(identityColumn: "id");
        allGenerated.InsertableColumns = System.Array.Empty<string>();

        Assert.Null(ComposedBatchBuilder.BuildTvpFormalSeed(allGenerated, "#__dbgtv_1_source"));
    }
}
