// M5 I8 (§8.3 setVariable, A19 — design note §2, docs/archive/reviews/m5-inspection-design-
// notes-fable.md): the healthy path (literal parse -> client-side validate/convert ->
// parameterized UPDATE) plus the detached/doomed/broken arms, and the conversion
// rejections (unknown variable, non-literal expression, exotic/CLR declared type,
// unparseable text, a genuine server-side CONVERT fault reported not thrown).
using TsqlDbg.Core.Execution;
using TsqlDbg.Core.Sessions;
using TsqlDbg.Core.Tests.Fakes;
using Xunit;

namespace TsqlDbg.Core.Tests.Sessions;

public sealed class SessionSetVariableTests
{
    private static BatchResult Ok(int trancount = 1, int xactState = 0)
        => new(new List<ResultSet>
        {
            new(new[] { "__dbg_ctl", "ok", "rc", "scope_identity", "trancount", "xact_state" },
                new IReadOnlyList<object?>[] { new object?[] { 1, true, null, null, trancount, xactState } }),
        }, Array.Empty<string>());

    private static BatchResult DoomFault()
        => new(new List<ResultSet>
        {
            new(new[] { "__dbg_ctl", "ok", "err_number", "err_severity", "err_state", "err_line", "err_procedure", "err_message", "trancount", "xact_state" },
                new IReadOnlyList<object?>[] { new object?[] { 1, false, 8134, 16, 1, 1, null, "Divide by zero error encountered.", 1, -1 } }),
        }, Array.Empty<string>());

    // Piggybacks a __dbg_state result set (§8.1) onto a control-row BatchResult so
    // Frame.Snapshot is populated — needed for the doomed-mode assertion (A8: the
    // client-side snapshot is the authoritative doomed-mode value source) and for
    // ReseedAllFramesAfterDetachAsync's reseed UPDATE to actually fire on a detached
    // edge, matching what a real ComposedBatchBuilder batch always piggybacks.
    private static BatchResult WithState(BatchResult result, params object?[] variableValues)
    {
        var stateColumns = new[] { "__dbg_state" }.Concat(Enumerable.Range(0, variableValues.Length).Select(i => $"v{i}")).ToArray();
        var stateRow = new object?[] { 1 }.Concat(variableValues).ToArray();
        var stateSet = new ResultSet(stateColumns, new IReadOnlyList<object?>[] { stateRow });
        return result with { ResultSets = result.ResultSets.Append(stateSet).ToList() };
    }

    private static Session ScriptSession(string script, FakeStatementExecutor executor)
        => new(new SessionOptions("DEVSQL01", "SalesDb", LaunchMode.Script, null, null, script), executor);

    private static FakeStatementExecutor Init(FakeStatementExecutor executor)
        => executor.ThenEmpty().ThenEmpty().ThenEmpty().ThenEmpty();   // SET opts, CREATE state, seed, BEGIN TRAN

    private const string Script = """
        DECLARE @x int = 1;
        BEGIN TRY
            SELECT 1/0;
        END TRY
        BEGIN CATCH
            SELECT 1;
        END CATCH
        """;

    [Fact]
    public async Task Healthy_AppliesParameterizedUpdate_WithConvert()
    {
        var executor = Init(new FakeStatementExecutor()).Then(_ => Ok());
        var session = ScriptSession(Script, executor);
        await session.InitializeAsync();
        var frame = session.Frames[0];

        var result = await session.SetVariableAsync(frame, "@x", "42");

        Assert.Equal(Session.SetVariableOutcome.Applied, result.Outcome);
        Assert.Equal(42L, result.AppliedValue);
        Assert.Contains("UPDATE #__dbg_s0", executor.ReceivedBatches[^1]);
        Assert.Contains("CONVERT(int, @p)", executor.ReceivedBatches[^1]);
        Assert.Equal(42L, executor.ReceivedParameters[executor.ReceivedBatches.Count - 1][0].Value);
        await session.TeardownAsync();
    }

