using System.Text.Json;
using TsqlDbg.Core.Execution;
using TsqlDbg.Core.Sessions;
using TsqlDbg.Core.Targets;
using TsqlDbg.Mcp.Tests.Fakes;

namespace TsqlDbg.Mcp.Tests;

// DESIGN §24.11: drive McpDebugSession's step/continue loop and teardown against a fake
// IStatementExecutor (no connection) — the coverage the M11 gate flagged as missing. These
// pin the load-bearing driver behavior: breakpoint stop, stop-before-COMMIT, single-step
// advance, unhandled-fault stop, and the commit gate (§24.1(2)) through the real teardown path.
public sealed class DriverTests
{
    private static async Task<(McpDebugSession Mcp, DriverFakeExecutor Fake, Session Session)> BuildAsync(
        string script, DriverFakeExecutor? fake = null, bool allowWrites = false, string commitMode = "rollback")
    {
        fake ??= new DriverFakeExecutor();
        var mode = commitMode == "commit" ? CommitMode.Commit : CommitMode.Rollback;
        var options = new SessionOptions("SRV", "DB", LaunchMode.Script, null, null, script, CommitMode: mode);
        var session = new Session(options, fake);
        await session.InitializeAsync();

        var target = new TargetEntry("SRV", "dev", allowWrites, null);
        var args = new SessionArgs("SRV", "DB", Script: script, CommitMode: commitMode);
        Func<Func<Task<bool>>?, ValueTask> teardown = async d => await session.TeardownAsync(d);
        var mcp = new McpDebugSession("s1", session, teardown, target, args);
        return (mcp, fake, session);
    }

    [Fact]
    public async Task Continue_StopsAtBreakpoint()
    {
        var (mcp, _, _) = await BuildAsync("SELECT 1 AS a;\nSELECT 2 AS b;");
        await mcp.GetEntryStateAsync();
        await mcp.SetBreakpointsAsync(new BreakpointLocation("script"), new[] { new BreakpointRequest(2) }, default);

        var stop = await mcp.ContinueAsync(default);

        Assert.Equal("breakpoint", stop.StopReason);
        Assert.Equal(2, stop.Frames[0].Line);
    }

    [Fact]
    public async Task Continue_StopsOnceBeforeCommit()
    {
        var (mcp, _, _) = await BuildAsync("SELECT 1 AS a;\nCOMMIT;");
        await mcp.GetEntryStateAsync();

        var stop = await mcp.ContinueAsync(default);

        // §10.4: continue stops once before a COMMIT with a console explanation (reason:breakpoint).
        Assert.Equal("breakpoint", stop.StopReason);
        Assert.Contains(stop.Output, o => o.Contains("COMMIT"));
    }

    [Fact]
    public async Task StepOver_AdvancesToNextStatement()
    {
        var (mcp, _, _) = await BuildAsync("SELECT 1 AS a;\nSELECT 2 AS b;");
        var entry = await mcp.GetEntryStateAsync();
        Assert.Equal(1, entry.Frames[0].Line);

        var stop = await mcp.StepAsync("over", default);

        Assert.Equal("step", stop.StopReason);
        Assert.Equal(2, stop.Frames[0].Line);
    }

    [Fact]
    public async Task Continue_UnhandledFault_StopsWithException()
    {
        var fake = new DriverFakeExecutor
        {
            // Fault the first user statement (SELECT 1/0) — no armed TRY, so §10.3 continues
            // natively (UnhandledContinued); the default 'unhandled' filter stops on it.
            Override = (batch, controlIndex) =>
                batch.Contains("__dbg_ctl") && controlIndex == 0
                    ? DriverFakeExecutor.FaultedControlRow(8134, "Divide by zero error encountered.")
                    : null,
        };
        var (mcp, _, _) = await BuildAsync("SELECT 1 / 0;\nSELECT 2 AS b;", fake);
        await mcp.GetEntryStateAsync();

        var stop = await mcp.ContinueAsync(default);

        Assert.Equal("exception", stop.StopReason);
        Assert.NotNull(stop.Error);
        Assert.Equal(8134, stop.Error!.Number);
    }

