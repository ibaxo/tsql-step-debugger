using Microsoft.Data.SqlClient;
using TsqlDbg.Core.Sessions;
using TsqlDbg.Core.Targets;
using Xunit;

namespace TsqlDbg.Integration;

// DESIGN §20.4 corpus fixture p06_nested_calls_output_params + §22 M4 accept
// criterion. See the fixture's own header comment for the fact-23/C15
// completion-gated OUTPUT/@rc copy-back shapes it exercises.
public sealed class P06NestedCallsOutputParamsFidelityTests
{
    private const string ConnEnvVar = "TSQLDBG_TEST_CONN";

    private sealed record OutputResult(int Result, int Rc);

    [SkippableTheory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task DebuggerRun_MatchesNativeRun_ForNestedCallsOutputParams(int mode)
    {
        var rawConnectionString = Environment.GetEnvironmentVariable(ConnEnvVar);
        Skip.If(
            string.IsNullOrWhiteSpace(rawConnectionString),
            $"{ConnEnvVar} is not set; skipping fidelity test (never fake a pass — CLAUDE.md).");

        var csb = new SqlConnectionStringBuilder(rawConnectionString);
        var server = csb.DataSource;
        var database = csb.InitialCatalog;

        await DeployFixtureAsync(rawConnectionString!);

        var native = await RunNativeAsync(rawConnectionString!, mode);
        var debugged = await RunThroughDebuggerAsync(server, database, mode, StepKind.Into);

        Assert.Equal(native, debugged);
    }

    // D5's ACCEPTANCE TEST (M4-gate review §5-F1; the "add it then" this file's own
    // pass-2 comment demanded): the stepped-over pass. Mode 2 is the shape the §10.1
    // oracle used to get wrong — the callee's internal fault natively CONTINUES to
    // completion with OUTPUT copy-back (fact 23-H), where the oracle imposed transfer.
    // With D5, a no-armed-TRY stepped-over EXEC composes oracle-free and absorbs
    // (fact 24 Group A), so all three modes must now match native under StepKind.Over
    // with the SAME assertions as the Into pass. Also closes a slice of M7's
    // three-pass criterion.
    [SkippableTheory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task DebuggerRun_MatchesNativeRun_SteppedOver_D5(int mode)
    {
        var rawConnectionString = Environment.GetEnvironmentVariable(ConnEnvVar);
        Skip.If(
            string.IsNullOrWhiteSpace(rawConnectionString),
            $"{ConnEnvVar} is not set; skipping fidelity test (never fake a pass — CLAUDE.md).");

        var csb = new SqlConnectionStringBuilder(rawConnectionString);
        await DeployFixtureAsync(rawConnectionString!);

        var native = await RunNativeAsync(rawConnectionString!, mode);
        var debugged = await RunThroughDebuggerAsync(csb.DataSource, csb.InitialCatalog, mode, StepKind.Over);

        Assert.Equal(native, debugged);
    }

    // M6 item 6 (B9): fidelity pass 3 — continue with boost on. Assertions identical
    // to the pass-1 method (SteppedOver_D5) above.
    [SkippableTheory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task DebuggerRun_MatchesNativeRun_ContinueBoost(int mode)
    {
        var rawConnectionString = Environment.GetEnvironmentVariable(ConnEnvVar);
        Skip.If(
            string.IsNullOrWhiteSpace(rawConnectionString),
            $"{ConnEnvVar} is not set; skipping fidelity test (never fake a pass — CLAUDE.md).");

        var csb = new SqlConnectionStringBuilder(rawConnectionString);
        await DeployFixtureAsync(rawConnectionString!);

        var native = await RunNativeAsync(rawConnectionString!, mode);
        var debugged = await RunThroughDebuggerAsync(
            csb.DataSource, csb.InitialCatalog, mode, StepKind.Over, boost: true);

        Assert.Equal(native, debugged);
    }

    // CREATE PROCEDURE must be the only statement in its batch; this fixture defines
    // three procs across three GO-separated batches.
    private static async Task DeployFixtureAsync(string connectionString)
    {
        var sqlPath = Path.Combine(AppContext.BaseDirectory, "corpus", "p06_nested_calls_output_params.sql");
        var script = await File.ReadAllTextAsync(sqlPath);
        var batches = System.Text.RegularExpressions.Regex.Split(script, @"^\s*GO\s*$",
            System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        foreach (var batch in batches)
        {
            if (string.IsNullOrWhiteSpace(batch))
            {
                continue;
            }

            await using var command = connection.CreateCommand();
            command.CommandText = batch;
            await command.ExecuteNonQueryAsync();
        }
    }

    // Mode 2's callee continues past its internal fault natively (fact 21/23-H) — the
    // client must not throw on that absorbed message, same fix p04/p12 needed.
    private static async Task<OutputResult> RunNativeAsync(string connectionString, int mode)
    {
        await using var connection = new SqlConnection(connectionString);
        connection.FireInfoMessageEventOnUserErrors = true;
        connection.InfoMessage += (_, _) => { };
        await connection.OpenAsync();
        await using var tran = (SqlTransaction)await connection.BeginTransactionAsync();

        await using var command = connection.CreateCommand();
        command.Transaction = tran;
        command.CommandText = "EXEC dbo.p06_nested_calls_output_params @Mode = @Mode;";
        command.Parameters.AddWithValue("@Mode", mode);
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        var result = new OutputResult(reader.GetInt32(0), reader.GetInt32(1));
        await reader.DisposeAsync();

        await tran.RollbackAsync();
        return result;
    }

    // Pass 2 (StepKind.Into, design notes §8 item 4) exercises the debugger's own
    // frame push/pop/OUTPUT-copy-back machinery; the stepped-over pass (StepKind.Over)
    // is D5's acceptance surface — the EXECs run as single oracle-free absorbed
    // statements (§10.1/A13, fact 24 Group A) and mode 2's callee continues past its
    // internal fault natively (fact 23-H) instead of being transfer-aborted by the
    // oracle. Same native comparison for both passes; no exemptions.
    private static async Task<OutputResult> RunThroughDebuggerAsync(
        string server, string database, int mode, StepKind stepKind, bool boost = false)
    {
        var target = new TargetEntry(server, "test", AllowWrites: false, Options: null);
        var options = new SessionOptions(
            server,
            database,
            LaunchMode.Procedure,
            "dbo.p06_nested_calls_output_params",
            new Dictionary<string, string> { ["@Mode"] = mode.ToString() },
            ScriptText: null,
            Boost: boost);

        var result = await SessionHost.RunAsync(options, target, stepKind: stepKind);

        var resultSet = Assert.Single(result.Execution.ResultSets);
        var row = Assert.Single(resultSet.Rows);
        return new OutputResult((int)row[0]!, (int)row[1]!);
    }
}
