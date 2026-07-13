using TsqlDbg.Core.Sessions;
using TsqlDbg.Core.Tests.Fakes;
using Xunit;

namespace TsqlDbg.Core.Tests.Sessions;

// M7 hardening (design note §5.2/§16, commit-modal) — the teardown branch matrix.
// CLAUDE.md safety rule 7: "rollback teardown is UNCONDITIONAL except the explicit,
// ratified commit path." TeardownAsync only ever touches _options/_executor/_trace
// (never frame/cursor state), so these construct a bare, never-initialized Session
// and call TeardownAsync directly — DESIGN §4's own comment on the method already
// documents this as safe ("even if InitializeAsync never completed").
public sealed class SessionTeardownCommitTests
{
    private static Session Bare(CommitMode commitMode, FakeStatementExecutor executor, string? executeAs = null)
        => new(
            new SessionOptions(
                "DEVSQL01", "SalesDb", LaunchMode.Script, null, null, "SELECT 1;",
                CommitMode: commitMode, ExecuteAs: executeAs),
            executor);

    [Fact]
    public async Task NoDecisionCallback_RollbackModeSession_RollsBack()
    {
        var executor = new FakeStatementExecutor().ThenEmpty();
        await Bare(CommitMode.Rollback, executor).TeardownAsync();

        Assert.Equal("IF @@TRANCOUNT > 0 ROLLBACK;", Assert.Single(executor.ReceivedBatches));
    }

    [Fact]
    public async Task NoDecisionCallback_CommitModeSession_StillRollsBack()
    {
        // Disconnect/error/lost-adapter paths NEVER supply a callback — commitMode
        // alone is never enough to commit (CLAUDE.md safety rule 7).
        var executor = new FakeStatementExecutor().ThenEmpty();
        await Bare(CommitMode.Commit, executor).TeardownAsync();

        Assert.Equal("IF @@TRANCOUNT > 0 ROLLBACK;", Assert.Single(executor.ReceivedBatches));
    }

    [Fact]
    public async Task DecisionCallback_ButRollbackModeSession_IgnoresIt_StillRollsBack()
    {
        // Defense in depth: a decision callback alone is never enough either — the
        // session must ALSO be configured commitMode=Commit (itself gated at launch
        // on the target's allowWrites:true — TsqlDbgDebugSession.HandleLaunchRequest).
        var executor = new FakeStatementExecutor().ThenEmpty();
        await Bare(CommitMode.Rollback, executor).TeardownAsync(() => Task.FromResult(true));

        Assert.Equal("IF @@TRANCOUNT > 0 ROLLBACK;", Assert.Single(executor.ReceivedBatches));
    }

    [Fact]
    public async Task DecisionCallback_ConfirmedYes_CommitModeSession_Commits()
    {
        var executor = new FakeStatementExecutor().ThenEmpty();
        await Bare(CommitMode.Commit, executor).TeardownAsync(() => Task.FromResult(true));

        Assert.Equal("IF @@TRANCOUNT > 0 COMMIT;", Assert.Single(executor.ReceivedBatches));
    }

    [Fact]
    public async Task DecisionCallback_Declined_CommitModeSession_RollsBackInstead()
    {
        var executor = new FakeStatementExecutor().ThenEmpty();
        await Bare(CommitMode.Commit, executor).TeardownAsync(() => Task.FromResult(false));

        Assert.Equal("IF @@TRANCOUNT > 0 ROLLBACK;", Assert.Single(executor.ReceivedBatches));
    }

    [Fact]
    public async Task DecisionCallbackThrows_TreatedAsDeclined_StillRollsBack()
    {
        // The 60s-timeout/no-reply cases surface to Session as either a callback that
        // never resolves in time (adapter-side Task.WhenAny — not exercised at this
        // layer) or one that faults; both must fail SAFE.
        var executor = new FakeStatementExecutor().ThenEmpty();
        Task<bool> Faulting() => throw new InvalidOperationException("extension never replied");
        await Bare(CommitMode.Commit, executor).TeardownAsync(Faulting);

        Assert.Equal("IF @@TRANCOUNT > 0 ROLLBACK;", Assert.Single(executor.ReceivedBatches));
    }

    [Fact]
    public async Task ExecuteAs_RevertsBeforeTheCommitDecisionAndBeforeCommit()
    {
        // C4 x commit-modal composition (orchestrator note): REVERT always precedes
        // the rollback/commit decision, in EVERY teardown, including the commit path.
        var executor = new FakeStatementExecutor().ThenEmpty().ThenEmpty();
        await Bare(CommitMode.Commit, executor, executeAs: "USER = 'AppUser'")
            .TeardownAsync(() => Task.FromResult(true));

        Assert.Equal(2, executor.ReceivedBatches.Count);
        Assert.Equal("REVERT;", executor.ReceivedBatches[0]);
        Assert.Equal("IF @@TRANCOUNT > 0 COMMIT;", executor.ReceivedBatches[1]);
    }
}
