using Microsoft.SqlServer.TransactSql.ScriptDom;
using TsqlDbg.Core.Batches;
using TsqlDbg.Core.Interpreter;
using TsqlDbg.Core.Rewrite;
using TsqlDbg.Core.State;
using TsqlDbg.Core.Tests.Interpreter;
using Xunit;

namespace TsqlDbg.Core.Tests.Batches;

// M6 §14/A21 — the boost composition layer (design note §7 "builder pins"):
// insertion-at-offset API, marker computation (suppression + WritesState),
// BuildForBoostedSubtree shape (prologue, line neutrality, no trailing semicolon,
// F2 CATCH scope_identity), and the B2/B5 composition guards.
public class BoostBuilderTests
{
    // ---------------------------------------------------------------- insertion API

    [Fact]
    public void AddInsertionAfter_ComposesWithAdjacentReplacement()
    {
        var (body, script) = ParseTestHelper.ParseBatch("SET @x = 1;\nSET @y = 2;");
        var first = body[0];
        var collector = new SpanPatchCollector(RuleId.Boost);
        collector.AddInsertionAfter(first, ";MARKER");
        // A replacement starting exactly at the insertion offset (the next statement's
        // start would be later; use a Test-rule patch on the SECOND statement).
        var replace = new SpanPatchCollector(RuleId.Test);
        replace.Add(body[1], "SET @y = 99;");

        var all = collector.Patches.Concat(replace.Patches).ToList();
        var sliceStart = first.StartOffset;
        var sliceLength = body[1].StartOffset + body[1].FragmentLength - sliceStart;
        var patched = SpanPatcher.Apply(script, sliceStart, sliceLength, all);

        Assert.Contains("SET @x = 1;;MARKER", patched);
        Assert.Contains("SET @y = 99;", patched);
    }

    [Fact]
    public void AddInsertionAfter_InsideAReplacedSpan_IsAHardOverlapAssert()
    {
        var (body, script) = ParseTestHelper.ParseBatch("SET @x = 1 + 2;");
        var statement = body[0];
        // Replace the whole statement, and insert after an interior fragment (the
        // initializer expression ends before the statement does).
        var expression = ((SetVariableStatement)statement).Expression;
        var collector = new SpanPatchCollector(RuleId.Boost);
        collector.AddInsertionAfter(expression, ";MARKER");
        Assert.True(expression.StartOffset + expression.FragmentLength < statement.StartOffset + statement.FragmentLength);
        var replace = new SpanPatchCollector(RuleId.Test);
        replace.Add(statement, "SET @x = 0;");

        Assert.Throws<SpanOverlapException>(() => SpanPatcher.Apply(
            script, statement.StartOffset, statement.FragmentLength,
            collector.Patches.Concat(replace.Patches).ToList()));
    }

    [Fact]
    public void AddInsertionAfter_RejectsLineBreaks()
    {
        var (body, _) = ParseTestHelper.ParseBatch("SET @x = 1;");
        var collector = new SpanPatchCollector(RuleId.Boost);
        Assert.Throws<InvalidOperationException>(() => collector.AddInsertionAfter(body[0], ";TWO\nLINES"));
    }

    // ------------------------------------------------------------- marker computation

    private static TSqlStatement RootOf(string script)
    {
        var (body, _) = ParseTestHelper.ParseBatch(script);
        return body[0];
    }

    [Fact]
    public void Markers_WhileBody_NeverSuppressed_EvenAtTheTail()
    {
        var root = RootOf("WHILE @i < 3\nBEGIN\n    INSERT dbo.T VALUES (1);\n    SET @i = @i + 1;\nEND");
        var markers = BoostSubtreeMarkers.Compute(root);

        Assert.Equal(2, markers.Count);
        Assert.All(markers, m => Assert.False(m.Suppressed));   // body tail flows to the predicate re-eval
        Assert.False(markers[0].WritesState);                   // INSERT assigns nothing
        Assert.True(markers[1].WritesState);                    // SET @i
    }

    [Fact]
    public void Markers_RootIfBranchTail_Suppressed_MidListNot()
    {
        var root = RootOf("IF @x = 1\nBEGIN\n    SET @x = 2;\n    INSERT dbo.T VALUES (1);\nEND");
        var markers = BoostSubtreeMarkers.Compute(root);

        Assert.Equal(2, markers.Count);
        Assert.False(markers[0].Suppressed);                    // SET is mid-list
        Assert.True(markers[1].Suppressed);                     // branch tail exits the subtree (A21/fact 27)
    }

