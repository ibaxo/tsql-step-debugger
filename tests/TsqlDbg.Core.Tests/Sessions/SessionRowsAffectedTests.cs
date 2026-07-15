// §12.3 / C5: the debugger forces SET NOCOUNT ON, which suppresses the engine's native
// "(N rows affected)" done-token messages. The session re-synthesizes that line for the
// row-affecting DML family (INSERT/UPDATE/DELETE/MERGE + SELECT … INTO) from the control
// row's captured @@ROWCOUNT (Session.AppendRowsAffectedNote). A plain SELECT is excluded —
// its row count is conveyed by the rendered result-set table (A50). The REPL half of the
// same feature lives in SessionReplTests (Dml_Write_EmitsRowsAffected).
using TsqlDbg.Core.Execution;
using TsqlDbg.Core.Sessions;
using TsqlDbg.Core.Tests.Fakes;
using Xunit;

namespace TsqlDbg.Core.Tests.Sessions;

public sealed class SessionRowsAffectedTests
{
    private static BatchResult Ok(int? rc = null, int trancount = 1, int xactState = 0,
        IReadOnlyList<ResultSet>? userSets = null)
    {
        var sets = new List<ResultSet>(userSets ?? Array.Empty<ResultSet>())
        {
            new(new[] { "__dbg_ctl", "ok", "rc", "scope_identity", "trancount", "xact_state" },
                new IReadOnlyList<object?>[] { new object?[] { 1, true, rc, null, trancount, xactState } }),
        };
        return new BatchResult(sets, Array.Empty<string>());
    }

    private static ResultSet Scalar(string column, object? value)
        => new(new[] { column }, new IReadOnlyList<object?>[] { new object?[] { value } });

    private static Session ScriptSession(string script, FakeStatementExecutor executor)
        => new(new SessionOptions("DEVSQL01", "SalesDb", LaunchMode.Script, null, null, script), executor);

    private static FakeStatementExecutor Init(FakeStatementExecutor executor)
        => executor.ThenEmpty().ThenEmpty().ThenEmpty().ThenEmpty();   // SET opts, CREATE s0, seed, BEGIN TRAN

    [Theory]
    [InlineData("INSERT INTO dbo.T (a) VALUES (1);", 5, "(5 rows affected)")]
    [InlineData("UPDATE dbo.T SET a = 1 WHERE a = 0;", 1, "(1 row affected)")]   // singular
    [InlineData("DELETE FROM dbo.T WHERE a > 0;", 0, "(0 rows affected)")]        // zero is shown too
    public async Task Step_OverDml_EmitsRowsAffected(string dml, int rc, string expected)
    {
        var script = dml + "\nSELECT 1 AS done;";
        var executor = Init(new FakeStatementExecutor()).Then(_ => Ok(rc: rc));
        var session = ScriptSession(script, executor);
        await session.InitializeAsync();

        var (_, messages) = await session.StepAsync();   // the DML statement

        Assert.Contains(expected, messages);
        await session.TeardownAsync();
    }

    [Fact]
    public async Task Step_OverSelectInto_EmitsRowsAffected()
    {
        // SELECT … INTO is a bulk insert (creates + populates) — it reports rows affected
        // natively, so it is included despite being a SelectStatement.
        const string script = "SELECT 1 AS a INTO #w;\nSELECT 1 AS done;";
        var executor = Init(new FakeStatementExecutor()).Then(_ => Ok(rc: 4));
        var session = ScriptSession(script, executor);
        await session.InitializeAsync();

        var (_, messages) = await session.StepAsync();   // SELECT … INTO #w

        Assert.Contains("(4 rows affected)", messages);
        await session.TeardownAsync();
    }

    [Fact]
    public async Task Step_OverPlainSelect_EmitsNoRowsAffected()
    {
        // A plain SELECT's row count is shown by its result-set table (A50); a rows-affected
        // line would be redundant noise, so it is deliberately excluded.
        const string script = "SELECT 1 AS x;\nSELECT 1 AS done;";
        var executor = Init(new FakeStatementExecutor())
            .Then(_ => Ok(rc: 3, userSets: new[] { Scalar("x", 1) }));
        var session = ScriptSession(script, executor);
        await session.InitializeAsync();

        var (_, messages) = await session.StepAsync();   // plain SELECT

        Assert.DoesNotContain(messages, m => m.Contains("affected"));
        await session.TeardownAsync();
    }
}
