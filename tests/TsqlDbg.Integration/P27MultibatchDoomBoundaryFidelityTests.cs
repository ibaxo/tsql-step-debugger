using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using TsqlDbg.Core.Sessions;
using TsqlDbg.Core.Targets;
using Xunit;

namespace TsqlDbg.Integration;

// DESIGN §5.4 / §10.4 (A35) corpus fixture p27_multibatch_doom_boundary — M8 lane 1b's
// R1 acceptance test (§10-gated). Proves the §8.1 GO-boundary force-rollback: a batch
// leaves its transaction DOOMED at the GO seam, the engine force-rolls it back there
// (Appendix C fact 22 at the separator; fact 32b), so the next batch reads @@TRANCOUNT = 0
// and a #temp created inside the doomed transaction is gone. The native oracle is the
// whole file run as sqlcmd-WITHOUT-`-b` (each GO batch a separate command on ONE
// connection, continuing past batch-aborting/epilogue errors — the engine itself
// force-rolls the doomed transaction at each boundary). Native == debugger.
public sealed class P27MultibatchDoomBoundaryFidelityTests
{
    private const string ConnEnvVar = "TSQLDBG_TEST_CONN";
    private const string FixtureFile = "p27_multibatch_doom_boundary.sql";

    // Batch 2's CATCH streams caught_number; batch 3 streams the boundary proof.
    private sealed record DoomBoundaryResult(int CaughtNumber, int TrancountAfter, int DoomedTempGone);

    [SkippableFact]
    public async Task DebuggerRun_MatchesNativeRun_ForDoomBoundary()
    {
        var (server, database, conn) = RequireConnection();

        var native = await RunNativeMultibatchAsync(conn);
        AssertNativeGroundTruth(native);

        var debugged = await RunThroughDebuggerAsync(server, database);
        Assert.Equal(native, debugged);
    }

    // pass 2 (Into): the fixture is EXEC-free, so Into is trajectory-identical to Over --
    // asserted (runs the full pipeline and can genuinely fail), not assumed.
    [SkippableFact]
    public async Task DebuggerRun_MatchesNativeRun_SteppedInto()
    {
        var (server, database, conn) = RequireConnection();

        var native = await RunNativeMultibatchAsync(conn);
        AssertNativeGroundTruth(native);

        var debugged = await RunThroughDebuggerAsync(server, database, StepKind.Into);
        Assert.Equal(native, debugged);
    }

    // pass 3 (continue+boost): boost refuses outright once the session is doomed (B2), and
    // there is no IF/WHILE to boost anyway, so this is byte-for-byte the pass-1 interpreted
    // walk -- the p01/p05 refusal-equivalence pattern, declared and asserted.
    [SkippableFact]
    public async Task DebuggerRun_MatchesNativeRun_ContinueBoost()
    {
        var (server, database, conn) = RequireConnection();

        var native = await RunNativeMultibatchAsync(conn);
        AssertNativeGroundTruth(native);

        var debugged = await RunThroughDebuggerAsync(server, database, boost: true);
        Assert.Equal(native, debugged);
    }

    // The p04 lesson: assert the engine's OWN ground truth explicitly, not merely
    // debugger == native (else a bug that breaks BOTH the same way would still pass).
    private static void AssertNativeGroundTruth(DoomBoundaryResult native)
    {
        Assert.Equal(8134, native.CaughtNumber);   // divide-by-zero doomed the transaction
        Assert.Equal(0, native.TrancountAfter);     // fact 22: doomed tran could not cross GO
        Assert.Equal(1, native.DoomedTempGone);     // #doomed destroyed by the forced rollback
    }

    private static (string Server, string Database, string ConnectionString) RequireConnection()
    {
        var rawConnectionString = Environment.GetEnvironmentVariable(ConnEnvVar);
        Skip.If(
            string.IsNullOrWhiteSpace(rawConnectionString),
            $"{ConnEnvVar} is not set; skipping fidelity test (never fake a pass — CLAUDE.md).");

        var csb = new SqlConnectionStringBuilder(rawConnectionString);
        return (csb.DataSource, csb.InitialCatalog, rawConnectionString!);
    }