    [Fact]
    public void Markers_SingleStatementIfBranches_HaveNoInteriorMarkers()
    {
        var root = RootOf("IF @x = 1 SET @x = 2; ELSE SET @x = 3;");
        Assert.Empty(BoostSubtreeMarkers.Compute(root));
    }

    [Fact]
    public void Markers_NestedShapes_SuppressionIsTransitive_AndLoopBodiesStayEmitted()
    {
        // Root IF branch block: [ S1; WHILE p BEGIN S2 END ] — the after-S2 marker is
        // in a loop body (never suppressed); the after-WHILE marker is the branch tail
        // (suppressed); after-S1 is mid-list (emitted).
        var root = RootOf(
            "IF @x = 1\nBEGIN\n    SET @x = 2;\n    WHILE @i < 2\n    BEGIN\n        SET @i = @i + 1;\n    END\nEND");
        var markers = BoostSubtreeMarkers.Compute(root);

        Assert.Equal(3, markers.Count);
        // Document order of insertion points: after S1, after S2 (interior), after WHILE.
        Assert.False(markers[0].Suppressed);
        Assert.IsType<SetVariableStatement>(markers[0].Child);
        Assert.False(markers[1].Suppressed);
        Assert.IsType<SetVariableStatement>(markers[1].Child);
        Assert.True(markers[2].Suppressed);
        Assert.IsType<WhileStatement>(markers[2].Child);
        // Pos is the marker-table index, monotonic in document order.
        Assert.Equal(new[] { 0, 1, 2 }, markers.Select(m => m.Pos).ToArray());
    }

    [Fact]
    public void Markers_NestedBareBlockTail_InheritsExitStatus()
    {
        // Root IF branch: [ BEGIN S1 END ] — S1's after-point flows out through the
        // block tail; both the after-S1 and after-block markers are suppressed.
        var root = RootOf("IF @x = 1\nBEGIN\n    BEGIN\n        SET @x = 2;\n    END\nEND");
        var markers = BoostSubtreeMarkers.Compute(root);

        Assert.Equal(2, markers.Count);
        Assert.All(markers, m => Assert.True(m.Suppressed));
    }

    [Theory]
    [InlineData("SET @v = 1;", true)]
    [InlineData("SELECT @v = c FROM dbo.T;", true)]
    [InlineData("FETCH NEXT FROM cur INTO @v;", true)]
    [InlineData("UPDATE dbo.T SET @v = c + 1;", true)]
    [InlineData("INSERT dbo.T VALUES (1);", false)]
    [InlineData("PRINT 'x';", false)]
    [InlineData("UPDATE dbo.T SET c = 1;", false)]
    [InlineData("IF @x = 1 SET @v = 2;", true)]              // nested assignment counts (B4)
    public void Markers_WritesState_DetectsAssignmentShapes(string sql, bool expected)
    {
        // Exercised through the public surface: the statement as the sole child of a
        // boostable loop body — its marker's WritesState is AssignsVariables' verdict.
        var root = RootOf($"WHILE @q < 1\nBEGIN\n    {sql}\nEND");
        var marker = Assert.Single(BoostSubtreeMarkers.Compute(root));
        Assert.Equal(expected, marker.WritesState);
    }

    // --------------------------------------------------------- boosted batch shape

    private static Frame BuildLoopFrame(out RewriteContext ctx, out string script)
    {
        ctx = new RewriteContext("7f3a");
        var (body, fullScript) = ParseTestHelper.ParseBatch(
            "WHILE @i < 2\nBEGIN\n    SET @i = @i + 1;\n    INSERT dbo.T VALUES (1);\nEND");
        script = fullScript;
        var cursor = ExecutionCursor.Create(body, fullScript);
        var frame = new Frame(0, ModuleIdentity.Script(), cursor, SetOptionEnvironment.Default);
        var declare = (DeclareVariableStatement)ParseTestHelper.ParseSingle("DECLARE @i int;");
        frame.Variables.Register(new VariableDeclaration("@i", "int", null, declare.Declarations[0]));
        return frame;
    }

    private static ComposedBatch BuildBoosted(Frame frame, RewriteContext ctx, string script, int seq = 5)
    {
        var controlNode = frame.Cursor.Index.All[0];
        var markers = BoostSubtreeMarkers.Compute(controlNode.Fragment);
        return ComposedBatchBuilder.BuildForBoostedSubtree(
            frame, RewriteEngine.CreateDefault(), ctx, controlNode, script, ShadowValues.Initial(), seq, markers);
    }

