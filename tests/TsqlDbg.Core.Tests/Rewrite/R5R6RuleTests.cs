// DESIGN §7.4 rules R5 (@@ERROR) and R6 (SCOPE_IDENTITY()) — mirrors R4RowcountRuleTests
// in SpanPatchEngineTests.cs (Fable, Phase-0), including the §20.1 R8-negative set.
using TsqlDbg.Core.Rewrite;
using TsqlDbg.Core.Tests.Interpreter;
using Xunit;

namespace TsqlDbg.Core.Tests.Rewrite;

public class R5ErrorRuleTests
{
    private static readonly RewriteContext Ctx = new("7f3a");

    private static RewriteResult Rewrite(string sql, bool quotedIdentifiers = true)
    {
        var stmt = ParseTestHelper.ParseSingle(sql, quotedIdentifiers);
        return RewriteEngine.CreateDefault().Rewrite(stmt, sql, Ctx);
    }

    [Fact]
    public void SingleOccurrence_IsReplaced_AndShadowReported()
    {
        var r = Rewrite("SET @miss = @@ERROR;");
        Assert.Equal("SET @miss = @__dbg7f3a_sh_error;", r.PatchedText);
        Assert.Contains(ShadowKind.Error, r.RequiredShadows);
        Assert.Single(r.Patches);
        Assert.Equal(RuleId.R5, r.Patches[0].Rule);
    }

    [Fact]
    public void R8Negative_StringLiteral_IsUntouched()
    {
        const string sql = "SET @s = N'value of @@ERROR';";
        var r = Rewrite(sql);
        Assert.Equal(sql, r.PatchedText);
        Assert.Empty(r.Patches);
    }

    [Fact]
    public void R8Negative_BracketedIdentifier_IsUntouched()
    {
        const string sql = "SELECT [@@ERROR] FROM dbo.t;";
        var r = Rewrite(sql);
        Assert.Equal(sql, r.PatchedText);
        Assert.Empty(r.Patches);
    }

    [Fact]
    public void R8Negative_Comment_IsUntouched()
    {
        const string sql = "SELECT c /* @@ERROR */ FROM dbo.t;";
        var r = Rewrite(sql);
        Assert.Equal(sql, r.PatchedText);
        Assert.Empty(r.Patches);
    }

    [Fact]
    public void NoOccurrence_ReturnsExactSlice_NoShadows()
    {
        const string sql = "SELECT 1 AS x;";
        var r = Rewrite(sql);
        Assert.Equal(sql, r.PatchedText);
        Assert.Empty(r.RequiredShadows);
    }
}

public class R6ScopeIdentityRuleTests
{
    private static readonly RewriteContext Ctx = new("7f3a");

    private static RewriteResult Rewrite(string sql, bool quotedIdentifiers = true)
    {
        var stmt = ParseTestHelper.ParseSingle(sql, quotedIdentifiers);
        return RewriteEngine.CreateDefault().Rewrite(stmt, sql, Ctx);
    }

    [Fact]
    public void SingleOccurrence_IsReplaced_AndShadowReported()
    {
        var r = Rewrite("SET @id = SCOPE_IDENTITY();");
        Assert.Equal("SET @id = @__dbg7f3a_sh_scopeid;", r.PatchedText);
        Assert.Contains(ShadowKind.ScopeIdentity, r.RequiredShadows);
        Assert.Single(r.Patches);
        Assert.Equal(RuleId.R6, r.Patches[0].Rule);
    }

    [Fact]
    public void IsCaseInsensitive()
    {
        var r = Rewrite("SET @id = scope_identity();");
        Assert.Equal("SET @id = @__dbg7f3a_sh_scopeid;", r.PatchedText);
    }

    [Fact]
    public void DoesNotMatchFunctionsWithParameters_OrSimilarNames()
    {
        // IDENT_CURRENT('t') is a different, never-rewritten intrinsic (§7.4) — a naive
        // substring/name check without checking Parameters.Count would risk false
        // positives on functions that merely contain "IDENTITY" in a longer name.
        const string sql = "SELECT IDENT_CURRENT('dbo.t');";
        var r = Rewrite(sql);
        Assert.Equal(sql, r.PatchedText);
        Assert.Empty(r.Patches);
    }

    [Fact]
    public void R8Negative_StringLiteral_IsUntouched()
    {
        const string sql = "SET @s = N'call SCOPE_IDENTITY()';";
        var r = Rewrite(sql);
        Assert.Equal(sql, r.PatchedText);
        Assert.Empty(r.Patches);
    }

    [Fact]
    public void NoOccurrence_ReturnsExactSlice_NoShadows()
    {
        const string sql = "SELECT 1 AS x;";
        var r = Rewrite(sql);
        Assert.Equal(sql, r.PatchedText);
        Assert.Empty(r.RequiredShadows);
    }
}
