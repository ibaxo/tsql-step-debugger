// M6 S2 (Sonnet, design note §3): Session.TryGetModuleIndexAsync — the per-module
// breakpoint store's resolution primitive. A live frame's own index wins without any
// executor round trip; otherwise a side-effect-free OBJECT_DEFINITION fetch + parse,
// refused (null index, message set) on a broken session, an unresolvable definition,
// or a script identity that was never part of this session.
using TsqlDbg.Core.Execution;
using TsqlDbg.Core.Interpreter;
using TsqlDbg.Core.Sessions;
using TsqlDbg.Core.Tests.Fakes;
using Xunit;

namespace TsqlDbg.Core.Tests.Sessions;

public sealed class SessionM6ModuleIndexTests
{
    // ---- fake-batch helpers (shapes mirror ComposedBatchBuilder's real output) --------

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

    private static BatchResult Fault(
        int errNumber, string errMessage, int errSeverity = 16, int errState = 1, int? errLine = null,
        int trancount = 1, int xactState = 1, object?[]? state = null)
    {
        var sets = new List<ResultSet>
        {
            new(new[] { "__dbg_ctl", "ok", "err_number", "err_severity", "err_state", "err_line", "err_procedure", "err_message", "trancount", "xact_state" },
                new IReadOnlyList<object?>[] { new object?[] { 1, false, errNumber, errSeverity, errState, errLine, null, errMessage, trancount, xactState } }),
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

    // The step-into / blueprint-fetch module query's single row (§11.3/D4).
    private static BatchResult ModuleRow(string definition, string schema = "dbo", string name = "child") => new(
        new[]
        {
            new ResultSet(new[] { "def", "qi", "ansi_nulls", "schema_name", "name" },
                new IReadOnlyList<object?>[] { new object?[] { definition, true, true, schema, name } }),
        },
        Array.Empty<string>());

    private static Session ScriptSession(string script, FakeStatementExecutor executor)
        => new(new SessionOptions("DEVSQL01", "SalesDb", LaunchMode.Script, null, null, script), executor);

    private static FakeStatementExecutor Init(FakeStatementExecutor executor)
        => executor.ThenEmpty().ThenEmpty().ThenEmpty().ThenEmpty();   // SET opts, CREATE s0, seed, BEGIN TRAN

    [Fact]
    public async Task LiveFrame_ReturnsIndex_WithoutAnyExecutorCall()
    {
        const string calleeDef = "CREATE PROCEDURE dbo.child AS BEGIN SELECT 1; END";
        var executor = Init(new FakeStatementExecutor())
            .Then(_ => ModuleRow(calleeDef))    // step-into module fetch
            .ThenEmpty()                        // CREATE s1
            .ThenEmpty();                       // seed insert
        var session = ScriptSession("EXEC dbo.child;", executor);
        await session.InitializeAsync();
        await session.StepAsync(StepKind.Into);
        Assert.Equal(StepDisposition.SteppedIn, session.LastStep.Disposition);
        var callsBefore = executor.ReceivedBatches.Count;

        var (index, message) = await session.TryGetModuleIndexAsync(session.TopFrame!.Module);

        Assert.Null(message);
        Assert.NotNull(index);
        Assert.Equal(calleeDef, index!.FullScript);
        Assert.Equal(callsBefore, executor.ReceivedBatches.Count);
    }

    [Fact]
    public async Task NotLive_FetchesAndParsesBlueprint()
    {
        const string otherDef = "CREATE PROCEDURE dbo.other AS BEGIN SELECT 2; END";
        var executor = Init(new FakeStatementExecutor())
            .Then(_ => ModuleRow(otherDef, name: "other"));
        var session = ScriptSession("SELECT 1;", executor);
        await session.InitializeAsync();

        var (index, message) = await session.TryGetModuleIndexAsync(new ModuleIdentity("SalesDb", "dbo", "other", IsScript: false));

        Assert.Null(message);
        Assert.NotNull(index);
        Assert.Equal(otherDef, index!.FullScript);
        Assert.True(index.TryMapBreakpointLine(1, out var unit));
        Assert.Equal(SuSubKind.General, unit.SubKind);
    }

    [Fact]
    public async Task NotLive_NoDefinitionRow_ReturnsRefusalMessage()
    {
        var executor = Init(new FakeStatementExecutor())
            .ThenEmpty();                        // OBJECT_DEFINITION query: no row (missing/encrypted)
        var session = ScriptSession("SELECT 1;", executor);
        await session.InitializeAsync();

        var (index, message) = await session.TryGetModuleIndexAsync(new ModuleIdentity("SalesDb", "dbo", "ghost", IsScript: false));

        Assert.Null(index);
        Assert.NotNull(message);
    }

    [Fact]
    public async Task ScriptIdentity_NotLive_RefusesWithoutFetch()
    {
        var executor = Init(new FakeStatementExecutor());
        var session = ScriptSession("SELECT 1;", executor);
        await session.InitializeAsync();
        var callsBefore = executor.ReceivedBatches.Count;

        var (index, message) = await session.TryGetModuleIndexAsync(ModuleIdentity.Script("some-other-tab"));

        Assert.Null(index);
        Assert.NotNull(message);
        Assert.Equal(callsBefore, executor.ReceivedBatches.Count);
    }

    [Fact]
    public async Task BrokenSession_RefusesWithoutFetch()
    {
        const string script = """
            BEGIN TRY
                SELECT 1 / 0 AS a;
            END TRY
            BEGIN CATCH
                THROW;
            END CATCH
            """;
        var executor = Init(new FakeStatementExecutor())
            .Then(_ => Fault(8134, "Divide by zero error encountered."));
        var session = ScriptSession(script, executor);
        await session.InitializeAsync();
        await session.StepAsync();                                     // -> CATCH
        await session.StepAsync();                                     // bare THROW, no outer TRY -> broken
        Assert.True(session.IsBroken);
        var callsBefore = executor.ReceivedBatches.Count;

        var (index, message) = await session.TryGetModuleIndexAsync(new ModuleIdentity("SalesDb", "dbo", "other", IsScript: false));

        Assert.Null(index);
        Assert.NotNull(message);
        Assert.Equal(callsBefore, executor.ReceivedBatches.Count);
    }

    // A55 (§5.4/A48): a script that CREATE/ALTERs a module must invalidate the session
    // module cache. A definition fetched BEFORE the DDL — by breakpoint mapping or VS Code
    // opening the module's virtual document (both funnel through FetchModuleBlueprintAsync's
    // session-lifetime _moduleCache) — is stale the moment the script alters that module; a
    // later step-into / index request must see the ALTERED body, not the cached one. RED
    // before the ExecuteModuleDdlAsync cache eviction (the second resolution returned the OLD
    // definition straight from _moduleCache).
    [Fact]
    public async Task ModuleDdl_InvalidatesCachedDefinition_SoLaterResolutionSeesTheAlteredBody()
    {
        const string oldDef = "CREATE OR ALTER PROCEDURE dbo.child AS BEGIN SELECT 1 AS v; END";
        const string newDef = "CREATE OR ALTER PROCEDURE dbo.child AS BEGIN SELECT 1 AS v; SELECT 2 AS w; END";
        var childIdentity = new ModuleIdentity("SalesDb", "dbo", "child", IsScript: false);

        // The script IS the ALTER (A48 bare module DDL — frame 0's only statement).
        var executor = Init(new FakeStatementExecutor())
            .Then(_ => ModuleRow(oldDef, name: "child"))    // early resolution (breakpoint / virtual doc) → caches the OLD body
            .ThenEmpty()                                    // the CREATE OR ALTER runs bare (ExecuteModuleDdlAsync)
            .Then(_ => ModuleRow(newDef, name: "child"));   // re-fetch AFTER the DDL — only reached once the cache is evicted
        var session = ScriptSession(newDef, executor);
        await session.InitializeAsync();

        // Something resolved the module's definition before the ALTER ran — the OLD body is cached.
        var (before, _) = await session.TryGetModuleIndexAsync(childIdentity);
        Assert.Equal(oldDef, before!.FullScript);

        // The script CREATE OR ALTERs the module.
        await session.StepAsync();

        // A later step-into / breakpoint resolution must see the NEW body, not the stale cache.
        var (after, _) = await session.TryGetModuleIndexAsync(childIdentity);
        Assert.Equal(newDef, after!.FullScript);
    }
}
