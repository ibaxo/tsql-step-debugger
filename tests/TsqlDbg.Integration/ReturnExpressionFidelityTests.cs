using System.Data;
using Microsoft.Data.SqlClient;
using TsqlDbg.Core.Sessions;
using TsqlDbg.Core.Targets;
using Xunit;

namespace TsqlDbg.Integration;

// §6/§11.5 D9 (ReturnFromFrame): a RETURN whose value is a non-trivial EXPRESSION
// (`RETURN (@x + 1)`, not a literal) is evaluated through the scalar-eval pipeline
// (ComposedBatchBuilder.BuildForScalarEval) into the frame's __ret — so @x resolves to
// its live state value and the arithmetic is computed. P17 pins `RETURN @var` and a bare
// RETURN; this pins the parenthesized-arithmetic shape from Ivan's fact09
// (dbo.p9_set_isolation: `DECLARE @x INT = 1; RETURN (@x+1);`), both as the frame-0 return
// code (procedure mode) and copied back to a caller's `EXEC @r =` variable (script mode,
// fact 23). Debugger vs native, both must read 2.
public sealed class ReturnExpressionFidelityTests
{
    private const string ConnEnvVar = "TSQLDBG_TEST_CONN";

    private const string ProcName = "dbo.a55_return_expr";
    private const string ProcDefinition = """
        CREATE OR ALTER PROCEDURE dbo.a55_return_expr AS
        BEGIN
            DECLARE @x INT = 1;
            RETURN (@x + 1);
        END
        """;

    [SkippableFact]
    public async Task ReturnExpression_ProcedureMode_ReturnCodeIsTheEvaluatedExpression_MatchingNative()
    {
        var raw = Environment.GetEnvironmentVariable(ConnEnvVar);
        Skip.If(string.IsNullOrWhiteSpace(raw), $"{ConnEnvVar} is not set; skipping fidelity test (never fake a pass — CLAUDE.md).");
        await DeployProcAsync(raw!);
        try
        {
            var native = await NativeReturnCodeAsync(raw!);
            Assert.Equal(2, native);   // @x + 1 = 2 (sanity: the expression, not the literal 1)

            var csb = new SqlConnectionStringBuilder(raw);
            var debugged = await DebuggerProcedureReturnCodeAsync(csb.DataSource, csb.InitialCatalog);
            Assert.Equal(native, debugged);
        }
        finally
        {
            await DropProcAsync(raw!);
        }
    }

    [SkippableFact]
    public async Task ReturnExpression_ScriptExecCapture_CopiesTheEvaluatedValueBack_MatchingNative()
    {
        var raw = Environment.GetEnvironmentVariable(ConnEnvVar);
        Skip.If(string.IsNullOrWhiteSpace(raw), $"{ConnEnvVar} is not set; skipping fidelity test (never fake a pass — CLAUDE.md).");
        await DeployProcAsync(raw!);
        try
        {
            // Ivan's fact09 shape: a caller captures the callee's return value.
            const string script = "DECLARE @result INT;\nEXEC @result = dbo.a55_return_expr;\nSELECT @result AS return_value;";

            var native = await NativeScriptScalarAsync(raw!, script);
            Assert.Equal(2, native);

            var csb = new SqlConnectionStringBuilder(raw);
            var debugged = await DebuggerScriptScalarAsync(csb.DataSource, csb.InitialCatalog, script);
            Assert.Equal(native, debugged);
        }
        finally
        {
            await DropProcAsync(raw!);
        }
    }

    private static async Task<int> NativeReturnCodeAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var tran = (SqlTransaction)await connection.BeginTransactionAsync();
        await using var command = connection.CreateCommand();
        command.Transaction = tran;
        command.CommandText = ProcName;
        command.CommandType = CommandType.StoredProcedure;
        var rc = command.Parameters.Add("@ReturnValue", SqlDbType.Int);
        rc.Direction = ParameterDirection.ReturnValue;
        await command.ExecuteNonQueryAsync();
        await tran.RollbackAsync();
        return (int)rc.Value!;
    }

    private static async Task<int> NativeScriptScalarAsync(string connectionString, string script)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var tran = (SqlTransaction)await connection.BeginTransactionAsync();
        await using var command = connection.CreateCommand();
        command.Transaction = tran;
        command.CommandText = script;
        var value = (int)(await command.ExecuteScalarAsync())!;
        await tran.RollbackAsync();
        return value;
    }

    private static async Task<int> DebuggerProcedureReturnCodeAsync(string server, string database)
    {
        var target = new TargetEntry(server, "test", AllowWrites: false, Options: null);
        var options = new SessionOptions(server, database, LaunchMode.Procedure, ProcName, null, ScriptText: null);
        var result = await SessionHost.RunAsync(options, target);
        return result.ReturnCode;
    }

    private static async Task<int> DebuggerScriptScalarAsync(string server, string database, string script)
    {
        var target = new TargetEntry(server, "test", AllowWrites: false, Options: null);
        var options = new SessionOptions(server, database, LaunchMode.Script, null, null, ScriptText: script);
        var result = await SessionHost.RunAsync(options, target);
        var resultSet = Assert.Single(result.Execution.ResultSets);
        var row = Assert.Single(resultSet.Rows);
        return (int)row[0]!;
    }

    private static async Task DeployProcAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var drop = connection.CreateCommand();
        drop.CommandText = $"IF OBJECT_ID('{ProcName}') IS NOT NULL DROP PROCEDURE {ProcName};";
        await drop.ExecuteNonQueryAsync();
        await using var create = connection.CreateCommand();
        create.CommandText = ProcDefinition;
        await create.ExecuteNonQueryAsync();
    }

    private static async Task DropProcAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"IF OBJECT_ID('{ProcName}') IS NOT NULL DROP PROCEDURE {ProcName};";
        await command.ExecuteNonQueryAsync();
    }
}
