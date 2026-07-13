// M5 I6 §4.3 (design note §2, docs/archive/reviews/m5-inspection-design-notes-fable.md):
// "unit tests over the whitelist (SELECT-only, SELECT INTO, NEXT VALUE FOR, tran
// control, SET, USE, __dbg guard, one-statement rule) x allowConsoleWrites" — the
// pure, state-INDEPENDENT half of the §22 automated M5 acceptance criterion.
// ReplWhitelist.Classify has no Session/state dependency at all; the healthy/doomed/
// detached/broken half of the matrix lives in SessionReplTests.cs, which drives the
// SAME classifications through Session.EvaluateReplAsync.
using Microsoft.SqlServer.TransactSql.ScriptDom;
using TsqlDbg.Core.Batches;
using TsqlDbg.Core.Parsing;
using Xunit;

namespace TsqlDbg.Core.Tests.Batches;

public sealed class ReplWhitelistTests
{
    private static TSqlStatement Parse(string sql)
    {
        var list = ScriptParser.ParseStatementList(sql, true, 150, out var errors);
        Assert.Empty(errors);
        return Assert.Single(list!.Statements);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PlainSelect_AlwaysAllowed_NotAWrite(bool allowWrites)
    {
        const string sql = "SELECT 1;";
        var result = ReplWhitelist.Classify(Parse(sql), sql, allowWrites);

        Assert.True(result.IsAllowed);
        Assert.False(result.IsWrite);
        Assert.False(result.CreatesNewTempObject);
    }

    [Fact]
    public void SelectInto_ReadOnly_Refused()
    {
        const string sql = "SELECT 1 AS x INTO #t;";
        var result = ReplWhitelist.Classify(Parse(sql), sql, allowConsoleWrites: false);

        Assert.False(result.IsAllowed);
        Assert.Equal(ReplRefusalKind.SelectIntoInReadOnly, result.Refusal);
    }

    [Fact]
    public void SelectInto_WriteMode_Allowed_AndFlaggedAsCreatingATempObject()
    {
        const string sql = "SELECT 1 AS x INTO #t;";
        var result = ReplWhitelist.Classify(Parse(sql), sql, allowConsoleWrites: true);

        Assert.True(result.IsAllowed);
        Assert.True(result.IsWrite);
        Assert.True(result.CreatesNewTempObject);
    }

    [Fact]
    public void NextValueFor_ReadOnly_Refused()
    {
        const string sql = "SELECT NEXT VALUE FOR dbo.MySeq;";
        var result = ReplWhitelist.Classify(Parse(sql), sql, allowConsoleWrites: false);

        Assert.False(result.IsAllowed);
        Assert.Equal(ReplRefusalKind.NextValueForInReadOnly, result.Refusal);
    }

    [Fact]
    public void NextValueFor_WriteMode_Allowed()
    {
        const string sql = "SELECT NEXT VALUE FOR dbo.MySeq;";
        var result = ReplWhitelist.Classify(Parse(sql), sql, allowConsoleWrites: true);

        Assert.True(result.IsAllowed);
    }

    [Theory]
    [InlineData("BEGIN TRANSACTION;", false)]
    [InlineData("BEGIN TRANSACTION;", true)]
    [InlineData("COMMIT TRANSACTION;", false)]
    [InlineData("COMMIT TRANSACTION;", true)]
    [InlineData("ROLLBACK TRANSACTION;", false)]
    [InlineData("ROLLBACK TRANSACTION;", true)]
    [InlineData("SAVE TRANSACTION s1;", false)]
    [InlineData("SAVE TRANSACTION s1;", true)]
    public void TransactionControl_AlwaysRefused_RegardlessOfAllowConsoleWrites(string sql, bool allowWrites)
    {
        var result = ReplWhitelist.Classify(Parse(sql), sql, allowWrites);

        Assert.False(result.IsAllowed);
        Assert.Equal(ReplRefusalKind.TransactionControl, result.Refusal);
    }

    [Theory]
    [InlineData("SET ANSI_NULLS ON;", false)]
    [InlineData("SET ANSI_NULLS ON;", true)]
    [InlineData("SET ARITHABORT OFF;", true)]
    [InlineData("SET TRANSACTION ISOLATION LEVEL SERIALIZABLE;", true)]
    [InlineData("SET ROWCOUNT 10;", true)]
    public void SetOptionStatement_AlwaysRefused_RegardlessOfAllowConsoleWrites(string sql, bool allowWrites)
    {
        var result = ReplWhitelist.Classify(Parse(sql), sql, allowWrites);

        Assert.False(result.IsAllowed);
        Assert.Equal(ReplRefusalKind.SetOption, result.Refusal);
    }

    [Fact]
    public void SetVariableAssignment_IsNotASetOptionStatement_AllowedInWriteMode()
    {
        // SET @x = 5 is a plain variable assignment, not a session-option toggle —
        // must NOT be refused as a "SET statement" the way SET ANSI_NULLS ON is.
        const string sql = "SET @x = 5;";
        var result = ReplWhitelist.Classify(Parse(sql), sql, allowConsoleWrites: true);

        Assert.True(result.IsAllowed);
        Assert.True(result.IsWrite);
    }

    [Theory]
    [InlineData("SET @x = 5;")]
    [InlineData("SET @x = @y + 1;")]
    [InlineData("SET @x = (SELECT COUNT(*) FROM dbo.T);")]
    public void SetVariableAssignment_IsFlaggedVariableOnlyWrite(string sql)
    {
        // A46: `SET @x = expr` modifies no table — the one write shape allowed while the
        // transaction is doomed (it persists to the frame snapshot, not the dead table).
        var result = ReplWhitelist.Classify(Parse(sql), sql, allowConsoleWrites: true);

        Assert.True(result.IsAllowed);
        Assert.True(result.IsWrite);
        Assert.True(result.IsVariableOnlyWrite);
    }

    [Fact]
    public void SetVariableFromNextValueFor_IsNotVariableOnlyWrite()
    {
        // A46: NEXT VALUE FOR is a durable, non-transactional side effect — it must NOT
        // ride the doomed variable-write exemption even though it targets a variable.
        const string sql = "SET @x = NEXT VALUE FOR dbo.MySeq;";
        var result = ReplWhitelist.Classify(Parse(sql), sql, allowConsoleWrites: true);

        Assert.True(result.IsAllowed);
        Assert.True(result.IsWrite);
        Assert.False(result.IsVariableOnlyWrite);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UseStatement_AlwaysRefused(bool allowWrites)
    {
        const string sql = "USE msdb;";
        var result = ReplWhitelist.Classify(Parse(sql), sql, allowWrites);

        Assert.False(result.IsAllowed);
        Assert.Equal(ReplRefusalKind.UseStatement, result.Refusal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DbgIdentifierReference_AlwaysRefused_HarnessSelfProtection(bool allowWrites)
    {
        const string sql = "SELECT * FROM #__dbg_s0;";
        var result = ReplWhitelist.Classify(Parse(sql), sql, allowWrites);

        Assert.False(result.IsAllowed);
        Assert.Equal(ReplRefusalKind.DbgIdentifierReference, result.Refusal);
    }

    [Fact]
    public void NonSelectStatement_ReadOnly_Refused()
    {
        const string sql = "INSERT INTO dbo.T (a) VALUES (1);";
        var result = ReplWhitelist.Classify(Parse(sql), sql, allowConsoleWrites: false);

        Assert.False(result.IsAllowed);
        Assert.Equal(ReplRefusalKind.ReadOnlyNonSelect, result.Refusal);
    }

    [Theory]
    [InlineData("INSERT INTO dbo.T (a) VALUES (1);")]
    [InlineData("UPDATE dbo.T SET a = 1;")]
    [InlineData("DELETE FROM dbo.T;")]
    [InlineData("CREATE TABLE #x (a int);")]
    [InlineData("EXEC dbo.SomeProc;")]
    public void DmlDdlExec_WriteMode_Allowed(string sql)
    {
        var result = ReplWhitelist.Classify(Parse(sql), sql, allowConsoleWrites: true);

        Assert.True(result.IsAllowed);
        Assert.True(result.IsWrite);
        Assert.False(result.IsVariableOnlyWrite);   // A46: a DB write is refused while doomed
    }

    [Fact]
    public void CreateTempTable_FlaggedAsCreatingANewTempObject()
    {
        const string sql = "CREATE TABLE #x (a int);";
        var result = ReplWhitelist.Classify(Parse(sql), sql, allowConsoleWrites: true);

        Assert.True(result.CreatesNewTempObject);
    }

    [Fact]
    public void DeclareTableVariable_FlaggedAsCreatingANewTempObject()
    {
        const string sql = "DECLARE @t TABLE (a int);";
        var result = ReplWhitelist.Classify(Parse(sql), sql, allowConsoleWrites: true);

        Assert.True(result.IsAllowed);
        Assert.True(result.CreatesNewTempObject);
    }

    [Fact]
    public void DeclareCursor_FlaggedAsCreatingANewTempObject()
    {
        const string sql = "DECLARE cur CURSOR LOCAL FOR SELECT 1;";
        var result = ReplWhitelist.Classify(Parse(sql), sql, allowConsoleWrites: true);

        Assert.True(result.IsAllowed);
        Assert.True(result.CreatesNewTempObject);
    }

    [Fact]
    public void PlainReferenceStatement_NotFlaggedAsCreatingATempObject()
    {
        const string sql = "SELECT * FROM #work;";
        var result = ReplWhitelist.Classify(Parse(sql), sql, allowConsoleWrites: false);

        Assert.False(result.CreatesNewTempObject);
    }
}
