// DESIGN §7.4 / A26 (D1) — ShadowValues.ObserveSuccess gates the R6 SCOPE_IDENTITY()
// capture on the session-owned scopeChainInSync flag; rc/err are per-statement live
// truth and are captured in every regime.
using TsqlDbg.Core.Batches;
using Xunit;

namespace TsqlDbg.Core.Tests.Batches;

public class ShadowValuesChainSyncTests
{
    private static readonly IReadOnlyDictionary<int, DisplayValue> NoDisplay = new Dictionary<int, DisplayValue>();

    private static ControlRow Ok(decimal? scopeId, int? rc = 1, int? errAfter = null)
        => new(true, rc, scopeId, 1, 0, null, null, null, null, null, null, NoDisplay, errAfter);

    [Fact]
    public void InSync_TakesTheScopeIdentityCapture()
    {
        var shadows = ShadowValues.Initial();
        shadows.ObserveSuccess(Ok(scopeId: 5m), scopeChainInSync: true);
        Assert.Equal(5m, shadows.ScopeIdentity);
    }

    [Fact]
    public void OutOfSync_SkipsTheCapture_KeepingTheClientModeledValue()
    {
        var shadows = ShadowValues.Initial();
        shadows.ObserveSuccess(Ok(scopeId: 5m), scopeChainInSync: true);    // establish 5 in sync
        shadows.ObserveSuccess(Ok(scopeId: 99m), scopeChainInSync: false);  // server leaked 99 — ignored
        Assert.Equal(5m, shadows.ScopeIdentity);                            // shadow keeps the modeled 5
    }

    [Fact]
    public void OutOfSync_StillCapturesRowcountAndError()
    {
        var shadows = ShadowValues.Initial();
        shadows.ObserveSuccess(Ok(scopeId: 5m), scopeChainInSync: true);
        shadows.ObserveSuccess(Ok(scopeId: 99m, rc: 7, errAfter: 0), scopeChainInSync: false);
        Assert.Equal(7, shadows.RowCount);          // rc captured (live truth in every regime)
        Assert.Equal(0, shadows.ErrorNumber);       // err captured
        Assert.Equal(5m, shadows.ScopeIdentity);    // only the scope-identity capture is gated
    }

    [Fact]
    public void DefaultIsInSync_PreservesPreD1Behavior()
    {
        var shadows = ShadowValues.Initial();
        shadows.ObserveSuccess(Ok(scopeId: 12m));   // no explicit arg → in sync (old behavior)
        Assert.Equal(12m, shadows.ScopeIdentity);
    }
}
