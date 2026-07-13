// DESIGN §7.4 / A26 (D1) — the SCOPE_IDENTITY() chain-sync fix driven end-to-end through
// Session.StepAsync with a fake executor: the full §1.3 trajectory matrix. The observable
// is the R6 shadow literal spliced into a SCOPE_IDENTITY()-reading SU's composed batch
// (`@__dbg{nonce}_sh_scopeid numeric(38,0) = <value>`), which is exactly what the debuggee
// reads. Engine truths: facts 26d/26e/31a/31b/31d.
using TsqlDbg.Core.Execution;
using TsqlDbg.Core.Sessions;
using TsqlDbg.Core.Tests.Fakes;
using Xunit;

namespace TsqlDbg.Core.Tests.Sessions;

public sealed class SessionD1ChainSyncTests
{
    private static BatchResult Ok(
        decimal? scopeId = null, int trancount = 1, int xactState = 0,
        IReadOnlyList<ResultSet>? userSets = null, object?[]? state = null)
    {
        var sets = new List<ResultSet>(userSets ?? Array.Empty<ResultSet>())
        {
            new(new[] { "__dbg_ctl", "ok", "rc", "scope_identity", "trancount", "xact_state" },
                new IReadOnlyList<object?>[] { new object?[] { 1, true, 1, scopeId, trancount, xactState } }),
        };
        if (state is not null)
        {
            sets.Add(StateSet(state));
        }

        return new BatchResult(sets, Array.Empty<string>());
    }

    private static BatchResult Fault(int errNumber, string errMessage, int xactState, int trancount = 1)
        => new(new List<ResultSet>
        {
            new(new[] { "__dbg_ctl", "ok", "err_number", "err_severity", "err_state", "err_line", "err_procedure", "err_message", "trancount", "xact_state" },
                new IReadOnlyList<object?>[] { new object?[] { 1, false, errNumber, 16, 1, 9, null, errMessage, trancount, xactState } }),
        }, Array.Empty<string>());

    private static ResultSet StateSet(object?[] values)
    {
        var columns = new string[values.Length + 1];
        var row = new object?[values.Length + 1];
        columns[0] = "__dbg_state";
        row[0] = 1;
        for (var i = 0; i < values.Length; i++)
        {
            columns[i + 1] = $"c{i}";
            row[i + 1] = values[i];
        }

        return new ResultSet(columns, new IReadOnlyList<object?>[] { row });
    }

    private static ResultSet Scalar(string column, object? value)
        => new(new[] { column }, new IReadOnlyList<object?>[] { new object?[] { value } });

    private static BatchResult Row(params object?[] values) => new(
        new[]
        {
            new ResultSet(
                Enumerable.Range(0, values.Length).Select(i => $"c{i}").ToArray(),
                new IReadOnlyList<object?>[] { values }),
        },
        Array.Empty<string>());

    private static BatchResult ModuleRow(string definition) => new(
        new[]
        {
            new ResultSet(new[] { "def", "qi", "ansi_nulls", "schema_name", "name" },
                new IReadOnlyList<object?>[] { new object?[] { definition, true, true, "dbo", "child" } }),
        },
        Array.Empty<string>());

    private static Session ScriptSession(string script, FakeStatementExecutor executor)
        => new(new SessionOptions("DEVSQL01", "SalesDb", LaunchMode.Script, null, null, script), executor);

    private static FakeStatementExecutor Init(FakeStatementExecutor executor)
        => executor.ThenEmpty().ThenEmpty().ThenEmpty().ThenEmpty();   // SET opts, CREATE s0, seed, BEGIN TRAN

    private static string Scopeid(int value) => $"_sh_scopeid numeric(38,0) = {value}";
    private const string ScopeidNull = "_sh_scopeid numeric(38,0) = NULL";