    [Fact]
    public async Task Detached_SameUpdate_NoProtectionReopen()
    {
        const string script = """
            DECLARE @x int = 1;
            ROLLBACK;
            SELECT 1;
            """;
        var executor = Init(new FakeStatementExecutor())
            .Then(_ => WithState(Ok(), 1))               // DECLARE @x initializer
            .Then(_ => WithState(Ok(trancount: 0), 1))   // ROLLBACK -> detached edge
            .ThenEmpty()                                  // ReseedAllFramesAfterDetachAsync's reseed UPDATE
            .Then(_ => Ok());                             // the setVariable UPDATE itself
        var session = ScriptSession(script, executor);
        await session.InitializeAsync();
        await session.StepAsync();          // DECLARE @x
        await session.StepAsync();          // ROLLBACK -> detached
        Assert.True(session.IsTransactionDetached);
        var frame = session.Frames[0];
        var batchesBefore = executor.ReceivedBatches.Count;

        var result = await session.SetVariableAsync(frame, "@x", "5");

        Assert.Equal(Session.SetVariableOutcome.Applied, result.Outcome);
        Assert.Equal(5L, result.AppliedValue);
        // Exactly ONE more batch — the UPDATE itself — no protection-reopen
        // BEGIN TRANSACTION (§10.4/A19: state tables are debugger housekeeping).
        Assert.Equal(batchesBefore + 1, executor.ReceivedBatches.Count);
        Assert.DoesNotContain("BEGIN TRANSACTION", executor.ReceivedBatches[^1]);
        Assert.True(session.IsTransactionDetached);   // setVariable didn't resurrect the safety net either
        await session.TeardownAsync();
    }

    [Fact]
    public async Task Doomed_NoServerWork_EditsClientSideSnapshot()
    {
        var executor = Init(new FakeStatementExecutor())
            .Then(_ => WithState(Ok(), 1))          // DECLARE @x initializer
            .Then(_ => WithState(DoomFault(), 1));  // 1/0 dooms
        var session = ScriptSession(Script, executor);
        await session.InitializeAsync();
        await session.StepAsync();          // DECLARE @x initializer
        await session.StepAsync();          // 1/0 dooms
        Assert.True(session.IsDoomed);
        var frame = session.Frames[0];
        var batchesBefore = executor.ReceivedBatches.Count;

        var result = await session.SetVariableAsync(frame, "@x", "99");

        Assert.Equal(Session.SetVariableOutcome.Applied, result.Outcome);
        Assert.Equal(99L, result.AppliedValue);
        Assert.Equal(batchesBefore, executor.ReceivedBatches.Count);   // NOTHING sent to the server
        Assert.NotNull(result.Note);
        Assert.Contains("doomed", result.Note);
        Assert.Equal(99L, frame.Snapshot![frame.Variables.All[0].Ordinal]);   // A8: authoritative client store
        await session.TeardownAsync();
    }

    [Fact]
    public async Task Broken_Refused()
    {
        var executor = Init(new FakeStatementExecutor())
            .Then(_ => throw new StatementExecutionException("compile error", 16, 999)); // sameScopeUncatchable, no caller -> terminal
        var session = ScriptSession(Script, executor);
        await session.InitializeAsync();
        // DECLARE @x's own initializer batch faults compile-class (sameScopeUncatchable);
        // frame 0 has no caller to route to, so this goes terminal in ONE step —
        // frame 0's own TRY/CATCH doesn't rescue a same-scope-uncatchable class.
        await session.StepAsync();
        Assert.True(session.IsBroken);
        var frame = session.Frames[0];

        var result = await session.SetVariableAsync(frame, "@x", "1");

        Assert.Equal(Session.SetVariableOutcome.Refused, result.Outcome);
        Assert.Contains("terminated", result.RefusalReason);
        await session.TeardownAsync();
    }

    [Fact]
    public async Task UnknownVariable_Refused()
    {
        var executor = Init(new FakeStatementExecutor());
        var session = ScriptSession(Script, executor);
        await session.InitializeAsync();
        var frame = session.Frames[0];

        var result = await session.SetVariableAsync(frame, "@nope", "1");

        Assert.Equal(Session.SetVariableOutcome.Refused, result.Outcome);
        Assert.Contains("no such variable", result.RefusalReason);
        await session.TeardownAsync();
    }

