using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using TsqlDbg.Core.Sessions;
using TsqlDbg.Core.Targets;
using Xunit;

namespace TsqlDbg.Integration;

// docs/DESIGN.md §11.7 (C11 / A68 dynamic-source capture, 2026-07-17). Stepping INTO an
// `INSERT <target> EXEC(@sql)` / `EXEC sp_executesql @sql` pushes a DYNAMIC frame that OWNS the
// capture stage; stepping INTO a dynamic child (`EXEC(@sql)` / `sp_executesql`) while an ancestor is
// capturing PROPAGATES the capture into that dynamic frame. The capture machinery keys on the frame's
// CaptureTargetSql / CaptureFlushSql (never procedure-vs-dynamic), so a dynamic frame captures
// identically. Native buffers a dynamic source's — and a nested dynamic child's — result stream into
// the target (engine fact 36), so stepping Over and Into both reproduce a native EXEC byte-for-byte —
// a FIDELITY case. Refused sub-cases (unsafe dynamic body, nested INSERT…EXEC) step OVER, which native
// still captures as one batch, so those too stay Into == Over == native.
// See tests/corpus/p42_dynamic_source_capture.sql.
public sealed class P42DynamicSourceCaptureFidelityTests
{
    private const string ConnEnvVar = "TSQLDBG_TEST_CONN";

    [SkippableTheory]
    [InlineData("dbo.p42_outer_exec", StepKind.Over)]     // outer EXEC(@sql) capture baseline
    [InlineData("dbo.p42_outer_exec", StepKind.Into)]     // outer EXEC(@sql): step-into, dynamic frame OWNS the stage
    [InlineData("dbo.p42_outer_spexec", StepKind.Over)]   // outer sp_executesql (with @params) baseline
    [InlineData("dbo.p42_outer_spexec", StepKind.Into)]   // outer sp_executesql: dynamic owner
    [InlineData("dbo.p42_fault", StepKind.Over)]          // mid-stream fault baseline
    [InlineData("dbo.p42_fault", StepKind.Into)]          // mid-stream fault → stage discarded, target empty (I7)
    [InlineData("dbo.p42_nested_dyn", StepKind.Over)]     // nested dynamic child baseline
    [InlineData("dbo.p42_nested_dyn", StepKind.Into)]     // nested dynamic child → PROPAGATES into the shared stage
    [InlineData("dbo.p42_ident", StepKind.Over)]          // cross-frame stream order into an IDENTITY target (baseline)
    [InlineData("dbo.p42_ident", StepKind.Into)]          // dynamic child must preserve seq order across the boundary (C28)
    [InlineData("dbo.p42_nie_dyn", StepKind.Over)]        // nested INSERT…EXEC in dynamic body baseline
    [InlineData("dbo.p42_nie_dyn", StepKind.Into)]        // nested INSERT…EXEC in dynamic body → refused → step-over 8164
    [InlineData("dbo.p42_cte_dyn", StepKind.Over)]        // unsafe dynamic body baseline
    [InlineData("dbo.p42_cte_dyn", StepKind.Into)]        // unsafe dynamic body (CTE) → refused → transparent step-over
    [InlineData("dbo.p42_sp_child", StepKind.Over)]       // sp_executesql child propagation baseline
    [InlineData("dbo.p42_sp_child", StepKind.Into)]       // nested sp_executesql child → PROPAGATES (not just EXEC(@sql))
    [InlineData("dbo.p42_dyn_in_dyn", StepKind.Over)]     // dynamic-owner → dynamic-child chain baseline
    [InlineData("dbo.p42_dyn_in_dyn", StepKind.Into)]     // dynamic owner with a dynamic child → both capture, stream order
    [InlineData("dbo.p42_collist", StepKind.Over)]        // explicit column list on a dynamic owner baseline
    [InlineData("dbo.p42_collist", StepKind.Into)]        // INSERT #t (a, b) EXEC(@sql) → explicit list threaded to the stage
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
        var sqlPath = Path.Combine(AppContext.BaseDirectory, "corpus", "p42_dynamic_source_capture.sql");
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
