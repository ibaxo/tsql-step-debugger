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

        var (summary, _, _) = await mcp.RunTraceAsync(dir, "over", captureTempRowCounts: false, default);

        Assert.NotEqual("completed", summary.FinalState);
        Assert.False(summary.Committed);
        Assert.Contains(RollbackBatch, fake.ReceivedBatches);
        Assert.DoesNotContain(CommitBatch, fake.ReceivedBatches);
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
