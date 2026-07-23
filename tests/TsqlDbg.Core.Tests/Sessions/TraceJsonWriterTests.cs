using System.Text.Json;
using TsqlDbg.Core.Execution;
using TsqlDbg.Core.Sessions;

namespace TsqlDbg.Core.Tests.Sessions;

// DESIGN §24.8 (A73 review MED-2): the on-disk JSONL shape is a PUBLISHED contract external
// agents already parse — top-level fields lowercase, nested error/resultSets objects
// PascalCase (the pre-A73 MCP writer serialized C# records with no naming policy). The
// mixed casing LOOKS like a bug; these pins are what stops a well-meaning "make the casing
// consistent" cleanup from silently breaking every existing trace-file reader.
public sealed class TraceJsonWriterTests
{
    private static TraceStepRecord Step(
        IReadOnlyList<ResultSet>? sets = null, TraceErrorRecord? error = null,
        IReadOnlyDictionary<string, string>? changed = null, string? statement = "SELECT 1;",
        IReadOnlyList<TraceStackEntry>? stack = null)
        => new(
            Seq: 1, FrameOrdinal: 0, Module: null, Line: 3, StatementText: statement,
            VariablesAfter: null, VariablesChanged: changed,
            TempRowCounts: null, Output: Array.Empty<string>(),
            ResultSets: sets ?? Array.Empty<ResultSet>(), Error: error, Notes: Array.Empty<string>(),
            Stack: stack ?? new[] { new TraceStackEntry(0, "<script>", 3) });

    [Fact]
    public void Step_NestedError_SerializesPascalCase_TopLevelLowercase()
    {
        var error = new TraceErrorRecord(8134, 16, 1, 9, "dbo.uspChild", "Divide by zero error encountered.", "catch");

        var json = TraceJsonWriter.Step(Step(error: error), maxRows: 200, displayValueChars: 256);

        // Exact key strings — the contract, not the shape.
        Assert.Contains("\"Number\":8134", json);
        Assert.Contains("\"Severity\":16", json);
        Assert.Contains("\"State\":1", json);
        Assert.Contains("\"Line\":9", json);
        Assert.Contains("\"Procedure\":\"dbo.uspChild\"", json);
        Assert.Contains("\"RoutedTo\":\"catch\"", json);
        Assert.Contains("\"error\":{", json);          // the wrapper field stays lowercase
        Assert.Contains("\"seq\":1", json);
        Assert.Contains("\"statement\":", json);
    }

    [Fact]
    public void Step_ResultSets_SerializePascalCase_WithCapsApplied()
    {
        var set = new ResultSet(
            new[] { "x" },
            new IReadOnlyList<object?>[] { new object?[] { 1 }, new object?[] { 2 }, new object?[] { 3 } });

        var json = TraceJsonWriter.Step(Step(sets: new[] { set }), maxRows: 2, displayValueChars: 256);

        Assert.Contains("\"resultSets\":[{", json);    // wrapper lowercase
        Assert.Contains("\"Columns\":[\"x\"]", json);  // nested PascalCase
        Assert.Contains("\"Rows\":[[\"1\"],[\"2\"]]", json);   // capped at maxRows, cells rendered
        Assert.Contains("\"Truncated\":true", json);
    }

    [Fact]
    public void Step_NullFields_AreOmitted()
    {
        // §24.8/A70: field PRESENCE is the signal — a null statement/error/variables field
        // never appears on the line.
        var json = TraceJsonWriter.Step(Step(statement: null), maxRows: 200, displayValueChars: 256);
        var root = JsonDocument.Parse(json).RootElement;

        Assert.False(root.TryGetProperty("statement", out _));
        Assert.False(root.TryGetProperty("error", out _));
        Assert.False(root.TryGetProperty("variablesAfter", out _));
        Assert.False(root.TryGetProperty("variablesChanged", out _));
        Assert.False(root.TryGetProperty("tempRowCounts", out _));
    }

    [Fact]
    public void Step_FrameDepth_ReflectsStackSize_AndOmitsWhenNoFrame()
    {
        // A74 rider (add-only §24.8): frame.depth = stack depth (0 = root). frame.id is the
        // §24.5 MONOTONIC ordinal — identity, never depth — so depth must come from the
        // captured stack, and be omitted (A70 null rule) when no frame was live.
        var nested = TraceJsonWriter.Step(Step(stack: new[]
        {
            new TraceStackEntry(0, "<script>", 4),
            new TraceStackEntry(3, "dbo.procA", 5),
        }), maxRows: 200, displayValueChars: 256);
        Assert.Contains("\"depth\":1", nested);

        var frameless = TraceJsonWriter.Step(
            Step(stack: Array.Empty<TraceStackEntry>()), maxRows: 200, displayValueChars: 256);
        var frame = JsonDocument.Parse(frameless).RootElement.GetProperty("frame");
        Assert.False(frame.TryGetProperty("depth", out _));
    }

    [Fact]
    public void Header_OmittedSessionId_LeavesTheFieldOut()
    {
        // A73: the adapter surface has no §24.2 session id — the header omits it rather
        // than writing null (same A70 omission rule as everywhere else).
        var json = TraceJsonWriter.Header(null, "SRV", "DB", "script", "into", "changed", "launch");
        var session = JsonDocument.Parse(json).RootElement.GetProperty("session");

        Assert.False(session.TryGetProperty("sessionId", out _));
        Assert.Equal("SRV", session.GetProperty("server").GetString());
    }
}