    [Fact]
    public void BoostedBatch_PrologueLines_PrecedeTheOracle_AndCarrySeq()
    {
        var frame = BuildLoopFrame(out var ctx, out var script);
        var batch = BuildBoosted(frame, ctx, script);

        var lines = batch.Text.Split('\n');
        var create = Array.FindIndex(lines, l => l.StartsWith("IF OBJECT_ID('tempdb..#__dbg_boost') IS NULL CREATE TABLE #__dbg_boost"));
        var seed = Array.FindIndex(lines, l => l.StartsWith("IF NOT EXISTS (SELECT 1 FROM #__dbg_boost) INSERT #__dbg_boost VALUES (5, -1);"));
        var reset = Array.FindIndex(lines, l => l == "ELSE UPDATE #__dbg_boost SET seq = 5, pos = -1;");
        var tryLine = Array.FindIndex(lines, l => l == "BEGIN TRY");
        Assert.True(create >= 0 && seed == create + 1 && reset == seed + 1, "prologue must be three consecutive lines");
        Assert.True(tryLine == reset + 1, "prologue must sit immediately before the oracle TRY");
    }

    [Fact]
    public void BoostedBatch_SliceLineArithmetic_IsUnchanged_AndPostambleFollows()
    {
        var frame = BuildLoopFrame(out var ctx, out var script);
        var controlNode = frame.Cursor.Index.All[0];
        var batch = BuildBoosted(frame, ctx, script);

        var originalSliceLines = controlNode.Span.EndLine - controlNode.Span.StartLine + 1;
        var lines = batch.Text.Split('\n');
        // B is 1-based; the slice occupies exactly its original line count, and the
        // §7.1 rc/err capture follows immediately — insertions added no lines.
        Assert.Equal("WHILE @i < 2", lines[batch.B - 1]);
        Assert.StartsWith(";SELECT @__dbg7f3a_rc = @@ROWCOUNT", lines[batch.B - 1 + originalSliceLines]);
    }

    [Fact]
    public void BoostedBatch_MarkersAreInline_StateWriteBeforePos_NoTrailingSemicolon()
    {
        var frame = BuildLoopFrame(out var ctx, out var script);
        var batch = BuildBoosted(frame, ctx, script);
        var lines = batch.Text.Split('\n');

        // After the SET (assigns @i): guarded state write THEN the pos update, on the
        // SET's own line, ending without a trailing semicolon.
        var setLine = lines.Single(l => l.Contains("SET @i = @i + 1;"));
        Assert.Contains(
            ";IF XACT_STATE() <> -1 AND OBJECT_ID('tempdb..#__dbg_s0') IS NOT NULL UPDATE #__dbg_s0 SET [i]=@i" +
            ";IF XACT_STATE() <> -1 UPDATE #__dbg_boost SET pos = 0", setLine);
        Assert.EndsWith("pos = 0", setLine);

        // After the INSERT (assigns nothing): pos update only.
        var insertLine = lines.Single(l => l.Contains("INSERT dbo.T VALUES (1);"));
        Assert.EndsWith(";IF XACT_STATE() <> -1 UPDATE #__dbg_boost SET pos = 1", insertLine);
        Assert.DoesNotContain("#__dbg_s0", insertLine);
    }

    [Fact]
    public void BoostedBatch_SuppressedTailMarker_IsNotEmitted()
    {
        var ctx = new RewriteContext("7f3a");
        var (body, script) = ParseTestHelper.ParseBatch(
            "IF @x = 1\nBEGIN\n    SET @x = 2;\n    INSERT dbo.T VALUES (1);\nEND");
        var cursor = ExecutionCursor.Create(body, script);
        var frame = new Frame(0, ModuleIdentity.Script(), cursor, SetOptionEnvironment.Default);
        var declare = (DeclareVariableStatement)ParseTestHelper.ParseSingle("DECLARE @x int;");
        frame.Variables.Register(new VariableDeclaration("@x", "int", null, declare.Declarations[0]));

        var controlNode = cursor.Index.All[0];
        var markers = BoostSubtreeMarkers.Compute(controlNode.Fragment);
        var batch = ComposedBatchBuilder.BuildForBoostedSubtree(
            frame, RewriteEngine.CreateDefault(), ctx, controlNode, script, ShadowValues.Initial(), 1, markers);

        Assert.Contains("pos = 0", batch.Text);                 // after the SET (mid-list)
        Assert.DoesNotContain("pos = 1", batch.Text);           // branch tail — suppressed (A21/fact 27)
    }

