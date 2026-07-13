using TsqlDbg.Core.Parsing;

namespace TsqlDbg.Core.Tests.Parsing;

// DESIGN §5.4 / A43: GO N repeat count — offline coverage of the token-based blank pass
// (ScriptParser.BlankGoRepeatCounts) and the offset-mapped, repeat-aware batch resolver
// (FrameBodyResolver.ResolveScriptBatchesWithRepeat). The end-to-end stepping fidelity is
// pinned live by P28GoRepeatCountFidelityTests; these lock the parsing invariants that make
// materialize-N safe (byte-identical offsets; a `GO 5` inside a literal is never touched).
public sealed class GoRepeatCountTests
{
    private static IReadOnlyList<ScriptBatch> Resolve(string sql)
    {
        var parseText = ScriptParser.BlankGoRepeatCounts(sql, initialQuotedIdentifiers: true, compatLevel: 150, out var markers);
        var parsed = ScriptParser.Parse(parseText, initialQuotedIdentifiers: true, compatLevel: 150, out var errors);
        Assert.Empty(errors);
        return FrameBodyResolver.ResolveScriptBatchesWithRepeat(parsed, markers);
    }

    [Fact]
    public void BlankGoRepeatCounts_BlanksTheCount_PreservesLength_AndCapturesMarker()
    {
        const string sql = "SELECT 1\nGO 5\nSELECT 2\n";

        var blanked = ScriptParser.BlankGoRepeatCounts(sql, true, 150, out var markers);

        // Byte-length preserved (so every downstream StartOffset/StartLine is unchanged).
        Assert.Equal(sql.Length, blanked.Length);
        // The count digit is gone, the `GO` and its separator role remain.
        Assert.DoesNotContain("GO 5", blanked);
        Assert.Contains("GO  \n", blanked);   // "GO 5" -> "GO  " (the 5 became a space)

        var marker = Assert.Single(markers);
        Assert.Equal(5, marker.Count);
        Assert.Equal(9, marker.GoOffset);     // "SELECT 1\n" is 9 chars; `GO` starts at offset 9
    }

    [Fact]
    public void Resolve_GoN_RepeatsThatBatch_OthersOnce()
    {
        var batches = Resolve("SELECT 1\nGO 3\nSELECT 2\n");

        Assert.Equal(2, batches.Count);
        Assert.Equal(3, batches[0].Repeat);   // SELECT 1 repeated 3x
        Assert.Equal(1, batches[1].Repeat);   // SELECT 2 once
    }

    [Fact]
    public void Resolve_MultipleGoN_MapsEachCountToItsBatch_ByOffset()
    {
        // Two counts, three batches — the offset map must not mis-align them.
        var batches = Resolve("SELECT 1\nGO 3\nSELECT 2\nGO 2\nSELECT 3\n");

        Assert.Equal(3, batches.Count);
        Assert.Equal(3, batches[0].Repeat);
        Assert.Equal(2, batches[1].Repeat);
        Assert.Equal(1, batches[2].Repeat);

        // Offsets are byte-faithful after blanking: SELECT 2 is on file line 3, SELECT 3 on 5.
        Assert.Equal(3, batches[1].Statements[0].StartLine);
        Assert.Equal(5, batches[2].Statements[0].StartLine);
    }

    [Fact]
    public void Resolve_Go0_SkipsTheBatchEntirely()
    {
        var batches = Resolve("SELECT 1\nGO 0\nSELECT 2\n");

        var only = Assert.Single(batches);   // batch 1 (GO 0) is skipped, native parity
        Assert.Equal(1, only.Repeat);
        Assert.Single(only.Statements);
        Assert.Equal(3, only.Statements[0].StartLine);   // the surviving SELECT 2 keeps its file line
    }

    [Fact]
    public void Resolve_PlainGo_EveryBatchRepeatsOnce()
    {
        var batches = Resolve("SELECT 1\nGO\nSELECT 2\n");

        Assert.Equal(2, batches.Count);
        Assert.All(batches, b => Assert.Equal(1, b.Repeat));
    }

    [Fact]
    public void Resolve_WholeScriptRepeated_SingleBatchWithCount()
    {
        var batches = Resolve("SELECT 1\nGO 3\n");

        var only = Assert.Single(batches);
        Assert.Equal(3, only.Repeat);
    }