    // The teardown batch Core actually sends (Session.TeardownAsync) — asserting on THIS, not just
    // summary.Committed, pins the EndAsync→teardown decision plumbing (M11 re-review N3): a
    // regression that always passed a commit decision would flip the real batch while Committed
    // (computed independently from the gate) still looked right.
    private const string CommitBatch = "IF @@TRANCOUNT > 0 COMMIT;";
    private const string RollbackBatch = "IF @@TRANCOUNT > 0 ROLLBACK;";

    [Fact]
    public async Task Trace_CancelledMidRun_RollsBack_EvenWhenCommitAuthorized()
    {
        // M11 re-review (N2): an involuntary exit (cancel/fault) never commits partial work, even
        // under commitMode:"commit" + allowWrites — only a trace that ran to completion may commit.
        var fake = new DriverFakeExecutor
        {
            Override = (batch, controlIndex) => batch.Contains("__dbg_ctl") && controlIndex == 1
                ? throw new OperationCanceledException()
                : (BatchResult?)null,
        };
        var (mcp, _, _) = await BuildAsync(
            "SELECT 1 AS a;\nSELECT 2 AS b;\nSELECT 3 AS c;", fake, allowWrites: true, commitMode: "commit");
        var dir = Path.Combine(Path.GetTempPath(), "tsqldbg-mcp-test-traces");

        var (summary, _, _) = await mcp.RunTraceAsync(dir, "over", captureTempRowCounts: false, variableCapture: null, default);

        Assert.NotEqual("completed", summary.FinalState);
        Assert.False(summary.Committed);
        Assert.Contains(RollbackBatch, fake.ReceivedBatches);
        Assert.DoesNotContain(CommitBatch, fake.ReceivedBatches);
    }

    // ---- A70: trace variable capture (changed vs full), message dedup, outputParams ----------

    private static BatchResult StateRow(string column, object? value) => new(
        new[] { new ResultSet(new[] { column }, new IReadOnlyList<object?>[] { new object?[] { value } }) },
        Array.Empty<string>());

    private static JsonElement[] TraceLines(string path) =>
        File.ReadLines(path).Select(l => JsonDocument.Parse(l).RootElement).ToArray();

    private static JsonElement[] StepLines(JsonElement[] lines) =>
        lines.Where(e => e.GetProperty("kind").GetString() == "step").ToArray();

