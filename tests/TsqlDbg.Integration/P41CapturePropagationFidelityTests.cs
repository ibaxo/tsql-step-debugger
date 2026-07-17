using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using TsqlDbg.Core.Sessions;
using TsqlDbg.Core.Targets;
using Xunit;

namespace TsqlDbg.Integration;

// docs/DESIGN.md §11.7 (C11 / A67 capture propagation into nested frames, 2026-07-17). Stepping INTO a
// plain nested `EXEC proc` while inside an `INSERT … EXEC` capture PROPAGATES the capture: the child
// frame inherits the ancestor stage's insert reference (CaptureTargetSql) but owns no stage and no
// flush (CaptureFlushSql null), so its result-returning statements redirect into the one shared
// seq-ordered stage the owner materializes at its completed pop. Native buffers the WHOLE callee
// subtree's result stream (engine fact 35), so stepping Over and Into both reproduce a native EXEC
// byte-for-byte — a FIDELITY case. Refused sub-cases (dynamic child, nested INSERT…EXEC, unsafe body)
// step OVER, which native still captures as one batch, so those too stay Into == Over == native.
// See tests/corpus/p41_capture_propagation.sql.
public sealed class P41CapturePropagationFidelityTests
{
    private const string ConnEnvVar = "TSQLDBG_TEST_CONN";

    [SkippableTheory]
    [InlineData("dbo.p41_prop", StepKind.Over)]        // one-level: nested plain EXEC captured (step-over baseline)
    [InlineData("dbo.p41_prop", StepKind.Into)]        // one-level: step-into PROPAGATES the capture
    [InlineData("dbo.p41_deep2", StepKind.Over)]       // two-level baseline
    [InlineData("dbo.p41_deep2", StepKind.Into)]       // two-level: propagation recurses (deep → deeper)
    [InlineData("dbo.p41_faultprop", StepKind.Over)]   // fact 35b baseline
    [InlineData("dbo.p41_faultprop", StepKind.Into)]   // fact 35b: nested child faults into ancestor TRY, buffer survives
    [InlineData("dbo.p41_ctedeep", StepKind.Over)]     // unsafe-child baseline
    [InlineData("dbo.p41_ctedeep", StepKind.Into)]     // unsafe child body (CTE) → propagation refused, transparent step-over
    [InlineData("dbo.p41_nie", StepKind.Over)]         // nested INSERT…EXEC baseline
    [InlineData("dbo.p41_nie", StepKind.Into)]         // nested INSERT…EXEC one level down → refused → step-over 8164
    [InlineData("dbo.p41_ident", StepKind.Over)]       // cross-frame stream order into an IDENTITY target (baseline)
    [InlineData("dbo.p41_ident", StepKind.Into)]       // propagation must preserve seq order across the boundary (C28)
    [InlineData("dbo.p41_dynchild", StepKind.Over)]    // dynamic child (EXEC(@sql)/sp_executesql) baseline
    [InlineData("dbo.p41_dynchild", StepKind.Into)]    // dynamic child → propagation refused → transparent step-over
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
        var sqlPath = Path.Combine(AppContext.BaseDirectory, "corpus", "p41_capture_propagation.sql");
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
