using Microsoft.SqlServer.TransactSql.ScriptDom;
using TsqlDbg.Core.Interpreter;
using TsqlDbg.Core.Parsing;
using Xunit;

namespace TsqlDbg.Core.Tests.Interpreter;

// DESIGN §11.7 (C11/A64): callee statement shapes that cannot be captured statement-by-statement
// (`INSERT INTO <target> <stmt>`) refuse step-into → faithful native step-over.
public class CaptureSafetyScannerTests
{
    private static string? Scan(string body)
    {
        var fragment = ScriptParser.Parse(body, initialQuotedIdentifiers: true, compatLevel: 150, out var errors);
        Assert.Empty(errors);
        return CaptureSafetyScanner.FindUncapturableStatement(((TSqlScript)fragment).Batches[0].Statements);
    }

    [Theory]
    [InlineData("WITH cte AS (SELECT 1 AS v) SELECT v FROM cte;")]     // S1: CTE-headed result SELECT
    [InlineData("ROLLBACK;")]                                          // S2: transaction control
    [InlineData("BEGIN TRAN; SELECT 1 AS v;")]
    [InlineData("COMMIT;")]
    [InlineData("SAVE TRANSACTION s;")]
    [InlineData("INSERT INTO #other EXEC dbo.q;")]                     // I5: nested INSERT…EXEC
    [InlineData("DELETE FROM #s OUTPUT deleted.v WHERE v >= 2;")]      // I1: streaming OUTPUT (no INTO)
    [InlineData("UPDATE #s SET v = 1 OUTPUT inserted.v;")]
    [InlineData("FETCH NEXT FROM c;")]                                 // I2: bare FETCH
    [InlineData("IF 1 = 1 WITH cte AS (SELECT 1 AS v) SELECT v FROM cte;")]  // nested inside control flow
    public void UncapturableShapes_AreRefused(string body) => Assert.NotNull(Scan(body));

    [Theory]
    [InlineData("SELECT 1 AS v;")]                                    // bare SELECT — captured, safe
    [InlineData("SELECT @x = 1;")]                                    // assignment — safe
    [InlineData("SELECT a INTO #x FROM dbo.t;")]                      // SELECT INTO — safe
    [InlineData("WITH cte AS (SELECT 1 AS v) SELECT @x = v FROM cte;")]  // CTE ASSIGNMENT — not captured, safe
    [InlineData("EXEC dbo.q;")]                                       // nested plain EXEC — pass-through capture, safe
    [InlineData("DELETE FROM #s OUTPUT deleted.v INTO #o WHERE v >= 2;")]  // OUTPUT INTO — safe
    [InlineData("FETCH NEXT FROM c INTO @x;")]                        // FETCH INTO vars — safe
    [InlineData("SET @x = 1; DECLARE @y int; UPDATE #s SET v = 1;")]  // ordinary statements
    public void CapturableShapes_AreAllowed(string body) => Assert.Null(Scan(body));
}
