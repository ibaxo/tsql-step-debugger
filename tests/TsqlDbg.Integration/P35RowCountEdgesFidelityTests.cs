using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using TsqlDbg.Core.Sessions;
using TsqlDbg.Core.Targets;
using Xunit;

namespace TsqlDbg.Integration;

// docs/DESIGN.md §20.4 corpus fixture p35_rowcount_edges (C13 Fable follow-ups F1 + F2 + F7). See
// the fixture header. Both the procedure-mode edges and the GO-boundary script live in ONE test
// class so they never deploy the shared fixture (dbo.p35_rows / dbo.p35_rowcount_edges) in
// parallel. ZERO exemptions: native == debugged AND pinned.
//   F2 — a non-literal SET ROWCOUNT @n must still limit the debuggee's statements.
//   F7 — a real WHILE subtree so the boost pass genuinely boosts under the limit.
//   F1 — SET ROWCOUNT persists across GO; the GO-boundary catalog query must not be truncated
//        (else the table type fails to resolve and the session dies — a confirmed regression).
public sealed class P35RowCountEdgesFidelityTests
{
    private const string ConnEnvVar = "TSQLDBG_TEST_CONN";

    private sealed record EdgesResult(int VarLimited, int LoopAcc);

    private sealed record GoResult(int N, int Mx);

    // A dedicated table type (dbo.p35_rows) declared AFTER the GO, so its resolution needs the
    // GO-boundary catalog query — the query the debuggee's SET ROWCOUNT would otherwise truncate.
    private const string GoScript =
        "SET ROWCOUNT 1;\n" +
        "GO\n" +
        "DECLARE @t dbo.p35_rows;\n" +
        "INSERT INTO @t (v) VALUES (42);\n" +
        "SELECT COUNT(*) AS n, ISNULL(MAX(v), -1) AS mx FROM @t;\n";

    [SkippableTheory]
    [InlineData(StepKind.Over, false)]
    [InlineData(StepKind.Into, false)]
    [InlineData(StepKind.Over, true)]      // boost — the WHILE subtree boosts under the limit
    public async Task DebuggerRun_MatchesNativeRun_ForRowCountEdges(StepKind stepKind, bool boost)
    {
        var (csb, raw) = await SetUpAsync();

        var native = await RunNativeEdgesAsync(raw);
        var debugged = await RunEdgesThroughDebuggerAsync(csb.DataSource, csb.InitialCatalog, stepKind, boost);

        Assert.Equal(native, debugged);
        Assert.Equal(2, debugged.VarLimited);   // SET ROWCOUNT @n (n=2) limited the SELECT INTO
        Assert.Equal(6, debugged.LoopAcc);       // WHILE 3x * COUNT(*)=2 (aggregate immune)
    }

    [SkippableTheory]
    [InlineData(StepKind.Over)]
    [InlineData(StepKind.Into)]
    public async Task DebuggerRun_MatchesNativeRun_AcrossGoUnderRowCount(StepKind stepKind)
    {
        var (csb, raw) = await SetUpAsync();

        var native = RunNativeGo(raw);
        var debugged = await RunGoThroughDebuggerAsync(csb.DataSource, csb.InitialCatalog, stepKind);

        Assert.Equal(native, debugged);
        Assert.Equal(1, debugged.N);      // INSERT of one row under ROWCOUNT 1 — and the type RESOLVED (no crash)
        Assert.Equal(42, debugged.Mx);
    }

    private static async Task<(SqlConnectionStringBuilder Csb, string Raw)> SetUpAsync()
    {
        var raw = Environment.GetEnvironmentVariable(ConnEnvVar);
        Skip.If(
            string.IsNullOrWhiteSpace(raw),
            $"{ConnEnvVar} is not set; skipping fidelity test (never fake a pass — CLAUDE.md).");

        await CorpusDeployer.DeployAsync(raw!, "p35_rowcount_edges.sql");
        return (new SqlConnectionStringBuilder(raw!), raw!);
    }

    private static async Task<EdgesResult> RunNativeEdgesAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var tran = (SqlTransaction)await connection.BeginTransactionAsync();

        await using var command = connection.CreateCommand();
        command.Transaction = tran;
        command.CommandText = "EXEC dbo.p35_rowcount_edges;";
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        var result = new EdgesResult(reader.GetInt32(0), reader.GetInt32(1));
        await reader.DisposeAsync();

        await tran.RollbackAsync();
        return result;
    }

    private static async Task<EdgesResult> RunEdgesThroughDebuggerAsync(
        string server, string database, StepKind stepKind, bool boost)
    {
        var target = new TargetEntry(server, "test", AllowWrites: false, Options: null);
        var options = new SessionOptions(
            server, database, LaunchMode.Procedure,
            "dbo.p35_rowcount_edges", Args: null, ScriptText: null, Boost: boost);

        var result = await SessionHost.RunAsync(options, target, stepKind: stepKind);

        var resultSet = Assert.Single(result.Execution.ResultSets);
        var row = Assert.Single(resultSet.Rows);
        return new EdgesResult((int)row[0]!, (int)row[1]!);
    }

    private static GoResult RunNativeGo(string connectionString)
    {
        using var connection = new SqlConnection(connectionString);
        connection.Open();

        var result = new GoResult(-1, -1);
        foreach (var batch in Regex.Split(GoScript, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(batch))
            {
                continue;
            }

            using var command = connection.CreateCommand();
            command.CommandText = batch;
            using var reader = command.ExecuteReader();
            if (reader.FieldCount >= 2 && reader.Read())
            {
                result = new GoResult(reader.GetInt32(0), reader.GetInt32(1));
            }
        }

        return result;
    }

    private static async Task<GoResult> RunGoThroughDebuggerAsync(string server, string database, StepKind stepKind)
    {
        var target = new TargetEntry(server, "test", AllowWrites: false, Options: null);
        var options = new SessionOptions(
            server, database, LaunchMode.Script,
            Procedure: null, Args: null, ScriptText: GoScript, Boost: false);

        var result = await SessionHost.RunAsync(options, target, stepKind: stepKind);

        foreach (var rs in result.Execution.ResultSets)
        {
            if (rs.Columns.Count >= 2 && rs.Columns[0] == "n" && rs.Rows.Count > 0)
            {
                return new GoResult((int)rs.Rows[0][0]!, (int)rs.Rows[0][1]!);
            }
        }

        throw new Xunit.Sdk.XunitException("The debugger produced no 'n'/'mx' result set — the GO-boundary catalog query was likely truncated (F1).");
    }
}
