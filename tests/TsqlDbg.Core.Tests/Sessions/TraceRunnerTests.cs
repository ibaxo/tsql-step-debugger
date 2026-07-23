using TsqlDbg.Core.Execution;
using TsqlDbg.Core.Sessions;

namespace TsqlDbg.Core.Tests.Sessions;

// DESIGN §24.3/§24.8 (A73): the Mode A drive-capture loop as a Core primitive, tested
// against a fake IStatementExecutor with no host on top — the MCP DriverTests cover the
// same loop through RunTraceAsync (file shape, N2 commit gate); these pin the Core
// contract both hosts consume: step records, the "changed" diff, cancellation, dedupe,
// and the keep-alive callback.
public sealed class TraceRunnerTests
{
    // Count-independent fake (the MCP DriverFakeExecutor pattern): replies are decided from
    // the SENT batch text — a "__dbg_ctl" composed batch gets a healthy control row, a state
    // read gets the scripted variable row, everything else (init/teardown) gets empty.
    private sealed class DispatchExecutor : IStatementExecutor
    {
        public List<string> ReceivedBatches { get; } = new();
        public Func<string, int, BatchResult?>? Override { get; set; }

        private int _controlRowSeq;

        public Task<BatchResult> ExecuteAsync(string batchText, CancellationToken cancellationToken = default)
        {
            ReceivedBatches.Add(batchText);
            var isControlRow = batchText.Contains("__dbg_ctl");
            var controlIndex = isControlRow ? _controlRowSeq++ : -1;

            var overridden = Override?.Invoke(batchText, controlIndex);
            if (overridden is not null)
            {
                return Task.FromResult(overridden);
            }

            if (isControlRow)
            {
                var columns = new List<string> { "__dbg_ctl", "ok", "rc", "scope_identity", "trancount", "xact_state" };
                var values = new List<object?> { 1, true, null, null, 1, 0 };
                return Task.FromResult(new BatchResult(
                    new[] { new ResultSet(columns, new IReadOnlyList<object?>[] { values }) }, Array.Empty<string>()));
            }

            return Task.FromResult(new BatchResult(Array.Empty<ResultSet>(), Array.Empty<string>()));
        }

        public Task<BatchResult> ExecuteAsync(string batchText, IReadOnlyList<BatchParameter> parameters, CancellationToken cancellationToken = default)
            => ExecuteAsync(batchText, cancellationToken);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static BatchResult StateRow(string column, object? value) => new(
        new[] { new ResultSet(new[] { column }, new IReadOnlyList<object?>[] { new object?[] { value } }) },
        Array.Empty<string>());

    private static async Task<Session> BuildScriptSessionAsync(string script, DispatchExecutor executor)
    {
        var session = new Session(
            new SessionOptions("SRV", "DB", LaunchMode.Script, null, null, script), executor);
        await session.InitializeAsync();
        return session;
    }

    private static async Task<(TraceRunResult Result, List<TraceStepRecord> Steps)> RunAsync(
        Session session, TraceRunOptions options, Action? keepAlive = null, CancellationToken ct = default)
    {
        var steps = new List<TraceStepRecord>();
        var result = await TraceRunner.RunAsync(
            session, options, step => { steps.Add(step); return Task.CompletedTask; }, keepAlive, ct);
        return (result, steps);
    }

    [Fact]
    public async Task RunAsync_ChangedMode_RecordsBaseline_ThenDelta_ThenEmpty()
    {
        // §24.8/A70: @a is 7 after the DECLARE, 8 after the SET, 8 after the SELECT — the
        // records must carry {baseline 7}, {delta 8}, {} (readable, nothing changed).
        var snapshotReads = 0;
        var executor = new DispatchExecutor
        {
            Override = (batch, _) => batch.StartsWith("SELECT * FROM #__dbg_s0")
                ? StateRow("a", ++snapshotReads == 1 ? 7 : 8)
                : null,
        };
        var session = await BuildScriptSessionAsync("DECLARE @a int = 7;\nSET @a = 8;\nSELECT 3 AS x;", executor);

        var (result, steps) = await RunAsync(session, new TraceRunOptions(StepKind.Over, false, FullVariableCapture: false));

        Assert.Equal(TraceFinalState.Completed, result.FinalState);
        Assert.Equal(3, result.StepCount);
        Assert.Equal(3, steps.Count);
        Assert.All(steps, s => Assert.Equal(0, s.FrameOrdinal));           // §24.5 frame identity on every record
        Assert.All(steps, s => Assert.Null(s.VariablesAfter));             // changed mode carries the delta only
        Assert.Equal("7", steps[0].VariablesChanged!["@a"]);
        Assert.Equal("8", steps[1].VariablesChanged!["@a"]);
        Assert.Empty(steps[2].VariablesChanged!);
        Assert.Equal(new[] { 1, 2, 3 }, steps.Select(s => s.Seq));
        Assert.All(steps, s => Assert.Null(s.Error));
    }

