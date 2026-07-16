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

    // ---- A62 (§11.3 step 2 / C9): the complement — the @var a DML statement WRITES when its
    // target is a table variable. The step-into TVP guard refuses a callee that writes a READONLY
    // table-valued parameter (native msg 10700); this classifier is what it asks about. ----

    private static string? VariableTargetOf(string sql) =>
        DmlTargetClassifier.TryGetTargetVariableName(ParseTestHelper.ParseSingle(sql));

    [Theory]
    [InlineData("INSERT @rows VALUES (1);", "@rows")]
    [InlineData("INSERT INTO @rows (k) VALUES (1);", "@rows")]
    [InlineData("INSERT INTO @rows EXEC dbo.p;", "@rows")]
    [InlineData("UPDATE @rows SET v = 1;", "@rows")]
    [InlineData("DELETE @rows;", "@rows")]
    [InlineData("DELETE FROM @rows WHERE k = 1;", "@rows")]
    [InlineData("MERGE @rows AS t USING dbo.S AS s ON t.k = s.k WHEN MATCHED THEN UPDATE SET v = s.v;", "@rows")]
    public void VariableTableTarget_ReturnsTheVariableName(string sql, string expected) =>
        Assert.Equal(expected, VariableTargetOf(sql));

    [Theory]
    [InlineData("INSERT dbo.T VALUES (1);")]                          // named table — not a variable
    [InlineData("INSERT #tmp VALUES (1);")]                           // #temp — not a variable
    [InlineData("UPDATE dbo.T SET v = 1;")]
    [InlineData("SELECT k FROM @rows;")]                              // reads FROM the variable — not a write
    [InlineData("SET @rows = 1;")]                                    // not DML
    [InlineData("EXEC dbo.p @rows;")]                                 // passes it as an argument, not a write
    public void NonVariableWriteTarget_ReturnsNull(string sql) => Assert.Null(VariableTargetOf(sql));

    // ---- A62 F3: the COMPREHENSIVE write scan — direct target + alias-resolved target +
    // OUTPUT … INTO. TryGetTargetVariableName saw only the direct target, so a body that wrote a
    // READONLY TVP via an alias or an OUTPUT-INTO clause slipped past the step-into guard and ran a
    // write native compile-refuses (msg 10700). ----

    private static string[] WritesOf(string sql) =>
        DmlTargetClassifier.GetWrittenTableVariableNames(ParseTestHelper.ParseSingle(sql)).ToArray();

    [Theory]
    [InlineData("INSERT @rows VALUES (1);")]                                        // direct target
    [InlineData("UPDATE @rows SET v = 1;")]
    [InlineData("DELETE @rows;")]
    [InlineData("MERGE @rows AS t USING dbo.S AS s ON t.k = s.k WHEN MATCHED THEN UPDATE SET v = s.v;")]
    [InlineData("UPDATE x SET v = 99 FROM @rows AS x;")]                            // aliased target (F3 P1)
    [InlineData("UPDATE x SET v = 99 FROM @rows x;")]                               // aliased target, no AS
    [InlineData("DELETE x FROM @rows AS x WHERE x.k = 1;")]                         // aliased DELETE target
    [InlineData("UPDATE X SET v = 99 FROM @rows AS x;")]                            // alias match is case-insensitive
    public void WriteToVariable_IsReported(string sql) => Assert.Contains("@rows", WritesOf(sql));

    [Fact]
    public void OutputInto_ReportsTheIntoVariable() =>
        // The direct target (@loc) AND the OUTPUT INTO target (@rows) are both written (F3 P1c).
        Assert.Equal(new[] { "@loc", "@rows" }, WritesOf("DELETE @loc OUTPUT deleted.v INTO @rows;"));

    [Fact]
    public void AliasedTarget_JoinedVariableRead_IsNotReported() =>
        // Only the aliased target (@rows) is written; @other is READ in the join — must not appear.
        Assert.Equal(new[] { "@rows" },
            WritesOf("UPDATE x SET x.v = o.v FROM @rows AS x JOIN @other AS o ON x.k = o.k;"));

    [Theory]
    [InlineData("INSERT INTO #other SELECT k FROM @rows;")]                         // reads @rows, writes #other
    [InlineData("SELECT k FROM @rows;")]
    [InlineData("UPDATE dbo.T SET v = 1 FROM @rows AS x WHERE dbo.T.k = x.k;")]     // writes real table T, reads @rows
    [InlineData("SET @rows = 1;")]
    public void NoVariableWrite_IsEmpty(string sql) => Assert.Empty(WritesOf(sql));
}
