// A61 (§12.3): after a write-mode console statement that changed table contents, the
// adapter drops the snapshot's fill-once Temp Tables display caches so the next expand
// re-reads the live rowcount. These pin that InvalidateTempValues() clears the cached
// display values while leaving the rows/detail reference space intact (A25/A16 contract).
using TsqlDbg.Adapter.Inspection;
using Xunit;

namespace TsqlDbg.Adapter.Tests;

public sealed class StopSnapshotTempCacheTests
{
    [Fact]
    public void InvalidateTempValues_DropsCachedDisplay_ForcingAReRead()
    {
        var snapshot = new StopSnapshot(0, Array.Empty<SnapshotFrame>(), null);
        snapshot.CacheTempValue("#__dbg_tv_f0", "(3 rows)");
        Assert.True(snapshot.TryGetCachedTempValue("#__dbg_tv_f0", out var cached));
        Assert.Equal("(3 rows)", cached);

        snapshot.InvalidateTempValues();

        // The next variables request now finds nothing cached → the executor re-runs the
        // live COUNT(*) and caches the fresh "(2 rows)".
        Assert.False(snapshot.TryGetCachedTempValue("#__dbg_tv_f0", out _));
    }

    [Fact]
    public void InvalidateTempValues_LeavesRowsReferenceSpaceValid()
    {
        var snapshot = new StopSnapshot(0, Array.Empty<SnapshotFrame>(), null);
        var reference = snapshot.GetOrMintRowsReference("#__dbg_tv_f0");
        snapshot.CacheTempValue("#__dbg_tv_f0", "(3 rows)");

        snapshot.InvalidateTempValues();

        // A61: only the display cache is dropped — the minted rows reference still resolves
        // to its physical name (the page fetch was always live), and re-minting returns the
        // SAME id, matching the "same reference space" republish contract.
        Assert.True(snapshot.TryResolveRowsReference(reference, out var physicalName));
        Assert.Equal("#__dbg_tv_f0", physicalName);
        Assert.Equal(reference, snapshot.GetOrMintRowsReference("#__dbg_tv_f0"));
    }
}