    [Fact]
    public async Task RunAsync_FullMode_RecordsCompleteSnapshotEveryStep()
    {
        var executor = new DispatchExecutor
        {
            Override = (batch, _) => batch.StartsWith("SELECT * FROM #__dbg_s0") ? StateRow("a", 7) : null,
        };
        var session = await BuildScriptSessionAsync("DECLARE @a int = 7;\nSELECT 3 AS x;", executor);

        var (result, steps) = await RunAsync(session, new TraceRunOptions(StepKind.Over, false, FullVariableCapture: true));

        Assert.Equal(TraceFinalState.Completed, result.FinalState);
        Assert.All(steps, s =>
        {
            Assert.Equal("7", s.VariablesAfter!["@a"]);
            Assert.Null(s.VariablesChanged);
        });
    }

    [Fact]
    public async Task RunAsync_CancelledMidRun_ReturnsIncomplete_WithRecordsUpToTheCancel()
    {
        // §10.5/N2: an OperationCanceledException from the in-flight statement ends the trace
        // cleanly — records up to the cancel survive, FinalState says Incomplete (the hosts'
        // never-commit-partial gate keys on this).
        var executor = new DispatchExecutor
        {
            Override = (batch, controlIndex) => batch.Contains("__dbg_ctl") && controlIndex == 1
                ? throw new OperationCanceledException()
                : null,
        };
        var session = await BuildScriptSessionAsync("SELECT 1 AS a;\nSELECT 2 AS b;\nSELECT 3 AS c;", executor);

        var (result, steps) = await RunAsync(session, new TraceRunOptions(StepKind.Over, false, false));

        Assert.Equal(TraceFinalState.Incomplete, result.FinalState);
        Assert.Equal(1, result.StepCount);
        Assert.Single(steps);
    }

    [Fact]
    public async Task RunAsync_InvokesKeepAlive_EveryIteration()
    {
        // §24.2/M11: the MCP host's idle-sweep Touch rides this hook — a long trace must
        // refresh at least once per statement or the sweep could dispose it mid-run.
        var executor = new DispatchExecutor();
        var session = await BuildScriptSessionAsync("SELECT 1 AS a;\nSELECT 2 AS b;", executor);
        var touches = 0;

        var (result, _) = await RunAsync(session, new TraceRunOptions(StepKind.Over, false, false), keepAlive: () => touches++);

        Assert.Equal(TraceFinalState.Completed, result.FinalState);
        Assert.True(touches >= result.StepCount);
    }

    [Fact]
    public async Task RunAsync_CaptureError_DoesNotAdvanceTheDiffBaseline()
    {
        // §24.8/A70 (A73 review LOW-5): a failed state read records __capture_error and must
        // NOT advance the per-frame baseline — the next successful read diffs against the last
        // GOOD value, so nothing is silently swallowed by the error step. The discriminating
        // pin: step 3's value equals step 1's, so a correctly-preserved baseline yields an
        // EMPTY delta; a wrongly-advanced one (baseline = the failure map) would re-report @a.
        var snapshotReads = 0;
        var executor = new DispatchExecutor
        {
            Override = (batch, _) =>
            {
                if (!batch.StartsWith("SELECT * FROM #__dbg_s0"))
                {
                    return null;
                }

                snapshotReads++;
                return snapshotReads == 2
                    ? throw new InvalidOperationException("simulated snapshot-read failure")
                    : StateRow("a", 7);
            },
        };
        var session = await BuildScriptSessionAsync("DECLARE @a int = 7;\nSELECT 1 AS x;\nSELECT 2 AS y;", executor);

        var (result, steps) = await RunAsync(session, new TraceRunOptions(StepKind.Over, false, false));

        Assert.Equal(TraceFinalState.Completed, result.FinalState);
        Assert.Equal("7", steps[0].VariablesChanged!["@a"]);
        Assert.True(steps[1].VariablesChanged!.ContainsKey("__capture_error"));
        Assert.Empty(steps[2].VariablesChanged!);   // diffed against the pre-error baseline
    }

    [Fact]
    public void DedupeMessages_CollapsesRepeats_PreservingFirstOccurrenceOrder()
    {
        var deduped = TraceRunner.DedupeMessages(new List<string> { "A", "B", "A", "A", "C", "B" });
        Assert.Equal(new[] { "A (occurred 3×)", "B (occurred 2×)", "C" }, deduped);
    }
}
