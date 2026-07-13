// M4 (Fable) — DESIGN §7.4 rules R1-R3 against a fake frame-chain scope: creation
// minting, chain-resolved references, miss-stays-unpatched (faithful-by-miss), R8
// literal immunity, and the R3 LOCAL→GLOBAL patch. Design decisions D7 in
// docs/archive/reviews/m4-frames-design-notes-fable.md.
using Microsoft.SqlServer.TransactSql.ScriptDom;
using TsqlDbg.Core.Interpreter;
using TsqlDbg.Core.Parsing;
using TsqlDbg.Core.Rewrite;
using Xunit;

namespace TsqlDbg.Core.Tests.Rewrite;

public sealed class R1R2R3RuleTests
{
    private sealed class FakeScope : ITempNameScope
    {
        private readonly Dictionary<(string Name, TempObjectKind Kind), string> _map = new();

        public int CurrentFrameOrdinal { get; init; }

        public FakeScope Map(string original, TempObjectKind kind, string physical)
        {
            _map[(original.ToLowerInvariant(), kind)] = physical;
            return this;
        }

        public string? ResolveReference(string originalName, TempObjectKind kind)
            => _map.TryGetValue((originalName.ToLowerInvariant(), kind), out var physical) ? physical : null;

        // A20: a mapped live TempTable entry IS the collision (mirrors the session's
        // chain predicate — same source of truth as ResolveReference here).
        public bool HasLiveTempTable(string originalName)
            => _map.ContainsKey((originalName.ToLowerInvariant(), TempObjectKind.TempTable));
    }

    private static string Rewrite(string sql, FakeScope? scope)
    {
        var fragment = ScriptParser.Parse(sql, initialQuotedIdentifiers: true, compatLevel: 150, out var errors);
        Assert.Empty(errors);
        var statement = ((TSqlScript)fragment).Batches[0].Statements[0];
        var context = new RewriteContext("t0") { TempNames = scope };
        return RewriteEngine.CreateDefault().Rewrite(statement, sql, context).PatchedText;
    }

    // ---- R2: user #temp tables ---------------------------------------------------------

    // A20 (ratified 2026-07-06): creates rename ONLY on collision with a live outer
    // entry; a non-colliding create keeps its original name (what a stepped-over
    // callee's compiled body sees). Revised from always-rename as the ratified change
    // (docs/archive/reviews/m5-a20-r2-collision-rename-fable.md), not a weakening.
    [Fact]
    public void R2_CreateTable_NoCollision_KeepsOriginalName()
        => Assert.Equal("CREATE TABLE #w (a int);",
            Rewrite("CREATE TABLE #w (a int);", new FakeScope { CurrentFrameOrdinal = 2 }));

    [Fact]
    public void R2_CreateTable_Collision_MintsCurrentFrameName()
        => Assert.Equal("CREATE TABLE [#w__f2] (a int);",
            Rewrite("CREATE TABLE #w (a int);",
                new FakeScope { CurrentFrameOrdinal = 2 }.Map("#w", TempObjectKind.TempTable, "#w")));

    // A20: a reference whose resolved physical name equals the written name is left
    // unpatched (no-op brackets add nothing) — the resolve still runs (A14 capture).
    [Fact]
    public void R2_Reference_ResolvedToItsOwnName_StaysUnpatched()
        => Assert.Equal("INSERT INTO #w VALUES (1);",
            Rewrite("INSERT INTO #w VALUES (1);",
                new FakeScope().Map("#w", TempObjectKind.TempTable, "#w")));

    [Fact]
    public void R2_Reference_ResolvesThroughChain()
        => Assert.Equal("INSERT INTO [#w__f0] VALUES (1);",
            Rewrite("INSERT INTO #w VALUES (1);",
                new FakeScope().Map("#w", TempObjectKind.TempTable, "#w__f0")));

    [Fact]
    public void R2_UnresolvedReference_StaysUnpatched_FaithfulByMiss()
        => Assert.Equal("SELECT * FROM #gone;", Rewrite("SELECT * FROM #gone;", new FakeScope()));