    private static string TraceDir() =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "tsqldbg-mcp-test-traces", Guid.NewGuid().ToString("N"))).FullName;

    [Fact]
    public async Task Trace_ChangedMode_WritesBaseline_ThenDelta_ThenEmpty_AndOmitsNullFields()
    {
        // §24.8/A70: three statements; @a is 7 after the DECLARE, 8 after the SET, 8 after the
        // SELECT — so the steps must record {baseline 7}, {delta 8}, {} (readable, unchanged).
        var snapshotReads = 0;
        var fake = new DriverFakeExecutor
        {
            Override = (batch, _) => batch.StartsWith("SELECT * FROM #__dbg_s0")
                ? StateRow("a", ++snapshotReads == 1 ? 7 : 8)
                : null,
        };
        var (mcp, _, _) = await BuildAsync("DECLARE @a int = 7;\nSET @a = 8;\nSELECT 3 AS x;", fake);

        var (_, filePath, steps) = await mcp.RunTraceAsync(TraceDir(), "over", captureTempRowCounts: false, variableCapture: "changed", default);

        Assert.Equal(3, steps);
        var lines = TraceLines(filePath);
        Assert.Equal("changed", lines[0].GetProperty("variableCapture").GetString());

        var stepLines = StepLines(lines);
        Assert.Equal(3, stepLines.Length);
        // Review MED-1: the frame ordinal is on every step line — integrating deltas is only
        // well-defined keyed by frame identity ({module, line} is reused by recursion).
        Assert.All(stepLines, s => Assert.Equal(0, s.GetProperty("frame").GetProperty("id").GetInt32()));
        Assert.Equal("7", stepLines[0].GetProperty("variablesChanged").GetProperty("@a").GetString());
        Assert.Equal("8", stepLines[1].GetProperty("variablesChanged").GetProperty("@a").GetString());
        Assert.Empty(stepLines[2].GetProperty("variablesChanged").EnumerateObject());   // stopped, readable, unchanged

        foreach (var step in stepLines)
        {
            Assert.False(step.TryGetProperty("variablesAfter", out _));   // changed mode carries the delta only
            Assert.False(step.TryGetProperty("error", out _));            // A70: null fields are omitted
        }
    }

    [Fact]
    public async Task Trace_FullMode_WritesCompleteSnapshotEveryStep()
    {
        var fake = new DriverFakeExecutor
        {
            Override = (batch, _) => batch.StartsWith("SELECT * FROM #__dbg_s0") ? StateRow("a", 7) : null,
        };
        var (mcp, _, _) = await BuildAsync("DECLARE @a int = 7;\nSELECT 3 AS x;", fake);

        // "Full" (mixed case) also pins review LOW-4: the value is case-insensitive, like stepMode.
        var (_, filePath, _) = await mcp.RunTraceAsync(TraceDir(), "over", captureTempRowCounts: false, variableCapture: "Full", default);

        var lines = TraceLines(filePath);
        Assert.Equal("full", lines[0].GetProperty("variableCapture").GetString());
        var stepLines = StepLines(lines);
        Assert.All(stepLines, s =>
        {
            Assert.Equal("7", s.GetProperty("variablesAfter").GetProperty("@a").GetString());
            Assert.False(s.TryGetProperty("variablesChanged", out _));
        });
    }

    [Fact]
    public async Task Trace_InvalidVariableCapture_Throws()
    {
        var (mcp, _, _) = await BuildAsync("SELECT 1 AS a;");
        await Assert.ThrowsAsync<ArgumentException>(
            () => mcp.RunTraceAsync(TraceDir(), "over", captureTempRowCounts: false, variableCapture: "delta", default));
    }

    [Fact]
    public void DedupeMessages_CollapsesRepeats_PreservingFirstOccurrenceOrder()
    {
        var deduped = McpDebugSession.DedupeMessages(new List<string> { "A", "B", "A", "A", "C", "B" });
        Assert.Equal(new[] { "A (occurred 3×)", "B (occurred 2×)", "C" }, deduped);
    }

    [Fact]
    public async Task Trace_ProcedureMode_OutputParamOmitted_SeedsNull_WarnsInSummary_AndReportsFinalValue()
    {
        // A70 end-to-end on the MCP surface: an omitted no-default OUTPUT param launches (NULL
        // seed, no error), the launch warning LEADS the summary messages (Mode A has no entry
        // stop), and the summary's outputParams carries the param's FINAL value.
        const string procDef = "CREATE PROCEDURE dbo.uspOut @o int OUTPUT AS BEGIN SET @o = 5; END";
        var fake = new DriverFakeExecutor
        {
            Override = (batch, _) =>
                batch.Contains("OBJECT_DEFINITION")
                    ? new BatchResult(
                        new[] { new ResultSet(new[] { "def", "qi", "ansi_nulls" }, new IReadOnlyList<object?>[] { new object?[] { procDef, true, true } }) },
                        Array.Empty<string>())
                    : batch.StartsWith("SELECT * FROM #__dbg_s0") ? StateRow("o", 5) : null,
        };
        var options = new SessionOptions("SRV", "DB", LaunchMode.Procedure, "dbo.uspOut", null, null);
        var session = new Session(options, fake);
        await session.InitializeAsync();
        var target = new TargetEntry("SRV", "dev", false, null);
        var args = new SessionArgs("SRV", "DB", Mode: "procedure", Procedure: "dbo.uspOut");
        Func<Func<Task<bool>>?, ValueTask> teardown = async d => await session.TeardownAsync(d);
        var mcp = new McpDebugSession("s-out", session, teardown, target, args);

        var (summary, filePath, _) = await mcp.RunTraceAsync(TraceDir(), "over", captureTempRowCounts: false, variableCapture: null, default);

        Assert.Contains(fake.ReceivedBatches, b => b.StartsWith("INSERT INTO #__dbg_s0") && b.Contains("NULL"));
        Assert.Contains(summary.Messages, m => m.Contains("@o is an OUTPUT parameter"));
        Assert.Equal("5", summary.OutputParams["@o"]);

        var summaryLine = TraceLines(filePath).Single(e => e.GetProperty("kind").GetString() == "summary");
        Assert.Equal("5", summaryLine.GetProperty("outputParams").GetProperty("@o").GetString());
        Assert.Contains(summaryLine.GetProperty("messages").EnumerateArray(),
            m => (m.GetString() ?? string.Empty).Contains("@o is an OUTPUT parameter"));
    }

    [Fact]
    public async Task EntryStop_SurfacesLaunchWarnings()
    {
        // A70 (M11 gap): interactive sessions surface Core LaunchWarnings on the entry stop.
        const string procDef = "CREATE PROCEDURE dbo.uspOut2 @o int OUTPUT AS BEGIN SET @o = 5; END";
        var fake = new DriverFakeExecutor
        {
            Override = (batch, _) => batch.Contains("OBJECT_DEFINITION")
                ? new BatchResult(
                    new[] { new ResultSet(new[] { "def", "qi", "ansi_nulls" }, new IReadOnlyList<object?>[] { new object?[] { procDef, true, true } }) },
                    Array.Empty<string>())
                : null,
        };
        var options = new SessionOptions("SRV", "DB", LaunchMode.Procedure, "dbo.uspOut2", null, null);
        var session = new Session(options, fake);
        await session.InitializeAsync();
        Func<Func<Task<bool>>?, ValueTask> teardown = async d => await session.TeardownAsync(d);
        var mcp = new McpDebugSession(
            "s-warn", session, teardown, new TargetEntry("SRV", "dev", false, null),
            new SessionArgs("SRV", "DB", Mode: "procedure", Procedure: "dbo.uspOut2"));

        var entry = await mcp.GetEntryStateAsync();

        Assert.Contains(entry.Warnings, w => w.Contains("@o is an OUTPUT parameter"));
    }

    [Fact]
    public async Task EndAsync_DefaultRollback_DoesNotCommit()
    {
        var (mcp, fake, _) = await BuildAsync("SELECT 1 AS a;");

        var summary = await mcp.EndAsync();

        Assert.False(summary.Committed);
        Assert.Contains(RollbackBatch, fake.ReceivedBatches);
        Assert.DoesNotContain(CommitBatch, fake.ReceivedBatches);
    }

    [Fact]
    public async Task EndAsync_CommitModeWithAllowWrites_Commits()
    {
        var (mcp, fake, _) = await BuildAsync("SELECT 1 AS a;", allowWrites: true, commitMode: "commit");

        var summary = await mcp.EndAsync();

        Assert.True(summary.Committed);
        Assert.Contains(CommitBatch, fake.ReceivedBatches);
    }

    [Fact]
    public async Task EndAsync_CommitModeWithoutAllowWrites_RollsBack()
    {
        // §24.1(2): commit needs BOTH commitMode:"commit" AND target allowWrites — the gate
        // denies commit here despite commitMode, and teardown rolls back.
        var (mcp, fake, _) = await BuildAsync("SELECT 1 AS a;", allowWrites: false, commitMode: "commit");

        var summary = await mcp.EndAsync();

        Assert.False(summary.Committed);
        Assert.Contains(RollbackBatch, fake.ReceivedBatches);
        Assert.DoesNotContain(CommitBatch, fake.ReceivedBatches);
    }
}