    [Fact]
    public void BlankGoRepeatCounts_GoDigitsInsideStringLiteral_AreNeverTouched()
    {
        // The critical safety invariant: `GO 5` inside a multi-line string is ONE string
        // token (never a `Go` token), so it must be left byte-for-byte alone.
        const string sql = "SELECT '\nGO 5\n' AS s\n";

        var blanked = ScriptParser.BlankGoRepeatCounts(sql, true, 150, out var markers);

        Assert.Empty(markers);
        Assert.Equal(sql, blanked);
    }

    [Theory]
    [InlineData("SELECT 1\nGO -1\nSELECT 2\n")]
    [InlineData("SELECT 1\nGO 1.5\nSELECT 2\n")]
    public void BlankGoRepeatCounts_MalformedCount_LeftUnblanked_ToBeRefusedAtParse(string sql)
    {
        // A malformed count is not an Integer token, so nothing is blanked — and ScriptDom
        // then parse-errors it (the launch refusal, matching sqlcmd's hard error).
        var blanked = ScriptParser.BlankGoRepeatCounts(sql, true, 150, out var markers);
        Assert.Empty(markers);
        Assert.Equal(sql, blanked);

        ScriptParser.Parse(blanked, true, 150, out var errors);
        Assert.NotEmpty(errors);   // 46010 — Session surfaces this as a launch failure
    }

    [Fact]
    public void BlankGoRepeatCounts_CountWithTrailingComment_ParsesCount_KeepsComment()
    {
        const string sql = "SELECT 1\nGO 2 -- twice\nSELECT 2\n";

        var blanked = ScriptParser.BlankGoRepeatCounts(sql, true, 150, out var markers);

        Assert.Contains("-- twice", blanked);
        Assert.Equal(2, Assert.Single(markers).Count);

        var batches = Resolve(sql);
        Assert.Equal(2, batches[0].Repeat);
    }

    // DESIGN §5.4 / §20.3 (A47): the GO splitter for the launch-failure ORACLE path
    // (ScriptParser.SplitOnGoSeparators). It tokenizes (which succeeds even when the parser
    // would fail), so it must find GO batch boundaries and their 1-based start lines for the
    // per-batch SET PARSEONLY ON probe, mapping a server line back as StartLine + line - 1.
    private static IReadOnlyList<OracleBatchSegment> Split(string sql)
        => ScriptParser.SplitOnGoSeparators(sql, initialQuotedIdentifiers: true, compatLevel: 150);

    [Fact]
    public void SplitOnGoSeparators_NoGo_IsOneSegment_WholeScript_StartLine1()
    {
        // The reported case: SET then CREATE OR ALTER PROC, no GO anywhere — one batch, so
        // the server sees the whole thing and reports 111 (CREATE-PROC-not-first).
        const string sql = "SET ANSI_NULLS ON\nCREATE OR ALTER PROCEDURE dbo.x AS\nBEGIN\n    SELECT 1;\nEND\n";

        var segment = Assert.Single(Split(sql));

        Assert.Equal(1, segment.StartLine);
        Assert.Contains("CREATE OR ALTER PROCEDURE", segment.Text);
        Assert.Contains("SET ANSI_NULLS ON", segment.Text);
    }

    [Fact]
    public void SplitOnGoSeparators_TwoBatches_SplitsWithCorrectStartLines()
    {
        var segments = Split("SELECT 1\nGO\nSELECT 2\n");

        Assert.Equal(2, segments.Count);
        Assert.Equal(1, segments[0].StartLine);
        Assert.Contains("SELECT 1", segments[0].Text);
        Assert.Equal(3, segments[1].StartLine);   // maps a server line 1 in batch 2 -> script line 3
        Assert.Contains("SELECT 2", segments[1].Text);
    }

    [Fact]
    public void SplitOnGoSeparators_LeadingAndTrailingGo_DropsEmptySegments_MapsSurvivor()
    {
        // A leading/trailing GO (and the whitespace tail) leave nothing to parse — only the
        // real batch survives, and its StartLine still maps the server's line back correctly.
        var segment = Assert.Single(Split("GO\nSELECT 1\nGO\n"));

        Assert.Equal(2, segment.StartLine);
        Assert.Contains("SELECT 1", segment.Text);
    }

    [Fact]
    public void SplitOnGoSeparators_GoInsideStringLiteral_IsNotASeparator()
    {
        // Same safety invariant as the blank pass: a `GO` inside a string is one string
        // token, never a `Go` token, so it must not split the batch.
        var segment = Assert.Single(Split("SELECT '\nGO\n' AS s\n"));

        Assert.Equal(1, segment.StartLine);
    }
}
