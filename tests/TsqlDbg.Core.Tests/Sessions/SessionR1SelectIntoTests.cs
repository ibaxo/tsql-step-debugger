// M6 R1 (Sonnet, design note §5-R1, A24): SELECT ... INTO #x is a create site under
// exactly the same A20/E1 collision predicate as CREATE TABLE #x — mirrors
// SessionFramesM4Tests' TempTableLifecycle_RenamesRegistersAndMarksDead /
// TempTableCollision_CalleeCreateRenames_CallerStaysOriginal, swapping CREATE TABLE
// for SELECT ... INTO as the create statement.
using TsqlDbg.Core.Execution;
using TsqlDbg.Core.Interpreter;
using TsqlDbg.Core.Sessions;
using TsqlDbg.Core.Tests.Fakes;
using Xunit;

namespace TsqlDbg.Core.Tests.Sessions;

public sealed class SessionR1SelectIntoTests
{
    private static BatchResult Ok(
        int? rc = null, int trancount = 1, int xactState = 0,
        IReadOnlyList<ResultSet>? userSets = null, object?[]? state = null)
    {
        var sets = new List<ResultSet>(userSets ?? Array.Empty<ResultSet>())
        {
            new(new[] { "__dbg_ctl", "ok", "rc", "scope_identity", "trancount", "xact_state" },
                new IReadOnlyList<object?>[] { new object?[] { 1, true, rc, null, trancount, xactState } }),
        };
        if (state is not null)
        {
            sets.Add(StateSet(state));
        }

        return new BatchResult(sets, Array.Empty<string>());
    }

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

    private static FakeStatementExecutor QueuePush(FakeStatementExecutor executor, string calleeDef, object? argValue)
        => executor
            .Then(_ => ModuleRow(calleeDef))
            .Then(_ => Ok(userSets: new[] { Scalar("p", argValue) }))
            .ThenEmpty()
            .ThenEmpty()
            .ThenEmpty()
            .Then(_ => Row(argValue));

    [Fact]
    public async Task SelectInto_NonColliding_KeepsOriginalName_RegistersEntry()
    {
        const string script = """
            SELECT 1 AS a INTO #w;
            INSERT INTO #w VALUES (2);
            DROP TABLE #w;
            SELECT 1 AS x;
            """;
        var executor = Init(new FakeStatementExecutor())
            .Then(_ => Ok()).Then(_ => Ok()).Then(_ => Ok());
        var session = ScriptSession(script, executor);
        await session.InitializeAsync();

        await session.StepAsync();                                     // SELECT ... INTO #w
        Assert.Contains("SELECT 1 AS a INTO #w;", executor.ReceivedBatches[^1]);
        Assert.DoesNotContain("__f0", executor.ReceivedBatches[^1].Split('\n').First(l => l.Contains("INTO #w")));
        var entry = Assert.Single(session.TopFrame!.TempObjects.All);
        Assert.Equal("#w", entry.OriginalName);
        Assert.Equal("#w", entry.PhysicalName);
        Assert.Equal(TempObjectKind.TempTable, entry.Kind);
        Assert.False(entry.IsDead);
        Assert.Equal(1, entry.CreatedAtTrancount);

        await session.StepAsync();                                     // INSERT — resolves to itself, unpatched
        Assert.Contains("INSERT INTO #w VALUES (2);", executor.ReceivedBatches[^1]);

        await session.StepAsync();                                     // DROP — unpatched AND marks dead
        Assert.Contains("DROP TABLE #w;", executor.ReceivedBatches[^1]);
        Assert.True(entry.IsDead);
        await session.TeardownAsync();
    }

    // A20/E1's collision arm, exercised through SELECT INTO instead of CREATE TABLE: a
    // stepped-into callee's SELECT INTO #w colliding with the caller's live #w gets the
    // minted rename; the caller's own references stay original.
    [Fact]
    public async Task SelectInto_Collision_CalleeRenames_CallerStaysOriginal()
    {
        const string script = """
            CREATE TABLE #w (a int);
            EXEC dbo.child @a = 2;
            INSERT INTO #w VALUES (1);
            """;
        const string calleeDef = """
            CREATE PROCEDURE dbo.child @a int AS
            BEGIN
            SELECT @a AS b INTO #w;
            INSERT INTO #w VALUES (@a);
            RETURN 0;
            END
            """;
        var executor = Init(new FakeStatementExecutor())
            .Then(_ => Ok());                                          // caller CREATE #w
        QueuePush(executor, calleeDef, 2)
            .Then(_ => Ok())                                           // callee SELECT INTO #w → renamed
            .Then(_ => Ok())                                           // callee INSERT → renamed ref
            .Then(_ => Ok(userSets: new[] { Scalar("p", 0) }))         // RETURN 0 eval
            .ThenEmpty()                                               // pop: cleanup (drop s1 + callee #w__f1)
            .Then(_ => Ok());                                          // caller INSERT — original ref
        var session = ScriptSession(script, executor);
        await session.InitializeAsync();

        await session.StepAsync();                                     // caller CREATE — original name
        Assert.Equal("#w", Assert.Single(session.TopFrame!.TempObjects.All).PhysicalName);

        await session.StepAsync(StepKind.Into);                        // push
        var calleeOrdinal = session.TopFrame!.Ordinal;

        await session.StepAsync();                                     // callee SELECT INTO — COLLISION → renamed
        Assert.Contains($"SELECT @a AS b INTO [#w__f{calleeOrdinal}]", executor.ReceivedBatches[^1]);
        var calleeEntry = Assert.Single(session.TopFrame.TempObjects.All);
        Assert.Equal("#w", calleeEntry.OriginalName);
        Assert.Equal($"#w__f{calleeOrdinal}", calleeEntry.PhysicalName);
        Assert.Equal(TempObjectKind.TempTable, calleeEntry.Kind);

        await session.StepAsync();                                     // callee INSERT — innermost wins, patched
        Assert.Contains($"INSERT INTO [#w__f{calleeOrdinal}]", executor.ReceivedBatches[^1]);

        await session.StepAsync();                                     // RETURN → pop
        Assert.Single(session.Frames);

        await session.StepAsync();                                     // caller INSERT — unpatched, original #w
        Assert.Contains("INSERT INTO #w VALUES (1);", executor.ReceivedBatches[^1]);
        await session.TeardownAsync();
    }
}
