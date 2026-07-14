using Microsoft.Data.SqlClient;
using TsqlDbg.Core.Sessions;
using TsqlDbg.Core.Targets;
using Xunit;

namespace TsqlDbg.Integration;

// A59, second pass — the holes an independent review found in the first one, each reproduced
// LIVE before it was fixed. Every one of them was invisible to the existing suite: p29/p30 pass
// a TVP only from an EXEC unit, create their types directly, live in dbo, and are clustered on
// their identity column. That is the shape of a fixture written by the person who wrote the
// code, and it is why these tests exist.
public sealed class UserTypeReviewFixLiveTests
{
    private const string ConnEnvVar = "TSQLDBG_TEST_CONN";

    // A table type + the two function shapes that take one: a scalar UDF and a TVF. Both take
    // the TVP as a scalar VariableReference at the call site, which is the whole point.
    private const string Setup = @"
IF TYPE_ID('dbo.rv_Rows') IS NULL EXEC('CREATE TYPE dbo.rv_Rows AS TABLE (id int IDENTITY(1,1), nm nvarchar(30) NOT NULL)');
IF TYPE_ID('dbo.rv_Ord') IS NULL EXEC('CREATE TYPE dbo.rv_Ord AS TABLE (id int IDENTITY(1,1), k nvarchar(10) NOT NULL, PRIMARY KEY CLUSTERED (k))');
IF TYPE_ID('dbo.rv_Sql') IS NULL EXEC('CREATE TYPE dbo.rv_Sql FROM nvarchar(200)');
IF SCHEMA_ID('rvs') IS NULL EXEC('CREATE SCHEMA rvs');
IF TYPE_ID('rvs.rv_Bare') IS NULL EXEC('CREATE TYPE rvs.rv_Bare AS TABLE (c int)');
GO
CREATE OR ALTER FUNCTION dbo.rv_cnt(@t dbo.rv_Rows READONLY) RETURNS int
AS BEGIN RETURN (SELECT COUNT(*) FROM @t); END
GO
CREATE OR ALTER FUNCTION dbo.rv_tvf(@t dbo.rv_Rows READONLY) RETURNS TABLE
AS RETURN (SELECT nm FROM @t);
GO
CREATE OR ALTER PROCEDURE dbo.rv_ret AS
BEGIN
    DECLARE @t dbo.rv_Rows;
    INSERT INTO @t (nm) VALUES (N'a'), (N'b'), (N'c');
    RETURN dbo.rv_cnt(@t);
END
GO
CREATE OR ALTER PROCEDURE dbo.rv_replay @t dbo.rv_Ord READONLY AS
BEGIN
    SELECT STRING_AGG(CONCAT(id, ':', k), ',') WITHIN GROUP (ORDER BY id) AS Replay FROM @t;
END
GO
";

    // ---- F1: a table-type variable referenced as a SCALAR outside an EXEC argument. ---------
    // The first cut wired the §9 materialization into BuildForUnit and BuildForRepl only. Every
    // other composed-batch shape emitted a batch that REFERENCED @t without DECLARing it →
    // error 137, "Must declare the scalar variable @t", which is batch-aborting: the session
    // dies. `SET @n = dbo.rv_cnt(@t)` worked (BuildForUnit) and these did not.

    [SkippableTheory]
    // The DECLARE initializer — a synthetic SET (BuildSyntheticAssignment).
    [InlineData("DECLARE @n int = dbo.rv_cnt(@t);\nSELECT @n AS N;", 2)]
    // The IF predicate (BuildForPredicate).
    [InlineData("DECLARE @n int = 0;\nIF dbo.rv_cnt(@t) > 1\n    SET @n = 99;\nSELECT @n AS N;", 99)]
    // A table-valued function in a FROM clause — an ordinary DML unit, but the argument is
    // still a scalar reference.
    [InlineData("SELECT COUNT(*) AS N FROM dbo.rv_tvf(@t);", 2)]
    public async Task TableTypeVariable_PassedAsATvpArgument_WorksInEveryStatementShape(
        string tail, int expected)
    {
        var connectionString = Require();
        await CorpusDeployer.DeployScriptAsync(connectionString, Setup);

        var script = "DECLARE @t dbo.rv_Rows;\nINSERT INTO @t (nm) VALUES (N'a'), (N'b');\n" + tail;

        Assert.Equal(expected, Convert.ToInt32(await RunScriptAsync(connectionString, script)));
        Assert.Equal(expected, Convert.ToInt32(await RunNativeAsync(connectionString, script)));
    }

