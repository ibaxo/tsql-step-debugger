using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using TsqlDbg.Core.Sessions;
using TsqlDbg.Core.Targets;
using Xunit;

namespace TsqlDbg.Integration;

// DESIGN §5.4 / §20.4 corpus fixture p26_multibatch_go_script — the M8 HEALTHY-PATH
// acceptance fixture (design note §12): the feature is BUILT, so this proves, in one
// fidelity assertion, that (1) local-variable scope RESETS per GO (batch 2 re-DECLAREs
// @acc at a different type), (2) #temp PERSISTS across GO (#acc survives every boundary),
// and (3) a compile-class failed batch (batch 3's undeclared @nope, engine 137) ABORTS
// but the client CONTINUES to batch 4 (sqlcmd/SSMS default, fact 32a) — unlike p27
// (a doom-class RUNTIME fault), this is a non-dooming, non-doom-boundary scenario, so it
// does NOT hit the F1 C23-boundary clarification (docs/archive/reviews/m8-s10-line-review-opus.md).
public sealed class P26MultibatchGoScriptFidelityTests
{
    private const string ConnEnvVar = "TSQLDBG_TEST_CONN";
    private const string FixtureFile = "p26_multibatch_go_script.sql";

    private sealed record AccRow(string Label, int N);

    [SkippableFact]
    public async Task DebuggerRun_MatchesNativeRun_ForMultibatchGoScript()
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

    // pass 3 (continue+boost): there is no IF/WHILE anywhere in the fixture, so boost
    // refuses outright (nothing to boost) -- this is byte-for-byte the pass-1 interpreted
    // walk, the p01-linear refusal-equivalence pattern (§6.2 non-hollow), declared here.
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
    private static void AssertNativeGroundTruth(IReadOnlyList<AccRow> native)
    {
        Assert.Equal(new[] { new AccRow("b1", 100), new AccRow("two", 2) }, native);
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

    // §20.3 native oracle for a multi-batch script (A37): split on `GO`, run each batch as
    // its OWN command on ONE open connection, CATCH per batch and CONTINUE (sqlcmd default,
    // no `-b`; Appendix C fact 32a). Batch 3's undeclared @nope aborts THAT batch only
    // (native 137, a SqlException the catch absorbs); #acc is untouched and batch 4 streams
    // the final projection. No BEGIN/EXEC/ROLLBACK wrapper: #temp-only, nothing to clean up.
    private static async Task<IReadOnlyList<AccRow>> RunNativeMultibatchAsync(string connectionString)
    {
        var sqlPath = Path.Combine(AppContext.BaseDirectory, "corpus", FixtureFile);
        var script = await File.ReadAllTextAsync(sqlPath);
        var batches = Regex.Split(script, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        var rows = new List<AccRow>();
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
                    var labelIdx = -1;
                    var nIdx = -1;
                    for (var i = 0; i < reader.FieldCount; i++)
                    {
                        if (string.Equals(reader.GetName(i), "label", StringComparison.OrdinalIgnoreCase))
                        {
                            labelIdx = i;
                        }

                        if (string.Equals(reader.GetName(i), "n", StringComparison.OrdinalIgnoreCase))
                        {
                            nIdx = i;
                        }
                    }

                    if (labelIdx >= 0 && nIdx >= 0)
                    {
                        while (await reader.ReadAsync())
                        {
                            rows.Add(new AccRow(reader.GetString(labelIdx), reader.GetInt32(nIdx)));
                        }
                    }
                }
                while (await reader.NextResultAsync());
            }
            catch (SqlException)
            {
                // sqlcmd default: batch 3's undeclared @nope (137) aborts THIS batch but
                // the client continues to the next. Rows already captured are kept.
            }
        }

        return rows;
    }

    // The debugger run: mode=script over the whole file, stepped (Over/Into/boost passes).
    // RunToEndAsync accumulates every batch's user result sets; extract rows from whichever
    // set carries "label"/"n" columns (only batch 4's SELECT does) so the comparison is
    // robust regardless of how many (empty) sets precede it.
    private static async Task<IReadOnlyList<AccRow>> RunThroughDebuggerAsync(
        string server, string database, StepKind stepKind = StepKind.Over, bool boost = false)
    {
        var sqlPath = Path.Combine(AppContext.BaseDirectory, "corpus", FixtureFile);
        var script = await File.ReadAllTextAsync(sqlPath);

        var target = new TargetEntry(server, "test", AllowWrites: false, Options: null);
        var options = new SessionOptions(
            server, database, LaunchMode.Script,
            Procedure: null, Args: null, ScriptText: script, Boost: boost);

        var result = await SessionHost.RunAsync(options, target, stepKind: stepKind);

        var rows = new List<AccRow>();
        foreach (var rs in result.Execution.ResultSets)
        {
            var labelIdx = -1;
            var nIdx = -1;
            for (var i = 0; i < rs.Columns.Count; i++)
            {
                switch (rs.Columns[i])
                {
                    case "label": labelIdx = i; break;
                    case "n": nIdx = i; break;
                }
            }

            if (labelIdx < 0 || nIdx < 0)
            {
                continue;
            }

            foreach (var row in rs.Rows)
            {
                rows.Add(new AccRow((string)row[labelIdx]!, Convert.ToInt32(row[nIdx])));
            }
        }

        return rows;
    }
}
