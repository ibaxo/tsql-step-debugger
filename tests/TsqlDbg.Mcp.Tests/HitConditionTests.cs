namespace TsqlDbg.Mcp.Tests;

// DESIGN §24.6/§13: minimal hit-count parser for the programmatic surface (v1).
public sealed class HitConditionTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_NullOrEmpty_ReturnsNull(string? text)
    {
        Assert.Null(HitCondition.Parse(text));
    }

    [Theory]
    [InlineData("3", 2, false)]
    [InlineData("3", 3, true)]
    [InlineData("3", 4, false)]
    public void Parse_BareNumber_IsEquality(string text, int hitCount, bool expected)
    {
        var condition = HitCondition.Parse(text)!;
        Assert.Equal(expected, condition.ShouldStop(hitCount));
    }

    [Theory]
    [InlineData("=3", 3, true)]
    [InlineData("=3", 2, false)]
    [InlineData("==3", 3, true)]
    [InlineData("==3", 4, false)]
    public void Parse_EqualityForms_StopOnlyOnExactHit(string text, int hitCount, bool expected)
    {
        var condition = HitCondition.Parse(text)!;
        Assert.Equal(expected, condition.ShouldStop(hitCount));
    }

    [Theory]
    [InlineData(">3", 3, false)]
    [InlineData(">3", 4, true)]
    [InlineData(">3", 100, true)]
    public void Parse_GreaterThan(string text, int hitCount, bool expected)
    {
        var condition = HitCondition.Parse(text)!;
        Assert.Equal(expected, condition.ShouldStop(hitCount));
    }

    [Theory]
    [InlineData(">=3", 2, false)]
    [InlineData(">=3", 3, true)]
    [InlineData(">=3", 4, true)]
    public void Parse_GreaterThanOrEqual(string text, int hitCount, bool expected)
    {
        var condition = HitCondition.Parse(text)!;
        Assert.Equal(expected, condition.ShouldStop(hitCount));
    }

    [Theory]
    [InlineData("<3", 2, true)]
    [InlineData("<3", 3, false)]
    [InlineData("<3", 4, false)]
    public void Parse_LessThan(string text, int hitCount, bool expected)
    {
        var condition = HitCondition.Parse(text)!;
        Assert.Equal(expected, condition.ShouldStop(hitCount));
    }

    [Theory]
    [InlineData("<=3", 2, true)]
    [InlineData("<=3", 3, true)]
    [InlineData("<=3", 4, false)]
    public void Parse_LessThanOrEqual(string text, int hitCount, bool expected)
    {
        var condition = HitCondition.Parse(text)!;
        Assert.Equal(expected, condition.ShouldStop(hitCount));
    }

    [Theory]
    [InlineData("%3", 1, false)]
    [InlineData("%3", 2, false)]
    [InlineData("%3", 3, true)]
    [InlineData("%3", 6, true)]
    [InlineData("%3", 7, false)]
    public void Parse_EveryNth(string text, int hitCount, bool expected)
    {
        var condition = HitCondition.Parse(text)!;
        Assert.Equal(expected, condition.ShouldStop(hitCount));
    }

    [Theory]
    [InlineData("not-a-number")]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData(">=0")]
    [InlineData("%0")]
    public void Parse_UnparseableOrNonPositive_IsInvalid_AlwaysStops(string text)
    {
        var condition = HitCondition.Parse(text)!;
        Assert.NotNull(condition);

        // Never silently skip: an invalid condition stops on every hit count checked.
        Assert.True(condition.ShouldStop(1));
        Assert.True(condition.ShouldStop(2));
        Assert.True(condition.ShouldStop(1000));
    }
}