    // The fourth shape: a RETURN expression (BuildForScalarEval), reachable only by stepping
    // INTO the procedure that returns it. The callee declares the table-type variable itself,
    // so this also exercises a table-type realization on a pushed frame.
    [SkippableFact]
    public async Task TableTypeVariable_InAReturnExpression_WorksWhenSteppedInto()
    {
        var connectionString = Require();
        await CorpusDeployer.DeployScriptAsync(connectionString, Setup);

        const string Script = "DECLARE @rc int;\nEXEC @rc = dbo.rv_ret;\nSELECT @rc AS N;";

        Assert.Equal(3, Convert.ToInt32(await RunScriptAsync(connectionString, Script, StepKind.Into)));
        Assert.Equal(3, Convert.ToInt32(await RunNativeAsync(connectionString, Script)));
    }

    // ---- F2: the type catalog went stale, and the raw 2715 came back. -----------------------
    // The refresh fired only on a DIRECTLY executed CREATE TYPE. But CREATE TYPE cannot sit
    // under an IF, so `IF TYPE_ID(…) IS NULL EXEC('CREATE TYPE …')` is THE conditional-create
    // idiom — and after it, a later batch declaring the type died with "Cannot find data type",
    // exactly the failure A59 exists to fix. (The catalog is now re-read whenever a frame is
    // about to resolve a named type, so it cannot go stale no matter who created it.)
    [SkippableFact]
    public async Task TypeCreatedByDynamicSql_IsDeclarableInALaterBatch()
    {
        var connectionString = Require();
        await ExecuteAsync(connectionString, "IF TYPE_ID('dbo.rv_Dyn') IS NOT NULL DROP TYPE dbo.rv_Dyn;");

        var value = await RunScriptAsync(connectionString, @"
IF TYPE_ID('dbo.rv_Dyn') IS NULL EXEC('CREATE TYPE dbo.rv_Dyn FROM decimal(6,3)');
GO
DECLARE @v dbo.rv_Dyn = 1.5;
SET @v = @v * 3;
SELECT @v AS V;");

        Assert.Equal(4.500m, (decimal)value!);

        // Created inside the safety transaction, so teardown rolled it back — no residue.
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var check = connection.CreateCommand();
        check.CommandText = "SELECT TYPE_ID('dbo.rv_Dyn');";
        Assert.IsType<DBNull>(await check.ExecuteScalarAsync());
    }

    // ---- F3: bare-name resolution must be the engine's, not a superset of it. ---------------
    // Native resolves an unqualified type name through the default schema, then dbo — and
    // NOTHING else: a type in some third schema is msg 2715 even when its name is unique in the
    // database (probed). The first cut resolved a bare name to any schema that held it uniquely,
    // so the debugger RAN a script the engine refuses — and, for a table type, silently realized
    // a structure the debuggee could never have declared.
    [SkippableFact]
    public async Task BareNamedType_InSomeThirdSchema_IsRefusedExactlyAsTheEngineRefusesIt()
    {
        var connectionString = Require();
        await CorpusDeployer.DeployScriptAsync(connectionString, Setup);

        const string Script = "DECLARE @t rv_Bare;\nSELECT COUNT(*) AS N FROM @t;";

        // Native: msg 2715, "Column, parameter, or variable #1: Cannot find data type rv_Bare".
        var native = await Assert.ThrowsAsync<SqlException>(
            () => RunNativeAsync(connectionString, Script));
        Assert.Equal(2715, native.Number);

        // The debugger must fail too. (It reaches the server as the unresolved named type it
        // is, so the message is the server's own — the debugger invents nothing.)
        var debugged = await Assert.ThrowsAnyAsync<Exception>(
            () => RunScriptAsync(connectionString, Script));
        Assert.Contains("rv_Bare", debugged.Message);
    }

    // ---- The materialization moves the identity chain (fact 34h). ---------------------------
    // Probed: an INSERT into a table variable with an IDENTITY column overwrites SCOPE_IDENTITY()
    // — it took a real table's 100 down to the table variable's 2. The §9 preamble does exactly
    // that INSERT, and the post-statement capture feeds the R6 shadow, so a debuggee reading
    // SCOPE_IDENTITY() after passing a TVP saw the DEBUGGER's bookkeeping insert. Nothing in the
    // suite could see it: no fixture reads SCOPE_IDENTITY() after a TVP argument.
    [SkippableFact]
    public async Task ScopeIdentity_AfterATvpArgument_ReadsTheDebuggeesInsert_NotTheDebuggersMaterialization()
    {
        var connectionString = Require();
        await CorpusDeployer.DeployScriptAsync(connectionString, Setup);

        // A #temp, not a permanent table: identity values are NOT rolled back (they are
        // non-transactional), so a permanent table would hand each run a different number and
        // the two sides could never be compared absolutely.
        // `SET @n = …`, deliberately NOT a DECLARE initializer: this test must fail for the
        // identity-chain reason alone, not because of F1 above.
        const string Script = @"
CREATE TABLE #real (id int IDENTITY(100,1) PRIMARY KEY, v int NULL);
DECLARE @t dbo.rv_Rows;
DECLARE @n int;
INSERT INTO @t (nm) VALUES (N'a'), (N'b');
INSERT #real (v) VALUES (1);
SET @n = dbo.rv_cnt(@t);
SELECT CONVERT(int, SCOPE_IDENTITY()) AS SI, @n AS N;";

        var native = await RunNativeRowAsync(connectionString, Script);
        var debugged = await RunScriptRowAsync(connectionString, Script);

        Assert.Equal(native[0], debugged[0]);
        Assert.Equal(native[1], debugged[1]);

        // Pinned absolutely, so a mutually-wrong pair cannot pass: the #temp's identity (100),
        // never the table variable's 2.
        Assert.Equal(100, Convert.ToInt32(debugged[0]));
        Assert.Equal(2, Convert.ToInt32(debugged[1]));
    }

    // ---- F6: the ORDER BY that makes C28's promise a guarantee. -----------------------------
    // Identity is assigned in INSERT order, and INSERT…SELECT only fixes that order under an
    // explicit ORDER BY. dbo.rv_Ord is clustered on `k`, NOT on its identity column — so the
    // realization scans in k-order (a, b, c) and the materialization re-generated identities in
    // THAT order, silently permuting which id belonged to which row. p30 could never catch this:
    // its clustered PK IS its identity column.
    [SkippableFact]
    public async Task TvpMaterialization_ReplaysIdentityValuesInTheRealizationsOwnOrder()
    {
        var connectionString = Require();
        await CorpusDeployer.DeployScriptAsync(connectionString, Setup);

        const string Script = @"
DECLARE @t dbo.rv_Ord;
INSERT INTO @t (k) VALUES (N'c'), (N'a'), (N'b');
EXEC dbo.rv_replay @t = @t;";

        var native = await RunNativeAsync(connectionString, Script);
        var debugged = await RunScriptAsync(connectionString, Script);

        Assert.Equal(native, debugged);
        Assert.Equal("1:c,2:a,3:b", debugged);   // insertion order, not clustered order
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

    private static async Task<object?> RunScriptAsync(
        string connectionString, string scriptText, StepKind stepKind = StepKind.Over)
        => (await RunScriptRowAsync(connectionString, scriptText, stepKind))[0];

    private static async Task<IReadOnlyList<object?>> RunScriptRowAsync(
        string connectionString, string scriptText, StepKind stepKind = StepKind.Over)
    {
        var csb = new SqlConnectionStringBuilder(connectionString);
        var target = new TargetEntry(csb.DataSource, "test", AllowWrites: false, Options: null);
        var options = new SessionOptions(
            csb.DataSource, csb.InitialCatalog, LaunchMode.Script,
            Procedure: null, Args: null, ScriptText: scriptText);

        var result = await SessionHost.RunAsync(options, target, stepKind: stepKind);
        return Assert.Single(Assert.Single(result.Execution.ResultSets).Rows);
    }

    /// <summary>The same script natively, in a rolled-back transaction — the fidelity oracle.</summary>
    private static async Task<object?> RunNativeAsync(string connectionString, string scriptText)
        => (await RunNativeRowAsync(connectionString, scriptText))[0];

    private static async Task<IReadOnlyList<object?>> RunNativeRowAsync(string connectionString, string scriptText)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var tran = (SqlTransaction)await connection.BeginTransactionAsync();
        try
        {
            var batches = scriptText.Split("\nGO\n", StringSplitOptions.RemoveEmptyEntries);
            var row = Array.Empty<object?>();
            foreach (var batch in batches)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = tran;
                command.CommandText = batch;
                await using var reader = await command.ExecuteReaderAsync();
                while (true)
                {
                    if (await reader.ReadAsync())
                    {
                        row = new object?[reader.FieldCount];
                        for (var i = 0; i < reader.FieldCount; i++)
                        {
                            row[i] = await reader.IsDBNullAsync(i) ? null : reader.GetValue(i);
                        }
                    }

                    if (!await reader.NextResultAsync())
                    {
                        break;
                    }
                }
            }

            return row;
        }
        finally
        {
            await tran.RollbackAsync();
        }
    }
}
