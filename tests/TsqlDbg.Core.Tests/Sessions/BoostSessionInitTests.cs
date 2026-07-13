using TsqlDbg.Core.Sessions;
using TsqlDbg.Core.Tests.Fakes;
using Xunit;

namespace TsqlDbg.Core.Tests.Sessions;

// M6 F1 ruling (docs/archive/reviews/m6-boost-core-fable.md §2): #__dbg_boost is created +
// seeded ONCE at session init when boost:true — before BEGIN TRANSACTION (trancount 0
// → rollback-immortal, fact 1) and on the fresh connection (the seed INSERT's
// SCOPE_IDENTITY clobber lands on an already-NULL chain, fact 26d).
public sealed class BoostSessionInitTests
{
    private static SessionOptions Options(bool boost) =>
        new("DEVSQL01", "SalesDb", LaunchMode.Script, null, null, "SELECT 1 AS x;", Boost: boost);

    [Fact]
    public async Task BoostTrue_SeedsTheBoostTable_BeforeBeginTransaction()
    {
        var executor = new FakeStatementExecutor();
        var session = new Session(Options(boost: true), executor);
        await session.InitializeAsync();

        var init = Assert.Single(executor.ReceivedBatches, b => b.Contains("#__dbg_boost"));
        Assert.Contains("IF OBJECT_ID('tempdb..#__dbg_boost') IS NULL CREATE TABLE #__dbg_boost", init);
        Assert.Contains("INSERT #__dbg_boost VALUES (0, -1);", init);

        var initIndex = executor.ReceivedBatches.FindIndex(b => b.Contains("#__dbg_boost"));
        var beginTranIndex = executor.ReceivedBatches.FindIndex(b => b == "BEGIN TRANSACTION;");
        Assert.True(beginTranIndex > initIndex, "the boost table must be created at trancount 0 (fact 1 immortality)");
    }

    [Fact]
    public async Task BoostFalse_NeverTouchesTheBoostTable()
    {
        var executor = new FakeStatementExecutor();
        var session = new Session(Options(boost: false), executor);
        await session.InitializeAsync();

        Assert.DoesNotContain(executor.ReceivedBatches, b => b.Contains("#__dbg_boost"));
    }
}
