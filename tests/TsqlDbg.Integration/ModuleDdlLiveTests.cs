using Microsoft.Data.SqlClient;
using TsqlDbg.Core.Execution;
using TsqlDbg.Core.Interpreter;
using TsqlDbg.Core.Sessions;
using TsqlDbg.Core.Targets;
using TsqlDbg.Core.Tracing;
using Xunit;

namespace TsqlDbg.Integration;

// DESIGN §5.4 (A48): module-creating DDL (CREATE/ALTER PROCEDURE/FUNCTION/VIEW/TRIGGER) in a
// script must be executed WHOLE by the server, as its own bare batch — it must be the first
// statement of its batch and is illegal inside the §7.1 oracle TRY (a wrapped `CREATE OR ALTER`
// even parse-errors, msg 156 near 'OR'). Before A48 the debugger wrapped every executable leaf
// in BEGIN TRY, so a legitimately GO-separated `CREATE OR ALTER PROCEDURE` (valid in SSMS)
// failed at EXECUTION with 156 — Ivan's report. This is only observable LIVE (the composed
// batch is sent to a real server); the fidelity harness drives RunToEndAsync but the failure is
// in the per-statement composition. Proven here by creating a proc through the debugger and
// then CALLING it (its result set is the receipt that the CREATE ran whole).
public sealed class ModuleDdlLiveTests
{
    private const string ConnEnvVar = "TSQLDBG_TEST_CONN";

    private sealed class NullSink : ITraceSink
    {
        public void Event(string category, string message) { }
    }

    private static async Task<(bool Completed, bool Broken, List<ResultSet> Sets, List<string> Messages, StepDisposition Last)>
        DriveScriptAsync(string script)
    {
        var raw = Environment.GetEnvironmentVariable(ConnEnvVar);
        Skip.If(string.IsNullOrWhiteSpace(raw),
            $"{ConnEnvVar} is not set; skipping live probe (never fake a pass — CLAUDE.md).");

        var csb = new SqlConnectionStringBuilder(raw);
        var options = new SessionOptions(csb.DataSource, csb.InitialCatalog, LaunchMode.Script, null, null, script);
        var target = new TargetEntry(csb.DataSource, "test", AllowWrites: false, Options: null);
        var nonce = SqlConnectionStringFactory.NewNonce();

        await using var connection = new SqlConnection(SqlConnectionStringFactory.Build(options, target, nonce));
        await connection.OpenAsync();
        var executor = new SqlStatementExecutor(connection, options.CommandTimeoutSeconds);
        var session = new Session(options, executor, new NullSink(), nonce);
        var sets = new List<ResultSet>();
        var messages = new List<string>();
        try
        {
            await session.InitializeAsync();
            var guard = 0;
            while (!session.IsCompleted && !session.IsBroken)
            {
                if (++guard > 200)
                {
                    throw new InvalidOperationException("module-DDL live run did not converge");
                }

                var (stepSets, stepMessages) = await session.StepAsync();
                sets.AddRange(stepSets);
                messages.AddRange(stepMessages);
            }
        }
        finally
        {
            await session.TeardownAsync();     // rolls the CREATE back — the test is self-cleaning
            await executor.DisposeAsync();
        }

        return (session.IsCompleted, session.IsBroken, sets, messages, session.LastStep.Disposition);
    }

    private static bool AnySetHas(List<ResultSet> sets, object value)
        => sets.Any(s => s.Rows.Any(r => r.Any(c => c is not null && c.Equals(value))));

    [SkippableFact]
    public async Task CreateOrAlterProcedure_RunsWhole_AndTheProcIsCallable()
    {
        // The reported shape: a GO-separated CREATE OR ALTER PROCEDURE, then a batch that calls
        // it. Before A48 the CREATE failed with "156 near 'OR'"; now it runs whole and the EXEC
        // returns the proc's own result set (42) — the receipt that it was really created.
        var (completed, broken, sets, messages, _) = await DriveScriptAsync(
            "CREATE OR ALTER PROCEDURE dbo.p_a48_ok AS\n" +
            "BEGIN\n" +
            "    SELECT 42 AS answer;\n" +
            "END\n" +
            "GO\n" +
            "EXEC dbo.p_a48_ok;\n");

        Assert.True(completed);
        Assert.False(broken);
        Assert.DoesNotContain(messages, m => m.Contains("Incorrect syntax near"));
        Assert.DoesNotContain(messages, m => m.Contains("'OR'"));
        Assert.True(AnySetHas(sets, 42), "the created proc must be callable and return 42");
    }

    [SkippableFact]
    public async Task Reported_SetHeavyMultiBatch_CreateOrAlterInLaterBatch_Works()
    {
        // Ivan's actual scenario: SET options as their own GO batches, then CREATE OR ALTER
        // PROCEDURE as a later batch ("batch 4 of 8"), then EXEC it. The later-batch CREATE was
        // where "156 near 'OR'" fired.
        var (completed, broken, sets, messages, _) = await DriveScriptAsync(
            "SET NOCOUNT ON;\n" +
            "GO\n" +
            "SET QUOTED_IDENTIFIER ON;\n" +
            "GO\n" +
            "SET ANSI_NULLS ON;\n" +
            "GO\n" +
            "CREATE OR ALTER PROCEDURE dbo.p_a48_iso AS\n" +
            "BEGIN\n" +
            "    SELECT 7 AS v;\n" +
            "END\n" +
            "GO\n" +
            "EXEC dbo.p_a48_iso;\n" +
            "GO\n");

        Assert.True(completed);
        Assert.False(broken);
        Assert.DoesNotContain(messages, m => m.Contains("'OR'"));
        Assert.True(AnySetHas(sets, 7), "the created proc must be callable and return 7");
    }

    [SkippableFact]
    public async Task CreateOrAlterView_RunsWhole_AndIsQueryable()
    {
        // Same family, different keyword: a wrapped CREATE OR ALTER VIEW also parse-errored 156
        // near 'OR' before A48.
        var (completed, broken, sets, _, _) = await DriveScriptAsync(
            "CREATE OR ALTER VIEW dbo.v_a48 AS SELECT 5 AS a\n" +
            "GO\n" +
            "SELECT a FROM dbo.v_a48;\n");

        Assert.True(completed);
        Assert.False(broken);
        Assert.True(AnySetHas(sets, 5), "the created view must be queryable and return 5");
    }

    [SkippableFact]
    public async Task ModuleDdl_FaultAtCreateTime_SurfacesNativeError_NotTheTryArtifact()
    {
        // A genuine CREATE-time fault (2714 duplicate — the body parses, so it is not an A47
        // launch refusal): it must surface the NATIVE error, never the pre-A48 "156 near 'OR'".
        var (_, _, _, messages, _) = await DriveScriptAsync(
            "CREATE OR ALTER PROCEDURE dbo.p_a48_dup AS SELECT 1\n" +
            "GO\n" +
            "CREATE PROCEDURE dbo.p_a48_dup AS SELECT 2\n");   // collides with the batch-1 creation

        Assert.Contains(messages, m => m.Contains("2714") || m.Contains("already an object named"));
        Assert.DoesNotContain(messages, m => m.Contains("'OR'"));
    }
}