    // §20.3 native oracle for a multi-batch script: split on `GO`, run each batch as its
    // OWN command on ONE open connection, CATCH per batch and CONTINUE (sqlcmd default, no
    // `-b`; Appendix C fact 32a). The engine force-rolls the doomed transaction at batch 2's
    // end (fact 22 → 3998, surfaced as a SqlException the catch absorbs), so batch 3 sees a
    // clean @@TRANCOUNT = 0 and a destroyed #doomed. No BEGIN/EXEC/ROLLBACK wrapper: the
    // fixture opens and dooms its own transaction, and #temp-only means nothing to clean up.
    private static async Task<DoomBoundaryResult> RunNativeMultibatchAsync(string connectionString)
    {
        var sqlPath = Path.Combine(AppContext.BaseDirectory, "corpus", FixtureFile);
        var script = await File.ReadAllTextAsync(sqlPath);
        var batches = Regex.Split(script, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        int? caughtNumber = null, trancountAfter = null, doomedTempGone = null;
        foreach (var batch in batches)
        {
            if (string.IsNullOrWhiteSpace(batch))
            {
                continue;
            }

            try
            {
                await using var command = connection.CreateCommand();
                command.CommandText = batch;
                await using var reader = await command.ExecuteReaderAsync();
                do
                {
                    while (await reader.ReadAsync())
                    {
                        for (var i = 0; i < reader.FieldCount; i++)
                        {
                            switch (reader.GetName(i))
                            {
                                case "caught_number": caughtNumber = reader.GetInt32(i); break;
                                case "trancount_after": trancountAfter = reader.GetInt32(i); break;
                                case "doomed_temp_gone": doomedTempGone = reader.GetInt32(i); break;
                            }
                        }
                    }
                }
                while (await reader.NextResultAsync());
            }
            catch (SqlException)
            {
                // sqlcmd default: a batch-aborting error / the fact-22 doomed epilogue
                // (3998) aborts THIS batch but the client continues to the next. Rows that
                // streamed before the error were already captured above.
            }
        }

        return new DoomBoundaryResult(
            caughtNumber ?? throw new InvalidOperationException("batch 2 CATCH did not stream caught_number"),
            trancountAfter ?? throw new InvalidOperationException("batch 3 did not stream trancount_after"),
            doomedTempGone ?? throw new InvalidOperationException("batch 3 did not stream doomed_temp_gone"));
    }

    // The debugger run: mode=script over the whole file, stepped (Over/Into/boost passes).
    // RunToEndAsync accumulates every batch's user result sets. Batch 2 streams an EMPTY
    // [boom] set (the debugger faithfully replays `SELECT 1/0`'s column metadata, which
    // streams just before the divide-by-zero faults — native does the same) plus the
    // CATCH's [caught_number] set; batch 3 streams the boundary proof. Extract the three
    // scalars BY COLUMN NAME so the comparison is robust to the (faithful) empty set —
    // the same by-name capture the native oracle uses.
    private static async Task<DoomBoundaryResult> RunThroughDebuggerAsync(
        string server, string database, StepKind stepKind = StepKind.Over, bool boost = false)
    {
        var sqlPath = Path.Combine(AppContext.BaseDirectory, "corpus", FixtureFile);
        var script = await File.ReadAllTextAsync(sqlPath);

        var target = new TargetEntry(server, "test", AllowWrites: false, Options: null);
        var options = new SessionOptions(
            server, database, LaunchMode.Script,
            Procedure: null, Args: null, ScriptText: script, Boost: boost);

        var result = await SessionHost.RunAsync(options, target, stepKind: stepKind);

        int? caughtNumber = null, trancountAfter = null, doomedTempGone = null;
        foreach (var rs in result.Execution.ResultSets)
        {
            if (rs.Rows.Count == 0)
            {
                continue;
            }

            for (var i = 0; i < rs.Columns.Count; i++)
            {
                switch (rs.Columns[i])
                {
                    case "caught_number": caughtNumber = Convert.ToInt32(rs.Rows[0][i]); break;
                    case "trancount_after": trancountAfter = Convert.ToInt32(rs.Rows[0][i]); break;
                    case "doomed_temp_gone": doomedTempGone = Convert.ToInt32(rs.Rows[0][i]); break;
                }
            }
        }

        return new DoomBoundaryResult(
            caughtNumber ?? throw new InvalidOperationException("debugger: batch 2 CATCH did not stream caught_number"),
            trancountAfter ?? throw new InvalidOperationException("debugger: batch 3 did not stream trancount_after"),
            doomedTempGone ?? throw new InvalidOperationException("debugger: batch 3 did not stream doomed_temp_gone"));
    }
}
