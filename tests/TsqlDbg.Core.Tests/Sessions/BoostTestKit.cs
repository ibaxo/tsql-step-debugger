using TsqlDbg.Core.Execution;
using TsqlDbg.Core.Interpreter;
using TsqlDbg.Core.Sessions;
using TsqlDbg.Core.Tests.Fakes;

namespace TsqlDbg.Core.Tests.Sessions;

// Shared fixture + scripted-response helpers for the M6 boost session tests. The fake
// executor IS the engine model here — responses are hand-scripted to the live-probed
// facts (26-29) and the response builders locate err_line by INSPECTING the composed
// batch text (batch-relative coordinates), so the tests never hardcode B.
internal static class BoostTestKit
{
    // Lines: 1 DECLARE, 2 SET @i, 3 SET @t, 4 WHILE, 5 BEGIN, 6 SET @i,
    // 7 INSERT, 8 SET @t, 9 END, 10 the post-loop @@ROWCOUNT reader (outside the
    // subtree — eligible; its R4 shadow-seed literal is the equivalence probe).
    public const string LoopScript = """
        DECLARE @i int, @t int;
        SET @i = 0;
        SET @t = 0;
        WHILE @i < 2
        BEGIN
            SET @i = @i + 1;
            INSERT dbo.T VALUES (@i);
            SET @t = @t + @i;
        END
        SET @t = @t + @@ROWCOUNT;
        """;

    public sealed record Kit(Session Session, FakeStatementExecutor Executor, RecordingTraceSink Trace);

    public sealed record Run(List<StepDisposition> Dispositions, List<int?> StopLines, List<string> Messages);

    public static SessionOptions Options(string script, bool boost)
        => new("DEVSQL01", "SalesDb", LaunchMode.Script, null, null, script, Boost: boost);

    public static void EnqueueInit(FakeStatementExecutor executor, bool boost)
    {
        executor.ThenEmpty();                                  // SET XACT_ABORT/NOCOUNT
        executor.ThenEmpty();                                  // CREATE TABLE #__dbg_s0
        executor.ThenEmpty();                                  // INSERT seed
        if (boost)
        {
            executor.ThenEmpty();                              // F1: #__dbg_boost create+seed
        }

        executor.ThenEmpty();                                  // BEGIN TRANSACTION
    }

    public static async Task<Kit> StartAsync(bool boost, Action<FakeStatementExecutor> configure, string? script = null, string nonce = "b0f1")
    {
        var executor = new FakeStatementExecutor();
        EnqueueInit(executor, boost);
        configure(executor);
        var trace = new RecordingTraceSink();
        var session = new Session(Options(script ?? LoopScript, boost), executor, trace, nonce);
        await session.InitializeAsync();
        return new Kit(session, executor, trace);
    }

    /// <summary>The reference driver loop — the same shape the adapter's continue path
    /// and the harness's boost mode use (B1): try boost at every arrival, fall back to
    /// interpreted. Stops on terminal/attention like RunToEndAsync.</summary>
    public static async Task<Run> DriveAsync(Session session)
    {
        var dispositions = new List<StepDisposition>();
        var stopLines = new List<int?>();
        var messages = new List<string>();
        var guard = 0;
        while (!session.IsCompleted)
        {
            if (++guard > 100)
            {
                throw new InvalidOperationException("Run did not converge — driver bug or unscripted response.");
            }

            var boosted = await session.TryStepBoostedAsync();
            var (_, stepMessages) = boosted ?? await session.StepAsync();
            messages.AddRange(stepMessages);
            dispositions.Add(session.LastStep.Disposition);
            stopLines.Add(session.Current?.Span.StartLine);
            if (session.LastStep.Disposition is StepDisposition.FrameFaulted or StepDisposition.EngineAttention)
            {
                break;
            }
        }

        return new Run(dispositions, stopLines, messages);
    }

    public static async Task StepToWhileAsync(Session session)
    {
        var guard = 0;
        while (session.Current is { } current && current.SubKind != SuSubKind.While)
        {
            if (++guard > 20)
            {
                throw new InvalidOperationException("Never reached a WHILE unit.");
            }

            await session.StepAsync();
        }
    }

    // ---------------------------------------------------------- response builders

