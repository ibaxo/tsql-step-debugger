using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using TsqlDbg.Core.Sessions;
using TsqlDbg.Core.Targets;
using TsqlDbg.Core.Tracing;
using Xunit;

namespace TsqlDbg.Integration;

// docs/DESIGN.md §20.4 corpus fixture p39_insert_exec_step_into + §21 C11 (A64). Stepping INTO an
// `INSERT <target> EXEC proc` captures the callee's result stream into the target byte-for-byte
// with a native EXEC — both Over (the pre-A64 native path) and Into (the new frame-push + capture).
public sealed class P39InsertExecStepIntoFidelityTests
{
    private const string ConnEnvVar = "TSQLDBG_TEST_CONN";

    [SkippableTheory]
    [InlineData("dbo.p39_insert_exec", StepKind.Over, false)]   // native single-batch capture (unchanged)
    [InlineData("dbo.p39_insert_exec", StepKind.Into, false)]   // A64: push the callee frame, redirect result sets into #t
    [InlineData("dbo.p39_cte_caller", StepKind.Into, false)]    // S1: CTE callee refused → faithful step-over
    [InlineData("dbo.p39_boost_caller", StepKind.Into, true)]   // I3: boost refused in a capture frame → interpreted capture
    public async Task DebuggerRun_MatchesNativeRun(string procedure, StepKind stepKind, bool boost)
    {
        var rawConnectionString = Environment.GetEnvironmentVariable(ConnEnvVar);
        Skip.If(
            string.IsNullOrWhiteSpace(rawConnectionString),
            $"{ConnEnvVar} is not set; skipping fidelity test (never fake a pass — CLAUDE.md).");

        var csb = new SqlConnectionStringBuilder(rawConnectionString);
        await DeployFixtureAsync(rawConnectionString!);

        var native = await RunNativeAsync(rawConnectionString!, procedure);
        var debugged = await RunThroughDebuggerAsync(csb.DataSource, csb.InitialCatalog, procedure, stepKind, boost);

        Assert.Equal(native, debugged);
    }

    private static async Task DeployFixtureAsync(string connectionString)
    {
        var sqlPath = Path.Combine(AppContext.BaseDirectory, "corpus", "p39_insert_exec_step_into.sql");
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
        string server, string database, string procedure, StepKind stepKind, bool boost)
    {
        var target = new TargetEntry(server, "test", AllowWrites: false, Options: null);
        var options = new SessionOptions(
            server, database, LaunchMode.Procedure, procedure,
            new Dictionary<string, string> { ["@Seed"] = "5" }, ScriptText: null, Boost: boost);

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
