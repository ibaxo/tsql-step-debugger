using Microsoft.Data.SqlClient;
using Xunit;

namespace TsqlDbg.Integration;

// docs/engine-facts.md fact 34 (A59) — the engine facts the user-defined-type support
// load-bears on. Every one of these was verified by hand before a line of A59 was written
// (docs/engine-facts/fact34_user_defined_types.sql); this pins them in the live suite so a
// future engine/driver change that invalidates one FAILS here rather than silently
// corrupting a stepped session.
public sealed class Fact34UserTypeLiveTests : IAsyncLifetime
{
    private const string ConnEnvVar = "TSQLDBG_TEST_CONN";
    private string? _connectionString;

    public async Task InitializeAsync()
    {
        _connectionString = Environment.GetEnvironmentVariable(ConnEnvVar);
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            return;
        }

        await ExecuteAsync(@"
IF TYPE_ID('dbo.f34t_Name') IS NULL EXEC('CREATE TYPE dbo.f34t_Name FROM nvarchar(50) NOT NULL');
IF TYPE_ID('dbo.f34t_Rows') IS NULL EXEC('CREATE TYPE dbo.f34t_Rows AS TABLE (id int IDENTITY(10,5) NOT NULL, nm nvarchar(30) NOT NULL)');");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // 34a: THE bug. An alias type is database-scoped; the state table (§8.1) lives in tempdb.
    [SkippableFact]
    public async Task Fact34a_AliasType_IsNotAValidTempdbColumnType_Msg2715()
    {
        SkipIfNoConnection();
        var ex = await Assert.ThrowsAsync<SqlException>(() =>
            ExecuteAsync("CREATE TABLE #f34t (v dbo.f34t_Name); DROP TABLE #f34t;"));

        Assert.Equal(2715, ex.Number);
        Assert.Contains("Cannot find data type", ex.Message);
    }

    // 34b: so the declared type cannot be spliced into ANY of the four CONVERT sites either.
    [SkippableFact]
    public async Task Fact34b_ConvertToAnAliasType_IsRefused_Msg243()
    {
        SkipIfNoConnection();
        var ex = await Assert.ThrowsAsync<SqlException>(() =>
            ExecuteAsync("SELECT CONVERT(dbo.f34t_Name, N'x');"));

        Assert.Equal(243, ex.Number);
        Assert.Contains("is not a defined system type", ex.Message);
    }

    // 34b, the other half: converting FROM an alias-typed value is ordinary — it IS its base
    // type. This is why the §7.5 display projection needed no change at all.
    [SkippableFact]
    public async Task Fact34b_ConvertFromAnAliasTypedValue_IsOrdinary()
    {
        SkipIfNoConnection();
        var value = await ScalarAsync(
            "DECLARE @n dbo.f34t_Name = N'abc'; SELECT CONVERT(nvarchar(4000), @n, 121);");
        Assert.Equal("abc", value);
    }

    // 34c: legal natively — and the exact shape whose #temp realization would raise 34a's
    // 2715 unless the alias column is base-resolved (§9).
    [SkippableFact]
    public async Task Fact34c_AliasType_IsAValidTableVariableColumnType()
    {
        SkipIfNoConnection();
        var count = await ScalarAsync(
            "DECLARE @t TABLE (v dbo.f34t_Name); INSERT INTO @t VALUES (N'a'); SELECT COUNT(*) FROM @t;");
        Assert.Equal(1, count);
    }

    // 34e: the reason C28 exists — identity values cannot be supplied to a table variable, so
    // the §9 TVP materialization has to let them regenerate.
    [SkippableFact]
    public async Task Fact34e_SetIdentityInsert_OnATableVariable_IsASyntaxError()
    {
        SkipIfNoConnection();
        var ex = await Assert.ThrowsAsync<SqlException>(() =>
            ExecuteAsync("DECLARE @t dbo.f34t_Rows; SET IDENTITY_INSERT @t ON;"));

        Assert.Equal(102, ex.Number);      // incorrect syntax
    }

    // 34d: the base-type oracle — the server formats the type, so §8.1's "no client-side type
    // mapping" rule survives A59 intact.
    [SkippableFact]
    public async Task Fact34d_DescribeFirstResultSet_ReturnsTheFormattedBaseType()
    {
        SkipIfNoConnection();
        var systemTypeName = await ScalarAsync(
            "SELECT system_type_name FROM sys.dm_exec_describe_first_result_set(" +
            "N'SELECT @v AS v', N'@v dbo.f34t_Name', 0);");

        Assert.Equal("nvarchar(50)", systemTypeName);
    }

    private void SkipIfNoConnection() => Skip.If(
        string.IsNullOrWhiteSpace(_connectionString),
        $"{ConnEnvVar} is not set; skipping live fact probe (never fake a pass — CLAUDE.md).");

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private async Task<dynamic?> ScalarAsync(string sql)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }
}