    public static int BatchLineOf(string batchText, string needle)
    {
        var lines = batchText.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains(needle))
            {
                return i + 1;
            }
        }

        throw new InvalidOperationException($"'{needle}' not found in the composed batch:\n{batchText}");
    }

    public static BatchResult OkRow(
        string batchText, int? rc = null, decimal? scopeIdentity = null, int trancount = 1, int xactState = 1,
        object?[]? state = null)
    {
        var control = new ResultSet(
            new[] { "__dbg_ctl", "ok", "rc", "scope_identity", "trancount", "xact_state" },
            new IReadOnlyList<object?>[] { new object?[] { 1, true, rc, scopeIdentity, trancount, xactState } });
        return WithState(control, state);
    }

    public static BatchResult PredicateRow(bool value)
    {
        var p = new ResultSet(new[] { "p" }, new IReadOnlyList<object?>[] { new object?[] { value ? 1 : 0 } });
        var control = new ResultSet(
            new[] { "__dbg_ctl", "ok", "rc", "scope_identity", "trancount", "xact_state" },
            new IReadOnlyList<object?>[] { new object?[] { 1, true, null, null, 1, 1 } });
        return new BatchResult(new[] { p, control }, Array.Empty<string>());
    }

    /// <summary>A CATCH-branch control row whose err_line points at the batch line
    /// containing <paramref name="atLine"/>. <paramref name="scopeIdentity"/> non-null
    /// semantics: (present, value) — pass includeScopeIdentity for boosted rows (F2);
    /// interpreted CATCH rows omit the column entirely, like the real builder.</summary>
    public static BatchResult CatchRow(
        string batchText, int number, string atLine, string message = "simulated fault",
        int severity = 16, int trancount = 1, int xactState = 1,
        bool includeScopeIdentity = false, decimal? scopeIdentity = null,
        string? procedure = null, object?[]? state = null)
    {
        var columns = new List<string>
        {
            "__dbg_ctl", "ok", "err_number", "err_severity", "err_state", "err_line",
            "err_procedure", "err_message", "trancount", "xact_state",
        };
        var values = new List<object?>
        {
            1, false, number, severity, 1, BatchLineOf(batchText, atLine),
            procedure, message, trancount, xactState,
        };
        if (includeScopeIdentity)
        {
            columns.Add("scope_identity");
            values.Add(scopeIdentity);
        }

        var control = new ResultSet(columns, new IReadOnlyList<object?>[] { values });
        return WithState(control, state);
    }

    /// <summary>The B7 recovery read's response: (seq, pos) then the bare state row
    /// (SELECT * FROM #__dbg_s0 — variable values only, no marker column).</summary>
    public static BatchResult RecoveryRead(int seq, int pos, object?[] state)
    {
        var posSet = new ResultSet(new[] { "seq", "pos" }, new IReadOnlyList<object?>[] { new object?[] { seq, pos } });
        var stateSet = new ResultSet(
            state.Select((_, i) => $"c{i}").ToArray(),
            new IReadOnlyList<object?>[] { state });
        return new BatchResult(new[] { posSet, stateSet }, Array.Empty<string>());
    }

    private static BatchResult WithState(ResultSet control, object?[]? state)
    {
        var sets = new List<ResultSet> { control };
        if (state is not null)
        {
            var columns = new List<string> { "__dbg_state" };
            var values = new List<object?> { 1 };
            for (var i = 0; i < state.Length; i++)
            {
                columns.Add($"v{i}");
                values.Add(state[i]);
            }

            sets.Add(new ResultSet(columns, new IReadOnlyList<object?>[] { values }));
        }

        return new BatchResult(sets, Array.Empty<string>());
    }

    // ------------------------------------------------------ canned response queues

    /// <summary>The clean 2-iteration loop, interpreted per-SU (the fact-12/27 model:
    /// predicate rows are wrapper artifacts; the final capture is the reset 0).</summary>
    public static Action<FakeStatementExecutor> CleanLoopInterpretedResponses() => executor =>
    {
        executor.Then(b => OkRow(b, state: new object?[] { 0, null }));   // SET @i = 0
        executor.Then(b => OkRow(b, state: new object?[] { 0, 0 }));      // SET @t = 0
        executor.Then(_ => PredicateRow(true));
        executor.Then(b => OkRow(b, state: new object?[] { 1, 0 }));      // SET @i = 1
        executor.Then(b => OkRow(b, rc: 1, state: new object?[] { 1, 0 }));   // INSERT
        executor.ThenEmpty();                                             // C2: sys.triggers lookup (dbo.T; cached after)
        executor.Then(b => OkRow(b, state: new object?[] { 1, 1 }));      // SET @t = 1
        executor.Then(_ => PredicateRow(true));
        executor.Then(b => OkRow(b, state: new object?[] { 2, 1 }));      // SET @i = 2
        executor.Then(b => OkRow(b, rc: 1, state: new object?[] { 2, 1 }));   // INSERT
        executor.Then(b => OkRow(b, state: new object?[] { 2, 3 }));      // SET @t = 3
        executor.Then(_ => PredicateRow(false));                          // loop exits — shadows reset (fact 12)
        executor.Then(b => OkRow(b, state: new object?[] { 2, 3 }));      // SET @t = @t + @@ROWCOUNT (0)
    };

    /// <summary>The same script with the loop boosted: one batch for the whole node,
    /// postamble rc = 0 (fact 27's final-predicate reset — the V-invariant).</summary>
    public static Action<FakeStatementExecutor> CleanLoopBoostedResponses() => executor =>
    {
        executor.Then(b => OkRow(b, state: new object?[] { 0, null }));   // SET @i = 0
        executor.Then(b => OkRow(b, state: new object?[] { 0, 0 }));      // SET @t = 0
        executor.Then(b => OkRow(b, rc: 0, state: new object?[] { 2, 3 }));   // the boosted WHILE
        executor.Then(b => OkRow(b, state: new object?[] { 2, 3 }));      // SET @t = @t + @@ROWCOUNT (0)
    };

    /// <summary>A boosted batch faulting at the INSERT with the given transaction
    /// shape (used by the doom/detach scenarios).</summary>
    public static Action<FakeStatementExecutor> Fault(int number, string message, int xactState, string atLine) => executor =>
    {
        executor.Then(b => OkRow(b, state: new object?[] { 0, null }));   // SET @i = 0
        executor.Then(b => OkRow(b, state: new object?[] { 0, 0 }));      // SET @t = 0
        executor.Then(b => CatchRow(b, number, atLine, message, xactState: xactState,
            includeScopeIdentity: true, state: new object?[] { 1, 0 }));
    };
}