    // ---- F3-1b + F3-1: push nulls the shadow (callee entry reads NULL), pop restores it
    // and poisons the chain, so the caller's post-pop read serves its OWN restored value
    // (not the callee's leaked server value); a completed insert-family SU then clears the
    // flag and the next read serves the fresh capture. ----
    [Fact]
    public async Task PushNullsShadow_PopRestoresAndPoisons_PostPopReadSkipsCapture_InsertFamilyClears()
    {
        const string script = """
            DECLARE @x int = 0, @s int;
            INSERT dbo.T VALUES (@x);
            EXEC dbo.child @a = @x;
            SET @s = SCOPE_IDENTITY();
            INSERT dbo.T VALUES (@x);
            SET @s = SCOPE_IDENTITY();
            """;
        const string calleeDef = """
            CREATE PROCEDURE dbo.child @a int AS
            BEGIN
            SELECT SCOPE_IDENTITY() AS si;
            END
            """;
        var executor = Init(new FakeStatementExecutor())
            .Then(_ => Ok(state: new object?[] { 0, null }))               // DECLARE @x = 0
            .Then(_ => Ok(scopeId: 5, state: new object?[] { 0, null }))   // INSERT → chain established at 5
            .ThenEmpty();                                                  // C2: sys.triggers lookup (dbo.T; cached — INSERT #2 below is free)
        QueuePush(executor, calleeDef, 0)
            .Then(_ => Ok(scopeId: 7))                                     // callee SELECT SCOPE_IDENTITY() — reads NULL client-side
            .ThenEmpty()                                                   // pop cleanup (DROP s1)
            .Then(_ => Ok(scopeId: 99, state: new object?[] { 0, null }))  // caller SET @s — server leaked 99 (ignored)
            .Then(_ => Ok(scopeId: 8, state: new object?[] { 0, null }))   // caller INSERT → re-syncs, chain = 8
            .Then(_ => Ok(scopeId: 8, state: new object?[] { 0, 8 }));     // caller SET @s — serves the fresh 8
        var session = ScriptSession(script, executor);
        await session.InitializeAsync();

        await session.StepAsync();                                         // DECLARE
        await session.StepAsync();                                         // INSERT (chain = 5)

        await session.StepAsync(StepKind.Into);                           // EXEC → push (shadow nulled)
        Assert.Equal(2, session.Frames.Count);

        await session.StepAsync();                                         // callee SELECT SCOPE_IDENTITY() → body end → PARKS at implicit return (A54)
        Assert.Equal(2, session.Frames.Count);                             // A54: parked, not yet popped
        Assert.True(session.AtImplicitReturn);

        await session.StepAsync();                                         // consume the park → completed pop (DROP s1)
        Assert.Single(session.Frames);
        Assert.False(session.AtImplicitReturn);

        await session.StepAsync();                                         // caller SET @s = SCOPE_IDENTITY() (post-pop)
        await session.StepAsync();                                         // caller INSERT → insert-family, clears the flag
        await session.StepAsync();                                         // caller SET @s = SCOPE_IDENTITY() (post-insert)

        // Only SCOPE_IDENTITY()-reading SUs splice the R6 shadow DECLARE; in order:
        // callee entry, caller post-pop, caller post-insert. The spliced literal is
        // exactly what the debuggee reads.
        var scopeidReads = executor.ReceivedBatches.Where(b => b.Contains("_sh_scopeid numeric")).ToList();
        Assert.Equal(3, scopeidReads.Count);
        Assert.Contains(ScopeidNull, scopeidReads[0]);                     // F3-1b: callee entry reads NULL (push nulled the shadow)
        Assert.Contains(Scopeid(5), scopeidReads[1]);                      // post-pop: RESTORED 5, not the callee's leaked 99 (skip)
        Assert.Contains(Scopeid(8), scopeidReads[2]);                      // insert-family cleared the flag: fresh capture 8

        await session.TeardownAsync();
    }

    // §11.3 push traffic for a one-arg callee (mirrors SessionFramesM4Tests.QueuePush):
    // module fetch, arg eval (scalar), CREATE s{n}, seed INSERT, parameterized reseed,
    // SELECT * read-back.
    private static FakeStatementExecutor QueuePush(FakeStatementExecutor executor, string calleeDef, object? argValue)
        => executor
            .Then(_ => ModuleRow(calleeDef))
            .Then(_ => Ok(userSets: new[] { Scalar("p", argValue) }))
            .ThenEmpty()
            .ThenEmpty()
            .ThenEmpty()
            .Then(_ => Row(argValue));

    // ---- F3-2: while doomed the debuggee batches ride an sp_executesql child scope
    // (fact 26e) — the capture is skipped and the shadow keeps its pre-doom value; on the
    // A9 resurrection edge the flag stays set so the kept value survives (fact 26d
    // rollback-neutral). ----
    [Fact]
    public async Task Doomed_ReadServesPreDoomValue_CaptureSkipped()
    {
        const string script = """
            DECLARE @s int;
            BEGIN TRY
            INSERT dbo.T VALUES (1);
            SELECT 1 / 0 AS a;
            END TRY
            BEGIN CATCH
            SET @s = SCOPE_IDENTITY();
            END CATCH
            """;
        var executor = Init(new FakeStatementExecutor())
            .Then(_ => Ok(scopeId: 5))                                     // INSERT → chain = 5
            .ThenEmpty()                                                   // C2: sys.triggers lookup (dbo.T)
            .Then(_ => Fault(8134, "Divide by zero error encountered.", xactState: -1))  // dooms + routes
            .Then(_ => Ok(scopeId: 99, xactState: -1));                    // doomed CATCH SET @s — server leaked 99
        var session = ScriptSession(script, executor);
        await session.InitializeAsync();

        await session.StepAsync();                                         // DECLARE
        await session.StepAsync();                                         // INSERT (chain = 5)

        await session.StepAsync();                                         // SELECT 1/0 → doomed, routes to CATCH
        Assert.True(session.IsDoomed);

        await session.StepAsync();                                         // doomed CATCH SET @s = SCOPE_IDENTITY()
        Assert.Contains(Scopeid(5), executor.ReceivedBatches[^1]);         // serves the PRE-DOOM 5 (capture skipped)

        await session.TeardownAsync();
    }
}
