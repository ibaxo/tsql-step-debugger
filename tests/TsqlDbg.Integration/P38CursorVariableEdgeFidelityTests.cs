using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using TsqlDbg.Core.Sessions;
using TsqlDbg.Core.Targets;
using Xunit;

namespace TsqlDbg.Integration;

// docs/DESIGN.md §20.4 corpus fixture p38_cursor_variable_edges + §21 C12 (A63 Fable-review fixes).
// F1 (unallocated cursor variable → native 16950, not a 137 session-kill), F2 (a callee's own
// unallocated @c must not leak the caller's cursor), F3 (a cursor survives a ROLLBACK — fact 24
// corrected). Each asserts the debugger's result sets equal a native EXEC's, byte-for-byte.
public sealed class P38CursorVariableEdgeFidelityTests
{
    private const string ConnEnvVar = "TSQLDBG_TEST_CONN";

    [SkippableTheory]
    [InlineData("dbo.p38_unallocated", StepKind.Over)]   // F1
    [InlineData("dbo.p38_rollback", StepKind.Over)]      // F3
    [InlineData("dbo.p38_leak_caller", StepKind.Into)]   // F2 (Into pushes the callee frame)
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

    private static async Task DeployFixtureAsync(string connectionString)
    {
        var sqlPath = Path.Combine(AppContext.BaseDirectory, "corpus", "p38_cursor_variable_edges.sql");
        var script = await File.ReadAllTextAsync(sqlPath);

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        // SqlCommand does not understand the GO batch separator — split and run each CREATE alone.
        foreach (var batch in Regex.Split(script, @"(?im)^\s*GO\s*$"))
        {
            if (string.IsNullOrWhiteSpace(batch)) continue;
            await using var command = connection.CreateCommand();
            command.CommandText = batch;
            await command.ExecuteNonQueryAsync();
        }
    }

    // Native truth: EXEC the proc directly (each proc manages its own transaction; none writes
    // persistent state) and flatten every result set to a stable string.
    private static async Task<string> RunNativeAsync(string connectionString, string procedure)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SET NOCOUNT ON; EXEC {procedure};";
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
            new Dictionary<string, string>(), ScriptText: null);

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
