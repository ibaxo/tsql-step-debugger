using Microsoft.Data.SqlClient;
using TsqlDbg.Core.Sessions;
using TsqlDbg.Core.Targets;
using Xunit;

namespace TsqlDbg.Integration;

// Caveat C28 (§21/§9, A59): a table-type variable passed as a TVP argument has its IDENTITY
// values REGENERATED, because the debugger rebuilds it from its #temp realization and
// `SET IDENTITY_INSERT` is a syntax error on a table variable (fact 34e).
//
// Contiguous inserts reproduce the values exactly — that is p30, and it is the overwhelmingly
// common shape. This test pins the shape that does NOT: a row deleted from the variable
// leaves a GAP, and every later identity value shifts down by it. Like the C23/C25 probes,
// the divergence is asserted EXPLICITLY (native 10/20, debugger 10/15) rather than silently
// exempted — a caveat is a promise about what the debugger does, so it must be pinned, and
// if the debugger ever stops doing it this test fails and the register is wrong.
public sealed class C28TvpIdentityGapLiveTests
{
    private const string ConnEnvVar = "TSQLDBG_TEST_CONN";

    private const string Fixture = @"
IF TYPE_ID('dbo.c28_Rows') IS NULL
    EXEC('CREATE TYPE dbo.c28_Rows AS TABLE (id int IDENTITY(10,5) NOT NULL, nm nvarchar(30) NOT NULL)');
";

    private const string Procs = @"
CREATE OR ALTER PROCEDURE dbo.c28_max_id @Rows dbo.c28_Rows READONLY
AS
BEGIN
    SET NOCOUNT ON;
    RETURN (SELECT MAX(id) FROM @Rows);
END";

    private const string Caller = @"
CREATE OR ALTER PROCEDURE dbo.c28_identity_gap
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @t dbo.c28_Rows;
    INSERT INTO @t (nm) VALUES (N'a'), (N'b'), (N'c');   -- ids 10, 15, 20
    DELETE FROM @t WHERE nm = N'b';                      -- ids 10, 20 -- THE GAP

    DECLARE @MaxIdSeenByCallee int;
    EXEC @MaxIdSeenByCallee = dbo.c28_max_id @Rows = @t;
    SELECT @MaxIdSeenByCallee AS MaxIdSeenByCallee;
END";

    [SkippableFact]
    public async Task TvpMaterialization_RegeneratesIdentityValues_AcrossAGap_C28()
    {
        var rawConnectionString = Environment.GetEnvironmentVariable(ConnEnvVar);
        Skip.If(
            string.IsNullOrWhiteSpace(rawConnectionString),
            $"{ConnEnvVar} is not set; skipping live caveat probe (never fake a pass — CLAUDE.md).");

        await ExecuteAsync(rawConnectionString!, Fixture);
        await ExecuteAsync(rawConnectionString!, Procs);
        await ExecuteAsync(rawConnectionString!, Caller);

        var native = await RunNativeAsync(rawConnectionString!);
        var debugged = await RunThroughDebuggerAsync(rawConnectionString!);

        // Native: the table variable IS the argument, gap and all — the callee sees id 20.
        Assert.Equal(20, native);

        // Debugger: the realization holds 10 and 20, but re-inserting them into a fresh
        // variable of the type regenerates identities from the seed — 10 and 15. The callee
        // sees 15. This is C28, and it is the honest cost of a #temp realization: no other
        // mechanism can hand rows to a table-valued parameter (fact 34e).
        Assert.Equal(15, debugged);
        Assert.NotEqual(native, debugged);
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> RunNativeAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var tran = (SqlTransaction)await connection.BeginTransactionAsync();

        await using var command = connection.CreateCommand();
        command.Transaction = tran;
        command.CommandText = "EXEC dbo.c28_identity_gap;";
        var value = (int)(await command.ExecuteScalarAsync())!;

        await tran.RollbackAsync();
        return value;
    }

    private static async Task<int> RunThroughDebuggerAsync(string connectionString)
    {
        var csb = new SqlConnectionStringBuilder(connectionString);
        var target = new TargetEntry(csb.DataSource, "test", AllowWrites: false, Options: null);
        var options = new SessionOptions(
            csb.DataSource, csb.InitialCatalog, LaunchMode.Procedure, "dbo.c28_identity_gap",
            Args: null, ScriptText: null);

        var result = await SessionHost.RunAsync(options, target);
        var resultSet = Assert.Single(result.Execution.ResultSets);
        return (int)Assert.Single(resultSet.Rows)[0]!;
    }
}
