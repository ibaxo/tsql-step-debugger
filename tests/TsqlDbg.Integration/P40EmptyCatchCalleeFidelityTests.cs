using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using TsqlDbg.Core.Sessions;
using TsqlDbg.Core.Targets;
using Xunit;

namespace TsqlDbg.Integration;

// docs/DESIGN.md §11.5 / §10.3 (empty-CATCH-at-end-of-callee settlement, 2026-07-17). A fault routed to
// an EMPTY CATCH that is the last construct of a CALLEE body (depth >= 2) completes the callee's cursor
// via routing; the fix settles that as a COMPLETED pop (copy-back + @rc) instead of leaving the frame
// unsettled (which crashed the next step with "Cursor is completed; nothing to peek"). Native swallows
// the error and returns normally, so this is a FIDELITY case: stepping Over and Into both reproduce a
// native EXEC byte-for-byte. See tests/corpus/p40_empty_catch_callee.sql.
public sealed class P40EmptyCatchCalleeFidelityTests
{
    private const string ConnEnvVar = "TSQLDBG_TEST_CONN";

    [SkippableTheory]
    [InlineData("dbo.p40_empty_catch", StepKind.Over)]          // native path (unchanged)
    [InlineData("dbo.p40_empty_catch", StepKind.Into)]          // the bug: route completes the callee cursor -> settle
    [InlineData("dbo.p40_empty_catch_notlast", StepKind.Over)]  // guard: statement after the empty CATCH
    [InlineData("dbo.p40_empty_catch_notlast", StepKind.Into)]  // guard: continuation lands after the CATCH
    [InlineData("dbo.p40_empty_catch_cascade", StepKind.Over)]  // cascade: EXEC is the caller's last statement
    [InlineData("dbo.p40_empty_catch_cascade", StepKind.Into)]  // cascade: callee completes -> caller completes
    [InlineData("dbo.p40_empty_catch_rowcount", StepKind.Over)] // F1: @@ROWCOUNT zeroed on the empty-CATCH transit
    [InlineData("dbo.p40_empty_catch_rowcount", StepKind.Into)] // F1: caller reads RcAfter = 0, not the stale 3
    [InlineData("dbo.p40_empty_catch_rethrow", StepKind.Over)]  // X6: bare THROW into an empty outer CATCH
    [InlineData("dbo.p40_empty_catch_rethrow", StepKind.Into)]  // X6: caller @@ERROR = 0 (rethrow context reconciled)
    [InlineData("dbo.p40_outer_live_catch", StepKind.Over)]     // F2a: continuation must not corrupt a live outer CATCH
    [InlineData("dbo.p40_outer_live_catch", StepKind.Into)]     // F2a: caller's CATCH still reads its own 50000/7
    public async Task DebuggerRun_MatchesNativeRun(string procedure, StepKind stepKind)
    {
        var rawConnectionString = Environment.GetEnvironmentVariable(ConnEnvVar);
        Skip.If(
            string.IsNullOrWhiteSpace(rawConnectionString),
            $"{ConnEnvVar} is not set; skipping fidelity test (never fake a pass — CLAUDE.md).");

        var csb = new SqlConnectionStringBuilder(rawConnectionString);
        await DeployFixtureAsync(rawConnectionString!);

        var native = await RunNativeAsync(rawConnectionString!, procedure);
        var debugged = await RunThroughDebuggerAsync(csb.DataSource, csb.InitialCatalog, procedure, stepKind);

        Assert.Equal(native, debugged);
    }

