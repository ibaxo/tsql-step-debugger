using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using TsqlDbg.Core.Sessions;
using TsqlDbg.Core.Targets;
using Xunit;

namespace TsqlDbg.Integration;

// DESIGN §5.4 / A43 corpus fixture p28_go_repeat_count — the `GO N` repeat-count acceptance
// fixture (docs/archive/reviews/go-n-repeat-count-opus.md §6). One fidelity assertion proves that a
// `GO N` batch runs N times (N result sets), that scope RESETS per iteration (a fresh DECLARE
// runs cleanly every time) while `#temp` PERSISTS across the iteration boundary (the counter
// grows), that a repeated `CREATE #temp` collides on iteration >= 2 (error 2714) and aborts
// THAT iteration while the loop continues (fact 32a per iteration), and that `GO 0` skips the
// batch entirely — all reusing the M8 boundary machinery via A43's materialize-N.
public sealed class P28GoRepeatCountFidelityTests
{
    private const string ConnEnvVar = "TSQLDBG_TEST_CONN";
    private const string FixtureFile = "p28_go_repeat_count.sql";

    private sealed record Row(string Label, int N);

    [SkippableFact]
    public async Task DebuggerRun_MatchesNativeRun_ForGoRepeatCount()
    {
        var (server, database, conn) = RequireConnection();

        var native = await RunNativeAsync(conn);
        AssertNativeGroundTruth(native);

        var debugged = await RunThroughDebuggerAsync(server, database);
        Assert.Equal(native, debugged);
    }

    // pass 2 (Into): the fixture is EXEC-free, so Into is trajectory-identical to Over --
    // asserted (runs the full pipeline, can genuinely fail), not assumed.
    [SkippableFact]
    public async Task DebuggerRun_MatchesNativeRun_SteppedInto()
    {
        var (server, database, conn) = RequireConnection();

        var native = await RunNativeAsync(conn);
        AssertNativeGroundTruth(native);

        var debugged = await RunThroughDebuggerAsync(server, database, StepKind.Into);
        Assert.Equal(native, debugged);
    }

    // pass 3 (continue+boost): no IF/WHILE anywhere, so boost refuses outright (nothing to
    // boost) -- byte-for-byte the pass-1 interpreted walk (the p01-linear refusal-equivalence
    // pattern), declared here.
    [SkippableFact]
    public async Task DebuggerRun_MatchesNativeRun_ContinueBoost()
    {
        var (server, database, conn) = RequireConnection();

        var native = await RunNativeAsync(conn);
        AssertNativeGroundTruth(native);

        var debugged = await RunThroughDebuggerAsync(server, database, boost: true);
        Assert.Equal(native, debugged);
    }

    // The p04 lesson: assert the engine's OWN ground truth explicitly (verified live via
    // sqlcmd 2026-07-12), not merely debugger == native. 'rep' 2/3/4 = GO 3 with the session
    // #temp accumulating + scope reset each iteration; no 'skipped' = GO 0; 'b4' 5/6 = a
    // second GO 2 batch continuing the same counter across the GO 0; 'final' 6 = the session
    // #temp survived every iteration and boundary.
    private static void AssertNativeGroundTruth(IReadOnlyList<Row> native)
    {
        Assert.Equal(
            new[]
            {
                new Row("rep", 2), new Row("rep", 3), new Row("rep", 4),
                new Row("b4", 5), new Row("b4", 6),
                new Row("final", 6),
            },
            native);
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

    // §20.3 native oracle for `GO N` (A43): split on `GO` CAPTURING the repeat count, run each
    // batch its count times on ONE open connection, and CATCH-and-CONTINUE per iteration
    // (sqlcmd default, no -b; fact 32a applied per iteration — verified live: a batch-aborting
    // error inside a repeated batch does NOT stop the loop). #temp-only, nothing to clean up.
    private static async Task<IReadOnlyList<Row>> RunNativeAsync(string connectionString)
    {
        var sqlPath = Path.Combine(AppContext.BaseDirectory, "corpus", FixtureFile);
        var script = await File.ReadAllTextAsync(sqlPath);

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        var rows = new List<Row>();
        foreach (var (text, count) in SplitGoBatchesWithCount(script))
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            for (var iteration = 0; iteration < count; iteration++)
            {
                try
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText = text;
                    await using var reader = await command.ExecuteReaderAsync();
                    do
                    {
                        var labelIdx = ColumnIndex(reader.GetName, reader.FieldCount, "label");
                        var nIdx = ColumnIndex(reader.GetName, reader.FieldCount, "n");
                        if (labelIdx >= 0 && nIdx >= 0)
                        {
                            while (await reader.ReadAsync())
                            {
                                rows.Add(new Row(reader.GetString(labelIdx), reader.GetInt32(nIdx)));
                            }
                        }
                    }
                    while (await reader.NextResultAsync());
                }
                catch (SqlException)
                {
                    // sqlcmd continue-on-error, per iteration: batch 4's iteration-2 collision
                    // (2714) aborts THAT iteration; rows already captured are kept; the loop and
                    // the following batches continue.
                }
            }
        }

        return rows;
    }

    // sqlcmd's `GO [count]` line rule: a line that is only `GO`, optionally a non-negative
    // integer count, and an optional trailing comment. The count belongs to the batch the GO
    // TERMINATES; a trailing batch (after the last GO / at EOF) runs once.
    private static IReadOnlyList<(string Text, int Count)> SplitGoBatchesWithCount(string script)
    {
        var goLine = new Regex(@"^\s*GO(?:\s+(\d+))?\s*(?:--.*)?$", RegexOptions.IgnoreCase);
        var result = new List<(string, int)>();
        var current = new StringBuilder();
        using var reader = new StringReader(script);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var match = goLine.Match(line);
            if (match.Success)
            {
                var count = match.Groups[1].Success ? int.Parse(match.Groups[1].Value) : 1;
                result.Add((current.ToString(), count));
                current.Clear();
            }
            else
            {
                current.AppendLine(line);
            }
        }

        if (current.ToString().Trim().Length > 0)
        {
            result.Add((current.ToString(), 1));
        }

        return result;
    }

    private static int ColumnIndex(Func<int, string> nameOf, int fieldCount, string name)
    {
        for (var i = 0; i < fieldCount; i++)
        {
            if (string.Equals(nameOf(i), name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    // The debugger run: mode=script over the whole file, stepped (Over/Into/boost passes).
    // RunAsync accumulates every batch iteration's user result sets; extract rows from
    // whichever set carries label/n columns.
    private static async Task<IReadOnlyList<Row>> RunThroughDebuggerAsync(
        string server, string database, StepKind stepKind = StepKind.Over, bool boost = false)
    {
        var sqlPath = Path.Combine(AppContext.BaseDirectory, "corpus", FixtureFile);
        var script = await File.ReadAllTextAsync(sqlPath);

        var target = new TargetEntry(server, "test", AllowWrites: false, Options: null);
        var options = new SessionOptions(
            server, database, LaunchMode.Script,
            Procedure: null, Args: null, ScriptText: script, Boost: boost);

        var result = await SessionHost.RunAsync(options, target, stepKind: stepKind);

        var rows = new List<Row>();
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
                rows.Add(new Row((string)row[labelIdx]!, Convert.ToInt32(row[nIdx])));
            }
        }

        return rows;
    }
}