    [Fact]
    public void BoostedBatch_CatchRow_CarriesScopeIdentity_PlainBatchDoesNot()
    {
        var frame = BuildLoopFrame(out var ctx, out var script);
        var boosted = BuildBoosted(frame, ctx, script);
        Assert.Contains("SCOPE_IDENTITY() AS scope_identity", GetCatchBranch(boosted.Text));

        var plain = ComposedBatchBuilder.BuildForUnit(
            frame, RewriteEngine.CreateDefault(), ctx, frame.Cursor.Index.All[1], script, ShadowValues.Initial());
        Assert.DoesNotContain("SCOPE_IDENTITY() AS scope_identity", GetCatchBranch(plain.Text));
    }

    private static string GetCatchBranch(string batchText)
    {
        var start = batchText.IndexOf("BEGIN CATCH", StringComparison.Ordinal);
        Assert.True(start >= 0);
        return batchText[start..];
    }

    [Fact]
    public void BoostedBatch_IsParameterFree()
    {
        var frame = BuildLoopFrame(out var ctx, out var script);
        Assert.Null(BuildBoosted(frame, ctx, script).Parameters);   // fact 26e transport invariant
    }

    [Fact]
    public void BoostedSubtree_WithIntrinsicReference_Throws_PlannerMustRefuse()
    {
        var ctx = new RewriteContext("7f3a");
        var (body, script) = ParseTestHelper.ParseBatch(
            "WHILE @i < 2\nBEGIN\n    SET @i = @@ROWCOUNT;\nEND");
        var cursor = ExecutionCursor.Create(body, script);
        var frame = new Frame(0, ModuleIdentity.Script(), cursor, SetOptionEnvironment.Default);
        var declare = (DeclareVariableStatement)ParseTestHelper.ParseSingle("DECLARE @i int;");
        frame.Variables.Register(new VariableDeclaration("@i", "int", null, declare.Declarations[0]));

        var controlNode = cursor.Index.All[0];
        var ex = Assert.Throws<InvalidOperationException>(() => ComposedBatchBuilder.BuildForBoostedSubtree(
            frame, RewriteEngine.CreateDefault(), ctx, controlNode, script, ShadowValues.Initial(), 1,
            BoostSubtreeMarkers.Compute(controlNode.Fragment)));
        Assert.Contains("planner", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BoostedSubtree_NonControlRoot_Throws()
    {
        var frame = BuildLoopFrame(out var ctx, out var script);
        var executable = frame.Cursor.Index.All[1];              // the SET inside the body
        Assert.Throws<ArgumentException>(() => ComposedBatchBuilder.BuildForBoostedSubtree(
            frame, RewriteEngine.CreateDefault(), ctx, executable, script, ShadowValues.Initial(), 1,
            Array.Empty<BoostMarker>()));
    }

    // ------------------------------------------------------------ composition guards

    [Theory]
    [InlineData("oraclefree")]
    [InlineData("debuggerinitiated")]
    [InlineData("rematerialize")]
    [InlineData("redoom")]
    [InlineData("doomedseed")]
    public void BoostSeq_IsMutuallyExclusive_WithEveryNonPlainKnob(string knob)
    {
        var frame = BuildLoopFrame(out var ctx, out var script);
        var composition = BatchComposition.Default with { BoostSeq = 1 };
        composition = knob switch
        {
            "oraclefree" => composition with { OracleFree = true },
            "debuggerinitiated" => composition with { DebuggerInitiated = true },
            "rematerialize" => composition with { Rematerialize = new ErrorContextValues(8134, 16, 1, 3, null, "boom") },
            "redoom" => composition with { RedoomTrancount = 1 },
            "doomedseed" => composition with { DoomedSeedValues = new object?[] { 1 }, IncludeStateWrite = false },
            _ => throw new ArgumentOutOfRangeException(nameof(knob)),
        };

        Assert.Throws<InvalidOperationException>(() => ComposedBatchBuilder.BuildForUnit(
            frame, RewriteEngine.CreateDefault(), ctx, frame.Cursor.Index.All[1], script,
            ShadowValues.Initial(), composition));
    }

    // ------------------------------------------------------------- F1 session init

    [Fact]
    public void BoostSessionInit_CreatesAndSeeds_TheOneRowTable()
    {
        var text = ComposedBatchBuilder.BuildBoostSessionInit();
        Assert.Contains("IF OBJECT_ID('tempdb..#__dbg_boost') IS NULL CREATE TABLE #__dbg_boost(seq int NOT NULL, pos int NOT NULL);", text);
        Assert.Contains("IF NOT EXISTS (SELECT 1 FROM #__dbg_boost) INSERT #__dbg_boost VALUES (0, -1);", text);
    }
}
