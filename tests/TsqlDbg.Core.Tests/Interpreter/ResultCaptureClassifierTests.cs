using TsqlDbg.Core.Interpreter;
using TsqlDbg.Core.Tests.Interpreter;
using Xunit;

namespace TsqlDbg.Core.Tests.Interpreter;

// DESIGN §11.1 (C11/A64): which callee statements stream a client result set (and so must be
// captured into the INSERT target) versus which run unwrapped.
public class ResultCaptureClassifierTests
{
    private static bool IsReturning(string sql)
        => ResultCaptureClassifier.IsResultReturning(ParseTestHelper.ParseSingle(sql));

    [Theory]
    [InlineData("SELECT 1 AS v;")]                       // bare scalar select
    [InlineData("SELECT * FROM dbo.t;")]                 // star
    [InlineData("SELECT a, b FROM dbo.t WHERE a > 1;")]  // projection
    [InlineData("SELECT 1 AS v UNION ALL SELECT 2;")]    // compound query (cannot assign)
    [InlineData("EXEC dbo.p;")]                          // nested EXEC (results pass through)
    [InlineData("EXEC dbo.p @a = 1;")]
    public void ResultReturning_IsCaptured(string sql) => Assert.True(IsReturning(sql));

    [Theory]
    [InlineData("SELECT @x = 1;")]                       // variable assignment — no client rows
    [InlineData("SELECT @x = a FROM dbo.t;")]            // assignment from a table
    [InlineData("SELECT a INTO #tmp FROM dbo.t;")]       // SELECT … INTO — creates a table, no rows
    [InlineData("SET @x = 1;")]                          // not a select at all
    [InlineData("UPDATE dbo.t SET a = 1;")]              // DML — no client rows
    [InlineData("INSERT INTO dbo.t (a) VALUES (1);")]
    [InlineData("DECLARE @x int;")]
    [InlineData("PRINT 'hi';")]
    public void NonReturning_RunsUnwrapped(string sql) => Assert.False(IsReturning(sql));
}
