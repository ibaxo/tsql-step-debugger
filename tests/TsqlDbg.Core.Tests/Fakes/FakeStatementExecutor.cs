using TsqlDbg.Core.Execution;

namespace TsqlDbg.Core.Tests.Fakes;

// DESIGN §3: "Core is UI-free and fully unit-testable ... tests inject a fake."
public sealed class FakeStatementExecutor : IStatementExecutor
{
    private readonly Queue<Func<string, BatchResult>> _scripted = new();

    public List<string> ReceivedBatches { get; } = new();

    public FakeStatementExecutor Then(Func<string, BatchResult> respond)
    {
        _scripted.Enqueue(respond);
        return this;
    }

    public FakeStatementExecutor ThenEmpty()
    {
        return Then(_ => new BatchResult(Array.Empty<ResultSet>(), Array.Empty<string>()));
    }

    /// <summary>Parameters of each parameterized call, keyed by ReceivedBatches index (M3 §10.4).</summary>
    public Dictionary<int, IReadOnlyList<BatchParameter>> ReceivedParameters { get; } = new();

    /// <summary>Indexes (into ReceivedBatches) executed via ExecuteAbsorbingAsync (D5/A13):
    /// tests assert oracle-free EXEC batches — and only those — take the absorbing path.</summary>
    public HashSet<int> AbsorbingCalls { get; } = new();

    public Task<BatchResult> ExecuteAbsorbingAsync(string batchText, CancellationToken cancellationToken = default)
    {
        AbsorbingCalls.Add(ReceivedBatches.Count);
        return ExecuteAsync(batchText, cancellationToken);
    }

    /// <summary>Opt-in mimic of SqlClient's token semantics (fact 30): throw
    /// OperationCanceledException up front when the caller's token is already
    /// cancelled. Lets tests pin which internal reads run on the step token vs
    /// CancellationToken.None — B7's recovery read must be the latter, or a §10.5
    /// pause would silently skip recovery (m6-boosted-attention triage).</summary>
    public bool ThrowOnCancelledToken { get; set; }

    public Task<BatchResult> ExecuteAsync(string batchText, CancellationToken cancellationToken = default)
    {
        if (ThrowOnCancelledToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        ReceivedBatches.Add(batchText);
        if (_scripted.Count == 0)
        {
            return Task.FromResult(new BatchResult(Array.Empty<ResultSet>(), Array.Empty<string>()));
        }

        return Task.FromResult(_scripted.Dequeue()(batchText));
    }

    public Task<BatchResult> ExecuteAsync(string batchText, IReadOnlyList<BatchParameter> parameters, CancellationToken cancellationToken = default)
    {
        ReceivedParameters[ReceivedBatches.Count] = parameters;
        return ExecuteAsync(batchText, cancellationToken);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
