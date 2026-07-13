using TsqlDbg.Core.Parsing;
using Xunit;

namespace TsqlDbg.Core.Tests.Parsing;

// DESIGN §2 (A57): compatLevel:0 auto-detect resolution. Explicit pins pass through untouched;
// auto maps the server's product major × 10, clamped to [150,170] with a warning outside the
// band, and falls back to 150 with a note when the version is unknown.
public class CompatLevelResolverTests
{
    [Theory]
    [InlineData(150)]
    [InlineData(160)]
    [InlineData(170)]
    [InlineData(155)] // any non-zero value is an explicit pin — honored verbatim, no clamp
    public void ExplicitPin_IsHonored_WithoutWarning(int requested)
    {
        var resolved = CompatLevelResolver.Resolve(requested, serverMajorVersion: 9, out var warning);

        Assert.Equal(requested, resolved);
        Assert.Null(warning);
    }

    [Theory]
    [InlineData(15, 150)] // SQL Server 2019
    [InlineData(16, 160)] // SQL Server 2022
    [InlineData(17, 170)] // SQL Server 2025
    public void Auto_InBand_PicksMajorTimesTen_WithoutWarning(int serverMajor, int expected)
    {
        var resolved = CompatLevelResolver.Resolve(requestedCompatLevel: 0, serverMajor, out var warning);

        Assert.Equal(expected, resolved);
        Assert.Null(warning);
    }

    [Theory]
    [InlineData(14)] // 2017 -> 140, below the band
    [InlineData(13)] // 2016 -> 130
    [InlineData(11)] // 2012 -> 110
    public void Auto_OlderThanBand_ClampsToMin_WithWarning(int serverMajor)
    {
        var resolved = CompatLevelResolver.Resolve(requestedCompatLevel: 0, serverMajor, out var warning);

        Assert.Equal(CompatLevelResolver.MinCompatLevel, resolved);
        Assert.NotNull(warning);
    }

    [Theory]
    [InlineData(18)]
    [InlineData(25)]
    public void Auto_NewerThanBand_ClampsToMax_WithWarning(int serverMajor)
    {
        var resolved = CompatLevelResolver.Resolve(requestedCompatLevel: 0, serverMajor, out var warning);

        Assert.Equal(CompatLevelResolver.MaxCompatLevel, resolved);
        Assert.NotNull(warning);
    }

    [Theory]
    [InlineData(0)]  // no live connection (e.g. FakeStatementExecutor) — version unknown
    [InlineData(-1)]
    public void Auto_UnknownVersion_FallsBackToMin_WithWarning(int serverMajor)
    {
        var resolved = CompatLevelResolver.Resolve(requestedCompatLevel: 0, serverMajor, out var warning);

        Assert.Equal(CompatLevelResolver.MinCompatLevel, resolved);
        Assert.NotNull(warning);
    }
}