    [Fact]
    public void R2_GlobalTempTable_IsNeverRenamed()
        => Assert.Equal("SELECT * FROM ##shared;",
            Rewrite("SELECT * FROM ##shared;",
                new FakeScope().Map("##shared", TempObjectKind.TempTable, "##nope")));

    [Fact]
    public void R2_NameInsideStringLiteral_IsNeverPatched_R8()
        => Assert.Equal("SELECT OBJECT_ID('tempdb..#w') AS o;",
            Rewrite("SELECT OBJECT_ID('tempdb..#w') AS o;",
                new FakeScope().Map("#w", TempObjectKind.TempTable, "#w__f0")));

    [Fact]
    public void R2_DropTable_ReferenceIsPatched()
        => Assert.Equal("DROP TABLE [#w__f0];",
            Rewrite("DROP TABLE #w;", new FakeScope().Map("#w", TempObjectKind.TempTable, "#w__f0")));

    [Fact]
    public void R2_RulesAreSilent_WithoutAScope()
        => Assert.Equal("CREATE TABLE #w (a int);", Rewrite("CREATE TABLE #w (a int);", null));

    // ---- R1: table variables ------------------------------------------------------------

    [Fact]
    public void R1_TableReference_ResolvesToRealization()
        => Assert.Equal("SELECT a FROM [#__dbgtv_0_t];",
            Rewrite("SELECT a FROM @t;", new FakeScope().Map("@t", TempObjectKind.TableVariable, "#__dbgtv_0_t")));

    [Fact]
    public void R1_InsertTarget_ResolvesToRealization()
        => Assert.Equal("INSERT INTO [#__dbgtv_0_t] VALUES (1);",
            Rewrite("INSERT INTO @t VALUES (1);", new FakeScope().Map("@t", TempObjectKind.TableVariable, "#__dbgtv_0_t")));

    [Fact]
    public void R1_ScalarVariable_IsStructurallySafe()
        => Assert.Equal("SET @t = 1;",
            Rewrite("SET @t = 1;", new FakeScope().Map("@t", TempObjectKind.TableVariable, "#__dbgtv_0_t")));

    [Fact]
    public void R1_UnresolvedTableVariable_StaysUnpatched_NativeError1087()
        => Assert.Equal("SELECT a FROM @nope;", Rewrite("SELECT a FROM @nope;", new FakeScope()));

    // ---- R3: cursors ---------------------------------------------------------------------

    [Fact]
    public void R3_Declare_RenamesAndForcesGlobal()
        => Assert.Equal("DECLARE [c__f1_c] CURSOR GLOBAL FOR SELECT 1 AS x;",
            Rewrite("DECLARE c CURSOR LOCAL FOR SELECT 1 AS x;", new FakeScope { CurrentFrameOrdinal = 1 }));

    [Fact]
    public void R3_OpenFetchCloseDeallocate_PatchCursorId()
    {
        var scope = new FakeScope().Map("c", TempObjectKind.Cursor, "c__f1_c");
        Assert.Equal("OPEN [c__f1_c];", Rewrite("OPEN c;", scope));
        Assert.Equal("FETCH NEXT FROM [c__f1_c] INTO @x;", Rewrite("FETCH NEXT FROM c INTO @x;", scope));
        Assert.Equal("CLOSE [c__f1_c];", Rewrite("CLOSE c;", scope));
        Assert.Equal("DEALLOCATE [c__f1_c];", Rewrite("DEALLOCATE c;", scope));
    }

    [Fact]
    public void R3_WhereCurrentOf_PatchesCursorId()
        => Assert.Equal("DELETE dbo.t WHERE CURRENT OF [c__f1_c];",
            Rewrite("DELETE dbo.t WHERE CURRENT OF c;", new FakeScope().Map("c", TempObjectKind.Cursor, "c__f1_c")));

    [Fact]
    public void R3_DeclareBody_StillGetsOtherRules()
        // The FOR SELECT body of a DECLARE runs through R1/R2 too (D7).
        => Assert.Equal("DECLARE [c__f0_c] CURSOR GLOBAL FOR SELECT a FROM [#w__f0];",
            Rewrite("DECLARE c CURSOR LOCAL FOR SELECT a FROM #w;",
                new FakeScope().Map("#w", TempObjectKind.TempTable, "#w__f0")));
}
