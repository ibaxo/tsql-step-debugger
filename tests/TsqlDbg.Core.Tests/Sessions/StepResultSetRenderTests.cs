using TsqlDbg.Core.Execution;
using TsqlDbg.Core.Sessions;
using Xunit;

namespace TsqlDbg.Core.Tests.Sessions;

// A50 (§12.3): Session.RenderResultSetsAsText is the shared result-set projection used by
// both the REPL (RenderReplOutput) and the adapter's stepped-statement Debug Console output
// (EmitStepResultSets). These offline pins nail the pure formatting contract — aligned text
// tables capped at maxConsoleRows, "N of M — refine your query", and "" when there is
// nothing to show (so the adapter emits nothing for a control-row-only / non-SELECT step).
public sealed class StepResultSetRenderTests
{
    private static ResultSet Set(IReadOnlyList<string> columns, params object?[][] rows)
        => new(columns, rows.Select(r => (IReadOnlyList<object?>)r).ToList());

    [Fact]
    public void RendersAlignedTable_WithHeaderSeparatorAndRows()
    {
        var rendered = Session.RenderResultSetsAsText(
            new[] { Set(new[] { "id", "name" }, new object?[] { 1, "ab" }, new object?[] { 20, "c" }) },
            maxConsoleRows: 200);

        // Every cell is right-padded to its column width (including the last column), so the
        // pipes line up. Widths: "id" -> 2 (max of id/1/20), "name" -> 4 (max of name/ab/c).
        var lines = rendered.Replace("\r\n", "\n").Split('\n');
        Assert.Equal("id | name", lines[0]);
        Assert.Equal("---+-----", lines[1]);
        Assert.Equal("1  | ab  ", lines[2]);
        Assert.Equal("20 | c   ", lines[3]);
    }

    [Fact]
    public void NullCell_RendersAsNULL()
    {
        var rendered = Session.RenderResultSetsAsText(
            new[] { Set(new[] { "v" }, new object?[] { null }) },
            maxConsoleRows: 200);

        Assert.Contains("NULL", rendered);
    }

    [Fact]
    public void CapsRowsAtMaxConsoleRows_AndAnnouncesTheTotal()
    {
        var rows = Enumerable.Range(0, 5).Select(i => new object?[] { i }).ToArray();

        var rendered = Session.RenderResultSetsAsText(
            new[] { Set(new[] { "n" }, rows) },
            maxConsoleRows: 2);

        // header + separator + exactly 2 data rows + the cap notice = 5 lines
        Assert.Contains("2 of 5 — refine your query.", rendered);
        Assert.Equal(5, rendered.Split('\n').Length);
    }

    [Fact]
    public void NoSets_OrColumnLessSet_RendersEmpty_SoTheCallerEmitsNothing()
    {
        Assert.Equal("", Session.RenderResultSetsAsText(System.Array.Empty<ResultSet>(), 200));
        Assert.Equal("", Session.RenderResultSetsAsText(
            new[] { Set(System.Array.Empty<string>()) }, 200));
    }

    [Fact]
    public void MultipleSets_RenderBackToBack()
    {
        var rendered = Session.RenderResultSetsAsText(
            new[]
            {
                Set(new[] { "a" }, new object?[] { 1 }),
                Set(new[] { "b" }, new object?[] { 2 }),
            },
            maxConsoleRows: 200);

        Assert.Contains("a", rendered);
        Assert.Contains("b", rendered);
        Assert.Contains("1", rendered);
        Assert.Contains("2", rendered);
    }
}
