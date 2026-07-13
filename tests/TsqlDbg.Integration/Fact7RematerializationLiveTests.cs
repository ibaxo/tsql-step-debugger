using Microsoft.Data.SqlClient;
using TsqlDbg.Core.Sessions;
using TsqlDbg.Core.Targets;
using Xunit;

namespace TsqlDbg.Integration;

// The M0 gate's standing Fact-7 obligation, discharged against the REAL §10.7
// implementation (M3): native ERROR_*() inside dynamic SQL invoked from a CATCH block
// is NOT NULL (docs/engine-facts.md fact 7 — the finding that contradicted Appendix C),
// and §10.7 re-materialization makes the debugger match it for ERROR_MESSAGE() while
// C21 registers the exact residual (ERROR_NUMBER() reads RAISERROR's 50000 for
// indirect consumers). This test asserts BOTH halves against a live server: the fix
// works, and the caveat's boundary is precisely where the register says it is.
public sealed class Fact7RematerializationLiveTests
{
    private const string ConnEnvVar = "TSQLDBG_TEST_CONN";

    private const string Script = """
        BEGIN TRY
            SELECT 1/0 AS boom;
        END TRY
        BEGIN CATCH
            EXEC('SELECT ERROR_MESSAGE() AS m, ERROR_NUMBER() AS n;');
        END CATCH
        """;

    private sealed record IndirectErrorView(string Message, int Number);

    [SkippableFact]
    public async Task Rematerialization_GivesIndirectConsumers_NativeErrorMessage_WithC21NumberResidual()
    {
        var rawConnectionString = Environment.GetEnvironmentVariable(ConnEnvVar);
        Skip.If(
            string.IsNullOrWhiteSpace(rawConnectionString),
            $"{ConnEnvVar} is not set; skipping fidelity test (never fake a pass — CLAUDE.md).");

        var csb = new SqlConnectionStringBuilder(rawConnectionString);

        var native = await RunNativeAsync(rawConnectionString!);
        var debugged = await RunThroughDebuggerAsync(csb.DataSource, csb.InitialCatalog);

        // Fact 7 (native truth): the dynamic SQL sees the real error.
        Assert.Equal("Divide by zero error encountered.", native.Message);
        Assert.Equal(8134, native.Number);

        // §10.7: the debugger's re-materialization gives the indirect consumer the
        // SAME message natively-faithfully...
        Assert.Equal(native.Message, debugged.Message);

        // ...and C21's registered residual is exactly — and only — the number:
        // RAISERROR's 50000 instead of the original 8134.
        Assert.Equal(50000, debugged.Number);
    }

    private static async Task<IndirectErrorView> RunNativeAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = Script;
        await using var reader = await command.ExecuteReaderAsync();

        // First result set is the faulted SELECT's empty 'boom' set; advance to m/n.
        while (reader.FieldCount == 0 || !string.Equals(reader.GetName(0), "m", StringComparison.Ordinal))
        {
            Assert.True(await reader.NextResultAsync(), "native run never produced the m/n result set");
        }

        Assert.True(await reader.ReadAsync());
        return new IndirectErrorView(reader.GetString(0), reader.GetInt32(1));
    }

    private static async Task<IndirectErrorView> RunThroughDebuggerAsync(string server, string database)
    {
        var target = new TargetEntry(server, "test", AllowWrites: false, Options: null);
        var options = new SessionOptions(server, database, LaunchMode.Script, null, null, Script);

        var result = await SessionHost.RunAsync(options, target);

        var mnSet = Assert.Single(result.Execution.ResultSets, rs => rs.Columns.Contains("m"));
        var row = Assert.Single(mnSet.Rows);
        return new IndirectErrorView((string)row[0]!, Convert.ToInt32(row[1]));
    }
}