    [Fact]
    public async Task NonLiteralExpression_Refused()
    {
        var executor = Init(new FakeStatementExecutor());
        var session = ScriptSession(Script, executor);
        await session.InitializeAsync();
        var frame = session.Frames[0];

        var result = await session.SetVariableAsync(frame, "@x", "1+1");

        Assert.Equal(Session.SetVariableOutcome.Refused, result.Outcome);
        Assert.Contains("literal", result.RefusalReason);
        await session.TeardownAsync();
    }

    [Fact]
    public async Task UnparseableText_Refused()
    {
        var executor = Init(new FakeStatementExecutor());
        var session = ScriptSession(Script, executor);
        await session.InitializeAsync();
        var frame = session.Frames[0];

        var result = await session.SetVariableAsync(frame, "@x", "1 +");

        Assert.Equal(Session.SetVariableOutcome.Refused, result.Outcome);
        Assert.Contains("could not parse", result.RefusalReason);
        await session.TeardownAsync();
    }

    [Fact]
    public async Task ExoticDeclaredType_RefusedUpFront_NoRoundTrip()
    {
        const string script = """
            DECLARE @p hierarchyid;
            SELECT 1;
            """;
        var executor = Init(new FakeStatementExecutor());
        var session = ScriptSession(script, executor);
        await session.InitializeAsync();
        var frame = session.Frames[0];
        var batchesBefore = executor.ReceivedBatches.Count;

        var result = await session.SetVariableAsync(frame, "@p", "'/1/'");

        Assert.Equal(Session.SetVariableOutcome.Refused, result.Outcome);
        Assert.Contains("no safe literal form", result.RefusalReason);
        Assert.Equal(batchesBefore, executor.ReceivedBatches.Count);
        await session.TeardownAsync();
    }

    [Fact]
    public async Task ServerSideConvertFault_ReportsInsteadOfThrowing()
    {
        var executor = Init(new FakeStatementExecutor())
            .Then(_ => throw new StatementExecutionException("Conversion failed.", 16, 245));
        var session = ScriptSession(Script, executor);
        await session.InitializeAsync();
        var frame = session.Frames[0];

        var result = await session.SetVariableAsync(frame, "@x", "'not-a-number'");

        Assert.Equal(Session.SetVariableOutcome.Refused, result.Outcome);
        Assert.Contains("setVariable faulted", result.RefusalReason);
        Assert.Contains("245", result.RefusalReason);
        await session.TeardownAsync();
    }

    [Fact]
    public async Task NullLiteral_AppliesAsDBNull()
    {
        var executor = Init(new FakeStatementExecutor()).Then(_ => Ok());
        var session = ScriptSession(Script, executor);
        await session.InitializeAsync();
        var frame = session.Frames[0];

        var result = await session.SetVariableAsync(frame, "@x", "NULL");

        Assert.Equal(Session.SetVariableOutcome.Applied, result.Outcome);
        Assert.Equal(DBNull.Value, result.AppliedValue);
        await session.TeardownAsync();
    }

    [Fact]
    public async Task NegativeIntegerLiteral_AppliesNegated()
    {
        var executor = Init(new FakeStatementExecutor()).Then(_ => Ok());
        var session = ScriptSession(Script, executor);
        await session.InitializeAsync();
        var frame = session.Frames[0];

        var result = await session.SetVariableAsync(frame, "@x", "-7");

        Assert.Equal(Session.SetVariableOutcome.Applied, result.Outcome);
        Assert.Equal(-7L, result.AppliedValue);
        await session.TeardownAsync();
    }

    [Fact]
    public async Task StringLiteral_ForDeclaredStringType_Applies()
    {
        const string script = """
            DECLARE @s varchar(50) = 'a';
            SELECT 1;
            """;
        var executor = Init(new FakeStatementExecutor()).Then(_ => Ok());
        var session = ScriptSession(script, executor);
        await session.InitializeAsync();
        var frame = session.Frames[0];

        var result = await session.SetVariableAsync(frame, "@s", "'hello world'");

        Assert.Equal(Session.SetVariableOutcome.Applied, result.Outcome);
        Assert.Equal("hello world", result.AppliedValue);
        await session.TeardownAsync();
    }
}
