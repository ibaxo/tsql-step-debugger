// DESIGN §10.2 / A27 (D2) — ERROR_PROCEDURE() synthesis for module frames, SCHEMA-
// QUALIFIED (orchestrator ruling 1 / fact 31c). When a fault has no server-named module
// (it happened in our ad-hoc batch, Procedure NULL) but the ORIGIN frame is a module,
// native ERROR_PROCEDURE() names it as schema.name; script frames keep NULL. Driven
// through Session.StepAsync with a fake executor — both builder paths (control-row route
// and the §10.1 propagate/compile-class path) and the p23 cross-frame abnormal-pop shape.
using TsqlDbg.Core.Execution;
using TsqlDbg.Core.Sessions;
using TsqlDbg.Core.Tests.Fakes;
using Xunit;

namespace TsqlDbg.Core.Tests.Sessions;

public sealed class SessionD2ErrorProcedureTests
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
        if (state is not null) sets.Add(StateSet(state));
        return new BatchResult(sets, Array.Empty<string>());
    }

    private static BatchResult Fault(int errNumber, string errMessage, int? errLine = null, int xactState = 1)
        => new(new List<ResultSet>
        {
            new(new[] { "__dbg_ctl", "ok", "err_number", "err_severity", "err_state", "err_line", "err_procedure", "err_message", "trancount", "xact_state" },
                new IReadOnlyList<object?>[] { new object?[] { 1, false, errNumber, 16, 1, errLine, null, errMessage, 1, xactState } }),
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

    private static BatchResult ModuleRow(string definition, string schema, string name) => new(
        new[]
        {
            new ResultSet(new[] { "def", "qi", "ansi_nulls", "schema_name", "name" },
                new IReadOnlyList<object?>[] { new object?[] { definition, true, true, schema, name } }),
        },
        Array.Empty<string>());

    private static Session ScriptSession(string script, FakeStatementExecutor executor)
        => new(new SessionOptions("DEVSQL01", "SalesDb", LaunchMode.Script, null, null, script), executor);

    private static FakeStatementExecutor Init(FakeStatementExecutor executor)
        => executor.ThenEmpty().ThenEmpty().ThenEmpty().ThenEmpty();

    private static FakeStatementExecutor QueuePush(
        FakeStatementExecutor executor, string calleeDef, string schema, string name, object? argValue)
        => executor
            .Then(_ => ModuleRow(calleeDef, schema, name))
            .Then(_ => Ok(userSets: new[] { Scalar("p", argValue) }))    // arg eval scalar
            .ThenEmpty().ThenEmpty().ThenEmpty()
            .Then(_ => Row(argValue));

    private const string CallerScript = """
        DECLARE @x int, @rc int;
        BEGIN TRY
        EXEC @rc = dbo.child @a = @x OUTPUT;
        END TRY
        BEGIN CATCH
        SELECT ERROR_PROCEDURE() AS p;
        END CATCH
        """;

    // ---- site 1: control-row route — a callee's caught fault names the callee (schema-qualified). ----
    [Fact]
    public async Task ControlRowRoute_CalleeFault_SynthesizesSchemaQualifiedModuleName()
    {
        const string calleeDef = """
            CREATE PROCEDURE dbo.child @a int OUTPUT AS
            BEGIN
            SELECT 1/0 AS boom;
            END
            """;
        var executor = Init(new FakeStatementExecutor());
        QueuePush(executor, calleeDef, "dbo", "child", null)
            .Then(_ => Fault(8134, "Divide by zero error encountered.", errLine: 9))
            .ThenEmpty();                                        // abnormal pop cleanup
        var session = ScriptSession(CallerScript, executor);
        await session.InitializeAsync();
        await session.StepAsync();
        await session.StepAsync(StepKind.Into);

        await session.StepAsync();                               // callee SELECT 1/0 → routed to caller CATCH

        Assert.Equal(StepDisposition.RoutedToCatch, session.LastStep.Disposition);
        Assert.Equal(8134, session.ActiveErrorContext!.Values.Number);
        Assert.Equal("dbo.child", session.ActiveErrorContext.Values.Procedure);   // A27: synthesized, schema-qualified
    }

    // ---- site 3: §10.1 propagate (compile-class 208) — the p23 shape: the callee's
    // uncatchable 208 lands in the CALLER's CATCH naming the callee (fact 23-F). ----
    [Fact]
    public async Task Propagate_CalleeCompileClass208_SynthesizesCalleeModuleName_TheP23Shape()
    {
        const string calleeDef = """
            CREATE PROCEDURE dbo.child @a int OUTPUT AS
            BEGIN
            BEGIN TRY
            SELECT * FROM dbo.missing;
            END TRY
            BEGIN CATCH
            SELECT 1 AS never;
            END CATCH
            END
            """;
        var executor = Init(new FakeStatementExecutor());
        QueuePush(executor, calleeDef, "dbo", "child", null)
            .Then(_ => throw new StatementExecutionException("Invalid object name 'dbo.missing'.", 16, 208))
            .ThenEmpty();
        var session = ScriptSession(CallerScript, executor);
        await session.InitializeAsync();
        await session.StepAsync();
        await session.StepAsync(StepKind.Into);

        await session.StepAsync();                               // callee 208 → same-scope-uncatchable → caller CATCH

        Assert.Equal(StepDisposition.RoutedToCatch, session.LastStep.Disposition);
        Assert.Equal(208, session.ActiveErrorContext!.Values.Number);
        Assert.Equal("dbo.child", session.ActiveErrorContext.Values.Procedure);   // names the CALLEE (fact 23-F)
    }

    // ---- ruling 1: a NON-dbo schema callee synthesizes schema.name, not defaulting to dbo. ----
    [Fact]
    public async Task NonDboSchemaCallee_SynthesizesActualSchema_NotDbo()
    {
        const string calleeDef = """
            CREATE PROCEDURE sales.child @a int OUTPUT AS
            BEGIN
            SELECT 1/0 AS boom;
            END
            """;
        var script = CallerScript.Replace("dbo.child", "sales.child");
        var executor = Init(new FakeStatementExecutor());
        QueuePush(executor, calleeDef, "sales", "child", null)
            .Then(_ => Fault(8134, "Divide by zero error encountered.", errLine: 9))
            .ThenEmpty();
        var session = ScriptSession(script, executor);
        await session.InitializeAsync();
        await session.StepAsync();
        await session.StepAsync(StepKind.Into);

        await session.StepAsync();

        Assert.Equal("sales.child", session.ActiveErrorContext!.Values.Procedure);   // actual schema, not "dbo"
    }

    // ---- script frames keep NULL — native ad-hoc batches read NULL (A27 unchanged half). ----
    [Fact]
    public async Task ScriptFrameFault_KeepsNullProcedure()
    {
        const string script = """
            BEGIN TRY
            SELECT 1/0 AS a;
            END TRY
            BEGIN CATCH
            SELECT ERROR_PROCEDURE() AS p;
            END CATCH
            """;
        var executor = Init(new FakeStatementExecutor()).Then(_ => Fault(8134, "Divide by zero error encountered."));
        var session = ScriptSession(script, executor);
        await session.InitializeAsync();

        await session.StepAsync();                               // SELECT 1/0 → routed to the frame's own CATCH

        Assert.Equal(StepDisposition.RoutedToCatch, session.LastStep.Disposition);
        Assert.Equal(8134, session.ActiveErrorContext!.Values.Number);
        Assert.Null(session.ActiveErrorContext.Values.Procedure);   // script frame — NULL, matching native ad-hoc
    }
}
