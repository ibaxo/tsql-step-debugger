// DESIGN §7.4 / A26 (D1) — the SCOPE_IDENTITY() chain-sync classifier, pinned against
// every engine shape from fact 31b (docs/engine-facts.md). Insert-family = the statement
// classes that natively MOVE the chain (and therefore re-sync it, clearing the poison
// flag). Everything else is NEUTRAL: while poisoned its capture is skipped.
using TsqlDbg.Core.Interpreter;
using Xunit;

namespace TsqlDbg.Core.Tests.Interpreter;

public class InsertFamilyClassifierTests
{
    private static bool Classify(string sql) => InsertFamilyClassifier.IsInsertFamily(ParseTestHelper.ParseSingle(sql));

    [Theory]
    // INSERT — every source (fact 31b + 26d): VALUES, SELECT, EXEC, and zero-row.
    [InlineData("INSERT dbo.T VALUES (1);")]
    [InlineData("INSERT dbo.T (c) SELECT c FROM dbo.S;")]
    [InlineData("INSERT dbo.T EXEC dbo.p;")]
    [InlineData("INSERT dbo.T EXEC('SELECT 1');")]
    [InlineData("INSERT dbo.T SELECT 1 WHERE 1 = 0;")]                 // zero-row (fact 26d)
    // SELECT ... INTO — creates + inserts.
    [InlineData("SELECT c INTO dbo.T2 FROM dbo.S;")]
    // MERGE with an INSERT action clause — statement-class-based (fact 31b: resets even at 0 rows).
    [InlineData("MERGE dbo.T AS t USING dbo.S AS s ON t.k = s.k WHEN NOT MATCHED THEN INSERT (k) VALUES (s.k);")]
    [InlineData("MERGE dbo.T AS t USING dbo.S AS s ON t.k = s.k WHEN MATCHED THEN UPDATE SET v = s.v WHEN NOT MATCHED THEN INSERT (k) VALUES (s.k);")]
    [InlineData("MERGE dbo.T AS t USING dbo.S AS s ON t.k = s.k WHEN NOT MATCHED THEN INSERT (k) VALUES (s.k) OUTPUT inserted.k INTO dbo.O;")]
    public void ChainMovers_AreInsertFamily(string sql) => Assert.True(Classify(sql), sql);

    [Theory]
    // UPDATE/DELETE, incl. OUTPUT and OUTPUT INTO — NEUTRAL (fact 31b: the output-target
    // insert does NOT move the caller's chain). Excluding them is the whole point of 31b.
    [InlineData("UPDATE dbo.T SET v = 1;")]
    [InlineData("DELETE dbo.T;")]
    [InlineData("UPDATE dbo.T SET v = 1 OUTPUT inserted.v INTO dbo.O;")]
    [InlineData("DELETE dbo.T OUTPUT deleted.v INTO dbo.O;")]
    [InlineData("UPDATE dbo.T SET v = 1 OUTPUT inserted.v;")]
    // MERGE with only UPDATE / only DELETE actions — NEUTRAL.
    [InlineData("MERGE dbo.T AS t USING dbo.S AS s ON t.k = s.k WHEN MATCHED THEN UPDATE SET v = s.v;")]
    [InlineData("MERGE dbo.T AS t USING dbo.S AS s ON t.k = s.k WHEN MATCHED THEN DELETE;")]
    // Non-DML.
    [InlineData("SELECT c FROM dbo.T;")]
    [InlineData("SET @x = 1;")]
    [InlineData("EXEC dbo.p;")]
    [InlineData("CREATE TABLE #t (id int);")]
    [InlineData("PRINT 'x';")]
    public void NonChainMovers_AreNeutral(string sql) => Assert.False(Classify(sql), sql);
}
