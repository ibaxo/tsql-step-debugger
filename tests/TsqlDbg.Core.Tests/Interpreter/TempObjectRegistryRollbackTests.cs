using TsqlDbg.Core.Interpreter;
using Xunit;

namespace TsqlDbg.Core.Tests.Interpreter;

// DESIGN §9 / fact 24 (corrected A63): a ROLLBACK past creation destroys #temp tables but NOT
// cursors — cursors are non-transactional (verified live). MarkDeadAbove must therefore skip
// TempObjectKind.Cursor. This also corrects a latent named-cursor rollback infidelity.
public class TempObjectRegistryRollbackTests
{
    private static TempObjectEntry Entry(string name, string phys, TempObjectKind kind, int createdAt) => new()
    {
        OriginalName = name,
        PhysicalName = phys,
        Kind = kind,
        CreatedAtTrancount = createdAt,
    };

    [Fact]
    public void MarkDeadAbove_KillsTempTableCreatedAboveSurvivingLevel_ButNotCursor()
    {
        var registry = new TempObjectRegistry();
        registry.Add(Entry("#t", "#t__f0", TempObjectKind.TempTable, createdAt: 2));   // created in the doomed tran
        registry.Add(Entry("cur", "cur__f0_c", TempObjectKind.Cursor, createdAt: 2));  // named cursor, same level
        registry.Add(Entry("@c", "c__f0_cv", TempObjectKind.Cursor, createdAt: 2));    // A63 variable cursor

        registry.MarkDeadAbove(survivingTrancount: 1);

        Assert.True(registry.TryResolve("#t") is null);                    // #temp destroyed by rollback
        Assert.Equal("cur__f0_c", registry.TryResolve("cur")?.PhysicalName); // named cursor survives (fact 24)
        Assert.Equal("c__f0_cv", registry.TryResolve("@c")?.PhysicalName);   // variable cursor survives
    }

    [Fact]
    public void MarkDeadAbove_LeavesCursorCreatedAboveEvenAtZeroSurvivingLevel()
    {
        var registry = new TempObjectRegistry();
        registry.Add(Entry("@c", "c__f0_cv", TempObjectKind.Cursor, createdAt: 2));

        registry.MarkDeadAbove(survivingTrancount: 0);   // the doom-boundary force rollback

        Assert.NotNull(registry.TryResolve("@c"));       // still alive — cursors are non-transactional
    }
}
