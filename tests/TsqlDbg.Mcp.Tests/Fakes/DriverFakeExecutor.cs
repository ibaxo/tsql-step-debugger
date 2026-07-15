using TsqlDbg.Core.Execution;

namespace TsqlDbg.Mcp.Tests.Fakes;

// A count-independent fake for driving McpDebugSession's step/continue loop without a real
// connection (§24.11). Rather than script an exact queue of init/step/teardown batches (brittle),
// it decides its reply from the SENT batch text: a composed user-statement batch carries the
// "__dbg_ctl" control-row SELECT, so it gets a healthy control row back; the @@OPTIONS init probe
// gets an empty probe; every other batch (SET / CREATE state table / INSERT seed / BEGIN TRAN /
// ROLLBACK / COMMIT) gets an empty result. A per-test Override hook can fault a specific statement.
public sealed class DriverFakeExecutor : IStatementExecutor
{
    public List<string> ReceivedBatches { get; } = new();

    // Optional per-test hook: return a BatchResult to override the default reply for a batch, or
    // null to fall through to the default. Receives (batchText, controlRowBatchIndex) — the index
    // counts only control-row-bearing (user-statement) batches, so a test can fault "the 2nd step".
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
            return Task.FromResult(OkControlRow());
        }

        return Task.FromResult(Empty());
    }

    public Task<BatchResult> ExecuteAsync(string batchText, IReadOnlyList<BatchParameter> parameters, CancellationToken cancellationToken = default)
        => ExecuteAsync(batchText, cancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static BatchResult Empty() => new(Array.Empty<ResultSet>(), Array.Empty<string>());

    // A healthy control row (§7.3) with no live variables (zero-variable script frame).
    public static BatchResult OkControlRow(int trancount = 1, int xactState = 0)
    {
        var columns = new List<string> { "__dbg_ctl", "ok", "rc", "scope_identity", "trancount", "xact_state" };
        var values = new List<object?> { 1, true, null, null, trancount, xactState };
        return new BatchResult(
            new[] { new ResultSet(columns, new IReadOnlyList<object?>[] { values }) }, Array.Empty<string>());
    }

    // A faulted control row (§7.3) — a statement-level fault the driver classifies per §10.3.
    public static BatchResult FaultedControlRow(int errNumber = 8134, string errMessage = "Divide by zero error encountered.", int xactState = 1)
    {
        var columns = new[] { "__dbg_ctl", "ok", "err_number", "err_severity", "err_message", "trancount", "xact_state" };
        var values = new object?[] { 1, false, errNumber, 16, errMessage, 1, xactState };
        return new BatchResult(
            new[] { new ResultSet(columns, new IReadOnlyList<object?>[] { values }) }, Array.Empty<string>());
    }
}
