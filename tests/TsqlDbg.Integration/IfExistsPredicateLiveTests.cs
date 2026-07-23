using Microsoft.Data.SqlClient;
using TsqlDbg.Core.Sessions;
using TsqlDbg.Core.Targets;
using Xunit;

namespace TsqlDbg.Integration;

// GitHub #2: `IF EXISTS (...)` faulted under the debugger with Msg 4145 ("An expression of
// non-boolean type ... near 'THEN'"), though the script runs fine natively. Root cause:
// ScriptDom sets an ExistsPredicate's StartOffset to its subquery's '(' — the leading EXISTS
// keyword is EXCLUDED from the fragment span — so the §6 predicate shell
// `SELECT CASE WHEN <slice> THEN 1 ELSE 0 END` degenerated to `... WHEN (SELECT ...) THEN ...`,
// a scalar subquery where a boolean is required. The omission bubbles up to any predicate
// whose LEFTMOST leaf is a bare EXISTS. ComposedBatchBuilder.BuildForPredicate now recovers
// the keyword; these live cases prove the fix end-to-end (under the bug RunToEndAsync throws
// SessionFaultException on the 4145) and guard the unaffected shapes against a spurious EXISTS.
public sealed class IfExistsPredicateLiveTests
{
    private const string ConnEnvVar = "TSQLDBG_TEST_CONN";

    // Each script takes the THEN or ELSE branch and SELECTs a single marker column so the
    // taken branch is observable in the result set (the predicate shells themselves never
    // surface). A row in #t makes EXISTS true; an empty #t makes it false.
    [SkippableTheory]
    // Bare leading EXISTS (the reported repro shape) — true and false.
    [InlineData("CREATE TABLE #t (id int); INSERT INTO #t (id) VALUES (1);\n"
        + "IF EXISTS (SELECT 1 FROM #t) SELECT 'then' AS branch; ELSE SELECT 'else' AS branch;", "then")]
    [InlineData("CREATE TABLE #t (id int);\n"
        + "IF EXISTS (SELECT 1 FROM #t) SELECT 'then' AS branch; ELSE SELECT 'else' AS branch;", "else")]
    // Leftmost EXISTS in a compound predicate — the keyword drop bubbles up through the AND.
    [InlineData("CREATE TABLE #t (id int); INSERT INTO #t (id) VALUES (1); DECLARE @x int = 1;\n"
        + "IF EXISTS (SELECT 1 FROM #t) AND @x = 1 SELECT 'then' AS branch; ELSE SELECT 'else' AS branch;", "then")]
    [InlineData("CREATE TABLE #t (id int); INSERT INTO #t (id) VALUES (1);\n"
        + "IF EXISTS (SELECT 1 FROM #t) OR EXISTS (SELECT 1 WHERE 1 = 0) SELECT 'then' AS branch; ELSE SELECT 'else' AS branch;", "then")]
    // Unaffected shapes (ScriptDom keeps their leading token) — must still be correct.
    [InlineData("CREATE TABLE #t (id int); INSERT INTO #t (id) VALUES (1); DECLARE @x int = 1;\n"
        + "IF @x = 1 AND EXISTS (SELECT 1 FROM #t) SELECT 'then' AS branch; ELSE SELECT 'else' AS branch;", "then")]
    [InlineData("CREATE TABLE #t (id int);\n"
        + "IF NOT EXISTS (SELECT 1 FROM #t) SELECT 'then' AS branch; ELSE SELECT 'else' AS branch;", "then")]
    [InlineData("CREATE TABLE #t (id int); INSERT INTO #t (id) VALUES (1);\n"
        + "IF (EXISTS (SELECT 1 FROM #t)) SELECT 'then' AS branch; ELSE SELECT 'else' AS branch;", "then")]
    public async Task IfExistsPredicate_TakesCorrectBranch_WithoutMsg4145(string script, string expectedBranch)
    {
        var rawConnectionString = Environment.GetEnvironmentVariable(ConnEnvVar);
        Skip.If(
            string.IsNullOrWhiteSpace(rawConnectionString),
            $"{ConnEnvVar} is not set; skipping live test (never fake a pass — CLAUDE.md).");

        var csb = new SqlConnectionStringBuilder(rawConnectionString);
        var target = new TargetEntry(csb.DataSource, "test", AllowWrites: false, Options: null);
        var options = new SessionOptions(
            csb.DataSource, csb.InitialCatalog, LaunchMode.Script, null, null, script);

        // Under the bug this throws SessionFaultException (unhandled 4145) before returning.
        var result = await SessionHost.RunAsync(options, target);

        var resultSet = Assert.Single(result.Execution.ResultSets);
        var row = Assert.Single(resultSet.Rows);
        Assert.Equal(expectedBranch, (string)row[0]!);
    }
}
