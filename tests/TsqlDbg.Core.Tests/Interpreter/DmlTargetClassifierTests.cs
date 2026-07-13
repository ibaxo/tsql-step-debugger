// DESIGN §21 C2 (M7 hardening) — the DML-target-name classifier: what Session's C2
// hook (a cached, lazy sys.triggers lookup) asks about. Never a local/global temp
// table or a table variable (neither can carry triggers in SQL Server at all).
using TsqlDbg.Core.Interpreter;
using Xunit;

namespace TsqlDbg.Core.Tests.Interpreter;

public class DmlTargetClassifierTests
{
    private static string? TargetOf(string sql) => DmlTargetClassifier.TryGetTargetTableName(ParseTestHelper.ParseSingle(sql));

    [Theory]
    [InlineData("INSERT dbo.T VALUES (1);", "dbo.T")]
    [InlineData("INSERT T VALUES (1);", "T")]                       // unqualified — no schema prefix
    [InlineData("UPDATE dbo.T SET v = 1;", "dbo.T")]
    [InlineData("DELETE dbo.T;", "dbo.T")]
    [InlineData("DELETE FROM dbo.T;", "dbo.T")]
    // C2 applies to ANY DML target (not just insert-family movers, unlike A26/D1) —
    // an update-only MERGE still touches the target table's own triggers.
    [InlineData("MERGE dbo.T AS t USING dbo.S AS s ON t.k = s.k WHEN MATCHED THEN UPDATE SET v = s.v;", "dbo.T")]
    [InlineData("MERGE dbo.T AS t USING dbo.S AS s ON t.k = s.k WHEN NOT MATCHED THEN INSERT (k) VALUES (s.k);", "dbo.T")]
    public void NamedTableTarget_ReturnsTheQualifiedName(string sql, string expected) => Assert.Equal(expected, TargetOf(sql));

    [Theory]
    [InlineData("INSERT #tmp VALUES (1);")]                          // local temp table — cannot carry triggers
    [InlineData("INSERT ##globaltmp VALUES (1);")]                   // global temp table — same reason
    [InlineData("INSERT @t VALUES (1);")]                            // table variable — VariableTableReference, not NamedTableReference
    [InlineData("UPDATE #tmp SET v = 1;")]
    public void TempTableOrTableVariableTarget_ReturnsNull(string sql) => Assert.Null(TargetOf(sql));

    [Theory]
    [InlineData("SELECT c FROM dbo.T;")]                             // reads FROM, not a DML target
    [InlineData("SELECT c INTO dbo.T2 FROM dbo.S;")]                 // creates a brand-new table — no pre-existing triggers possible
    [InlineData("SET @x = 1;")]
    [InlineData("EXEC dbo.p;")]
    [InlineData("PRINT 'x';")]
    [InlineData("CREATE TABLE #t (id int);")]
    public void NonDmlOrNonTarget_ReturnsNull(string sql) => Assert.Null(TargetOf(sql));
}