    // F1 @@ERROR-zeroing guard (not a native-fidelity comparison — the callee's pre-TRY RAISERROR
    // sev-16 surfaces to ADO.NET as a SqlException on a plain native EXEC, but the debugger handles it
    // as a statement-level continuation (fact 21) and streams on). Asserts the debugger's OWN behavior:
    // the empty-CATCH transit zeroes the shadow @@ERROR (ObserveHandledCatchReturn), so the caller reads
    // ErrAfter = 0 — NOT the stale 50000 the pre-re-review fix carried across the pop.
    [SkippableFact]
    public async Task PreTryError_EmptyCatchTransit_CallerReadsErrAfterZero()
    {
        var rawConnectionString = Environment.GetEnvironmentVariable(ConnEnvVar);
        Skip.If(
            string.IsNullOrWhiteSpace(rawConnectionString),
            $"{ConnEnvVar} is not set; skipping (never fake a pass — CLAUDE.md).");

        var csb = new SqlConnectionStringBuilder(rawConnectionString);
        await DeployFixtureAsync(rawConnectionString!);

        var debugged = await RunThroughDebuggerAsync(
            csb.DataSource, csb.InitialCatalog, "dbo.p40_empty_catch_preerr", StepKind.Into);

        // The caller's `SELECT @@ERROR AS ErrAfter` result set is the only one → its single row is 0.
        Assert.Contains("(row)0|", debugged);
        Assert.DoesNotContain("(row)50000|", debugged);   // the stale pre-TRY error must NOT survive
    }

    // Doomed sub-case (not a native-fidelity comparison — the §16 safety transaction is what gets
    // doomed under the debugger, so it diverges from a standalone native run by design). Asserts the
    // debugger's OWN behavior: an empty-CATCH-at-end completion that returns a DOOMED callee settles
    // through the doomed pop branch WITHOUT crashing, and the callee's pre-fault result still streams.
    [SkippableFact]
    public async Task DoomedEmptyCatch_CalleeCompletion_SettlesWithoutCrash()
    {
        var rawConnectionString = Environment.GetEnvironmentVariable(ConnEnvVar);
        Skip.If(
            string.IsNullOrWhiteSpace(rawConnectionString),
            $"{ConnEnvVar} is not set; skipping (never fake a pass — CLAUDE.md).");

        var csb = new SqlConnectionStringBuilder(rawConnectionString);
        await DeployFixtureAsync(rawConnectionString!);

        // The pre-fix crash was "Cursor is completed; nothing to peek." on the step after routing into
        // the empty CATCH — reaching a completed run at all proves the settlement fired.
        var debugged = await RunThroughDebuggerAsync(
            csb.DataSource, csb.InitialCatalog, "dbo.p40_empty_catch_doomed", StepKind.Into);

        Assert.Contains("(row)5|", debugged);   // the callee's pre-fault SELECT @Seed streamed (@Seed = 5)
    }

    private static async Task DeployFixtureAsync(string connectionString)
    {
        var sqlPath = Path.Combine(AppContext.BaseDirectory, "corpus", "p40_empty_catch_callee.sql");
        var script = await File.ReadAllTextAsync(sqlPath);

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        foreach (var batch in Regex.Split(script, @"(?im)^\s*GO\s*$"))
        {
            if (string.IsNullOrWhiteSpace(batch)) continue;
            await using var command = connection.CreateCommand();
            command.CommandText = batch;
            await command.ExecuteNonQueryAsync();
        }
    }

    private static async Task<string> RunNativeAsync(string connectionString, string procedure)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SET NOCOUNT ON; EXEC {procedure} @Seed = 5;";
        await using var reader = await command.ExecuteReaderAsync();

        var sb = new StringBuilder();
        do
        {
            sb.Append("[rs]");
            while (await reader.ReadAsync())
            {
                sb.Append("(row)");
                for (var i = 0; i < reader.FieldCount; i++)
                    sb.Append(reader.IsDBNull(i) ? "NULL" : reader.GetValue(i).ToString()).Append('|');
            }
        } while (await reader.NextResultAsync());
        return sb.ToString();
    }

    private static async Task<string> RunThroughDebuggerAsync(
        string server, string database, string procedure, StepKind stepKind)
    {
        var target = new TargetEntry(server, "test", AllowWrites: false, Options: null);
        var options = new SessionOptions(
            server, database, LaunchMode.Procedure, procedure,
            new Dictionary<string, string> { ["@Seed"] = "5" }, ScriptText: null, Boost: false);

        var result = await SessionHost.RunAsync(options, target, stepKind: stepKind);

        var sb = new StringBuilder();
        foreach (var rs in result.Execution.ResultSets)
        {
            sb.Append("[rs]");
            foreach (var row in rs.Rows)
            {
                sb.Append("(row)");
                foreach (var cell in row)
                    sb.Append(cell is null ? "NULL" : cell.ToString()).Append('|');
            }
        }
        return sb.ToString();
    }
}
