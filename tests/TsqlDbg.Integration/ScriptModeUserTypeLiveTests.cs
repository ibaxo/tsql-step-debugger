using Microsoft.Data.SqlClient;
using TsqlDbg.Core.Sessions;
using TsqlDbg.Core.Targets;
using Xunit;

namespace TsqlDbg.Integration;

// A59 in SCRIPT mode — the extension's default surface (a .sql file, F5). P29/P30 pin
// procedure mode; this pins the same two type kinds through the script-frame path, where
// frame 0 is an ad-hoc batch rather than a compiled module, plus the one thing only a
// script can do: DEFINE the type it later declares (§4 step 2a's catalog refresh).
public sealed class ScriptModeUserTypeLiveTests
{
    private const string ConnEnvVar = "TSQLDBG_TEST_CONN";

    private const string Setup = @"
IF TYPE_ID('dbo.sm_Name') IS NULL EXEC('CREATE TYPE dbo.sm_Name FROM nvarchar(50) NOT NULL');
IF TYPE_ID('dbo.sm_Rows') IS NULL EXEC('CREATE TYPE dbo.sm_Rows AS TABLE (id int IDENTITY(1,1), nm nvarchar(30) NOT NULL, qty int NULL DEFAULT ((7)))');
";

    [SkippableFact]
    public async Task ScriptMode_AliasTypedVariable_StepsAndKeepsItsValue()
    {
        var connectionString = Require();
        await ExecuteAsync(connectionString, Setup);

        var rows = await RunScriptAsync(connectionString, @"
DECLARE @Name dbo.sm_Name = N'Ada';
DECLARE @Loud dbo.sm_Name;
SET @Loud = UPPER(@Name) + N'!';
SELECT @Loud AS Loud, LEN(@Loud) AS Len;");

        Assert.Equal("ADA!", rows[0]);
        Assert.Equal(4, rows[1]);
    }

    [SkippableFact]
    public async Task ScriptMode_TableTypedVariable_FillsAndReadsBack()
    {
        var connectionString = Require();
        await ExecuteAsync(connectionString, Setup);

        var rows = await RunScriptAsync(connectionString, @"
DECLARE @t dbo.sm_Rows;
INSERT INTO @t (nm) VALUES (N'a'), (N'b');
INSERT INTO @t (nm, qty) VALUES (N'c', 1);
SELECT COUNT(*) AS Cnt, SUM(qty) AS Total, MAX(id) AS MaxId FROM @t;");

        Assert.Equal(3, rows[0]);
        Assert.Equal(15, rows[1]);      // DEFAULT ((7)) twice + explicit 1
        Assert.Equal(3, rows[2]);       // IDENTITY(1,1)
    }

    // The two CONVERT sites P29 cannot reach, because it neither dooms nor rolls back: the
    // §10.4 DOOMED seed (values ride parameters from the snapshot: `SELECT @n = CONVERT(…,
    // @p)`) and the §10.4 RESURRECTION re-seed after the debuggee's ROLLBACK (`UPDATE
    // #__dbg_s0 SET [n] = CONVERT(…, @p)`). Both would raise msg 243 — "Type dbo.sm_Name is
    // not a defined system type" (fact 34b) — if either still named the DECLARED type
    // instead of the storage type. The value must survive both crossings intact.
    [SkippableFact]
    public async Task ScriptMode_AliasTypedVariable_SurvivesADoomAndARollback()
    {
        var connectionString = Require();
        await ExecuteAsync(connectionString, Setup);

        // The faulting SELECT emits a result set of its own before it errors, so this script
        // ends with two — the value under test is the last.
        var rows = await RunScriptAsync(connectionString, @"
DECLARE @n dbo.sm_Name = N'keep';
BEGIN TRANSACTION;
BEGIN TRY
    SELECT CAST('not-a-number' AS int);   -- 245: dooms the transaction
END TRY
BEGIN CATCH
    SET @n = @n + N'-caught';             -- runs while DOOMED: the doomed-seed CONVERT
END CATCH;
ROLLBACK;                                 -- then the resurrection re-seed CONVERT
SET @n = @n + N'-after';
SELECT @n AS N;", expectLastResultSet: true);

        Assert.Equal("keep-caught-after", rows[0]);
    }

    // §4 step 2a: the catalog is refreshed after an executed CREATE TYPE, so a script can
    // define — across a GO — the type a later batch declares. Without the refresh, batch 2's
    // frame init would treat dbo.sm_Fresh as an unknown named type and pass it to tempdb,
    // which is 2715 all over again. (The CREATE TYPE lives inside the safety transaction and
    // is rolled back at teardown, so the test leaves no residue.)
    [SkippableFact]
    public async Task ScriptMode_TypeCreatedByTheScriptItself_IsDeclarableInALaterBatch()
    {
        var connectionString = Require();

        var rows = await RunScriptAsync(connectionString, @"
CREATE TYPE dbo.sm_Fresh FROM decimal(6,3);
GO
DECLARE @v dbo.sm_Fresh = 1.5;
SET @v = @v * 3;
SELECT @v AS V;");

        Assert.Equal(4.500m, rows[0]);

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var check = connection.CreateCommand();
        check.CommandText = "SELECT TYPE_ID('dbo.sm_Fresh');";
        Assert.IsType<DBNull>(await check.ExecuteScalarAsync());   // rolled back with the session
    }

    private static string Require()
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnEnvVar);
        Skip.If(
            string.IsNullOrWhiteSpace(connectionString),
            $"{ConnEnvVar} is not set; skipping live test (never fake a pass — CLAUDE.md).");
        return connectionString!;
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<IReadOnlyList<object?>> RunScriptAsync(
        string connectionString, string scriptText, bool expectLastResultSet = false)
    {
        var csb = new SqlConnectionStringBuilder(connectionString);
        var target = new TargetEntry(csb.DataSource, "test", AllowWrites: false, Options: null);
        var options = new SessionOptions(
            csb.DataSource, csb.InitialCatalog, LaunchMode.Script,
            Procedure: null, Args: null, ScriptText: scriptText);

        var result = await SessionHost.RunAsync(options, target);
        var resultSet = expectLastResultSet
            ? result.Execution.ResultSets[^1]
            : Assert.Single(result.Execution.ResultSets);
        return Assert.Single(resultSet.Rows);
    }
}
