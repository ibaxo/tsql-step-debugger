using Microsoft.Data.SqlClient;
using TsqlDbg.Core.Execution;
using TsqlDbg.Core.Sessions;
using TsqlDbg.Core.Targets;
using TsqlDbg.Core.Tracing;
using Xunit;

namespace TsqlDbg.Integration;

// M6 §14/A21 — the boost core end-to-end against a REAL server (Fable lane item 4's
// live half; NOT fidelity pass 3, which is the Sonnet item-6 harness): a real Session
// with boost:true, driven by the reference loop (TryStepBoostedAsync ?? StepAsync),
// exercising F1 session-init seeding, planner eligibility, the composed boosted
// batch, B8 success settlement — and a REAL B6 fault re-entry: an in-slice 8134 on
// iteration 3 of 4, mapped back to its SU via err_line, statement-level continuation
// applied, and the remainder re-boosted on the next arrival.
public sealed class BoostSessionLiveTests
{
    private const string ConnEnvVar = "TSQLDBG_TEST_CONN";

    private sealed class RecordingSink : ITraceSink
    {
        public List<(string Category, string Message)> Events { get; } = new();
        public void Event(string category, string message) => Events.Add((category, message));
    }

    private static async Task<(Session Session, RecordingSink Trace, List<string> Messages)> DriveAsync(string script)
    {
        var raw = Environment.GetEnvironmentVariable(ConnEnvVar);
        Skip.If(string.IsNullOrWhiteSpace(raw),
            $"{ConnEnvVar} is not set; skipping live probe (never fake a pass — CLAUDE.md).");

        var csb = new SqlConnectionStringBuilder(raw);
        var options = new SessionOptions(csb.DataSource, csb.InitialCatalog, LaunchMode.Script, null, null, script, Boost: true);
        var target = new TargetEntry(csb.DataSource, "test", AllowWrites: false, Options: null);
        var nonce = SqlConnectionStringFactory.NewNonce();

        await using var connection = new SqlConnection(SqlConnectionStringFactory.Build(options, target, nonce));
        await connection.OpenAsync();
        var executor = new SqlStatementExecutor(connection, options.CommandTimeoutSeconds);
        var trace = new RecordingSink();
        var session = new Session(options, executor, trace, nonce);
        var messages = new List<string>();
        try
        {
            await session.InitializeAsync();
            var guard = 0;
            while (!session.IsCompleted)
            {
                if (++guard > 100)
                {
                    throw new InvalidOperationException("live boosted run did not converge");
                }

                var boosted = await session.TryStepBoostedAsync();
                var (_, stepMessages) = boosted ?? await session.StepAsync();
                messages.AddRange(stepMessages);
                if (session.LastStep.Disposition is StepDisposition.FrameFaulted or StepDisposition.EngineAttention)
                {
                    break;
                }
            }
        }
        finally
        {
            await session.TeardownAsync();
            await executor.DisposeAsync();
        }

        return (session, trace, messages);
    }

    [SkippableFact]
    public async Task CleanBoostedLoop_RunsLive_InOneBatch_WithNativeEndState()
    {
        var (session, trace, _) = await DriveAsync("""
            DECLARE @i int, @n int;
            SET @i = 0;
            CREATE TABLE #work (v int);
            WHILE @i < 4
            BEGIN
                SET @i = @i + 1;
                INSERT #work VALUES (@i);
            END
            SELECT @n = COUNT(*) FROM #work;
            """);

        Assert.True(session.IsCompleted);
        Assert.Single(trace.Events, e => e.Category == "boost.fire");        // the whole loop = ONE batch
        Assert.Contains(trace.Events, e => e.Category == "boost.complete" && e.Message.Contains("rc=0"));
        Assert.Equal(new object?[] { 4, 4 }, session.Frames[0].Snapshot);    // @i = 4, @n = 4 — all four iterations ran
    }

    [SkippableFact]
    public async Task BoostedFault_ReentersAtTheMappedSU_AndTheRemainderReboosts_Live()
    {
        // Iteration 3 faults 8134 at the body's last child (10 / (3 - @i) at @i = 3);
        // native (no TRY anywhere) continues statement-level: @x keeps 10 from
        // iteration 2, iteration 4 completes with @x = -10, @i = 4 (facts 18/21).
        var (session, trace, messages) = await DriveAsync("""
            DECLARE @i int, @x int;
            SET @i = 0;
            WHILE @i < 4
            BEGIN
                SET @i = @i + 1;
                SET @x = 10 / (3 - @i);
            END
            """);

        Assert.True(session.IsCompleted);
        Assert.Equal(new object?[] { 4, -10 }, session.Frames[0].Snapshot);

        // The B6 story in the trace: first boosted batch faulted at the mapped SU,
        // re-entered interpreted, and the REMAINDER re-qualified (B1) — two fires.
        Assert.Equal(2, trace.Events.Count(e => e.Category == "boost.fire"));
        var fault = Assert.Single(trace.Events, e => e.Category == "boost.fault");
        Assert.Contains("err=8134", fault.Message);
        Assert.Contains(trace.Events, e => e.Category == "boost.reenter");
        Assert.Contains(trace.Events, e => e.Category == "boost.complete");
        Assert.Contains(messages, m => m.Contains("Msg 8134"));             // the native error text surfaced once
    }
}
