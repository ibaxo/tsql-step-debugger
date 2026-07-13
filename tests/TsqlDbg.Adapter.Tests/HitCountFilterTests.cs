// M2-gate hit-count ruling (docs/archive/reviews/m2-gate-review-fable.md §4, ratified by Ivan
// 2026-07-05): operator-aware semantics, bare N = stop on the Nth qualifying hit only.
using TsqlDbg.Adapter;
using Xunit;

namespace TsqlDbg.Adapter.Tests;

public sealed class HitCountFilterTests
{
    private static bool[] StopsOverHits(HitCountFilter filter, int hitCount)
    {
        var results = new bool[hitCount];
        for (var i = 1; i <= hitCount; i++)
        {
            results[i - 1] = filter.ShouldStop(i);
        }

        return results;
    }

    [Fact]
    public void NullOrBlank_ReturnsNoFilter()
    {
        Assert.Null(HitCountFilter.Parse(null));
        Assert.Null(HitCountFilter.Parse(""));
        Assert.Null(HitCountFilter.Parse("   "));
    }

    [Fact]
    public void BareNumber_StopsOnlyOnTheNthHit()
    {
        var filter = HitCountFilter.Parse("3")!;
        Assert.Null(filter.InvalidText);
        Assert.Equal(new[] { false, false, true, false, false }, StopsOverHits(filter, 5));
    }

    [Fact]
    public void GreaterOrEqual_StopsOnNthAndEveryHitAfter()
    {
        var filter = HitCountFilter.Parse(">= 3")!;
        Assert.Equal(new[] { false, false, true, true, true }, StopsOverHits(filter, 5));
    }

    [Fact]
    public void DoubleEquals_StopsOnlyOnTheNthHit()
    {
        var filter = HitCountFilter.Parse("== 3")!;
        Assert.Equal(new[] { false, false, true, false, false }, StopsOverHits(filter, 5));
    }

    [Fact]
    public void GreaterThan_StopsStrictlyAfterTheNthHit()
    {
        var filter = HitCountFilter.Parse("> 3")!;
        Assert.Equal(new[] { false, false, false, true, true }, StopsOverHits(filter, 5));
    }

    [Fact]
    public void LessThan_StopsOnlyBeforeTheNthHit()
    {
        var filter = HitCountFilter.Parse("< 2")!;
        Assert.Equal(new[] { true, false, false, false }, StopsOverHits(filter, 4));
    }

    [Fact]
    public void LessOrEqual_StopsUpToAndIncludingTheNthHit()
    {
        var filter = HitCountFilter.Parse("<= 2")!;
        Assert.Equal(new[] { true, true, false, false }, StopsOverHits(filter, 4));
    }

    [Fact]
    public void Modulo_StopsOnEveryNthHit()
    {
        var filter = HitCountFilter.Parse("% 2")!;
        Assert.Equal(new[] { false, true, false, true, false, true }, StopsOverHits(filter, 6));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("3+1")]
    [InlineData("% 0")]
    public void Unparseable_IsInvalid_AndAlwaysStops(string hitCondition)
    {
        var filter = HitCountFilter.Parse(hitCondition)!;
        Assert.NotNull(filter);
        Assert.Equal(hitCondition, filter.InvalidText);
        Assert.True(filter.ShouldStop(1));
        Assert.True(filter.ShouldStop(2));
        Assert.True(filter.ShouldStop(100));
    }

    [Theory]
    [InlineData("   3   ")]
    [InlineData(">=   3")]
    [InlineData("  >= 3")]
    public void WhitespaceTolerant(string hitCondition)
    {
        var filter = HitCountFilter.Parse(hitCondition)!;
        Assert.Null(filter.InvalidText);
    }
}
