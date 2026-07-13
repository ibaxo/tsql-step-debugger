// Phase-0 tests (Fable) — DESIGN §7.4 engine invariants + reference rule R4, including
// the §20.1 / R8-negative set the gate requires: string literals, [bracketed identifiers]
// and comments must NEVER rewrite. Sonnet mirrors the R4 positive tests for R5/R6.
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using TsqlDbg.Core.Interpreter;   // SourceSpan not needed; ParseTestHelper reused
using TsqlDbg.Core.Rewrite;
using TsqlDbg.Core.Rewrite.Rules;
using TsqlDbg.Core.Tests.Interpreter;
using Xunit;

namespace TsqlDbg.Core.Tests.Rewrite;

public class R4RowcountRuleTests
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
        var r = Rewrite("SET @miss = @@ROWCOUNT;");
        Assert.Equal("SET @miss = @__dbg7f3a_sh_rowcount;", r.PatchedText);
        Assert.Contains(ShadowKind.Rowcount, r.RequiredShadows);
        Assert.Single(r.Patches);
        Assert.Equal(RuleId.R4, r.Patches[0].Rule);
    }

    [Fact]
    public void MultipleOccurrences_MixedCase_LengthChangingReplacements_KeepOffsetsRight()
    {
        // Two replacements, each longer than the original — proves right-to-left apply.
        var r = Rewrite("SELECT @@RowCount + @@ROWCOUNT AS rc;");
        Assert.Equal("SELECT @__dbg7f3a_sh_rowcount + @__dbg7f3a_sh_rowcount AS rc;", r.PatchedText);
        Assert.Equal(2, r.Patches.Count);
    }

    [Fact]
    public void R8Negative_StringLiteral_IsUntouched()
    {
        const string sql = "SET @s = N'value of @@ROWCOUNT';";
        var r = Rewrite(sql);
        Assert.Equal(sql, r.PatchedText);
        Assert.Empty(r.Patches);
        Assert.Empty(r.RequiredShadows);
    }

    [Fact]
    public void R8Negative_BracketedIdentifier_IsUntouched()
    {
        const string sql = "SELECT [@@ROWCOUNT] FROM dbo.t;";
        var r = Rewrite(sql);
        Assert.Equal(sql, r.PatchedText);
        Assert.Empty(r.Patches);
    }

    [Fact]
    public void R8Negative_BlockComment_IsUntouched()
    {
        const string sql = "SELECT /* @@ROWCOUNT */ c FROM dbo.t;";
        var r = Rewrite(sql);
        Assert.Equal(sql, r.PatchedText);
        Assert.Empty(r.Patches);
    }

    [Fact]
    public void R8Negative_LineComment_InsideStatement_IsUntouched()
    {
        const string sql = "SELECT c -- @@rowcount\nFROM dbo.t;";
        var r = Rewrite(sql);
        Assert.Equal(sql, r.PatchedText);
        Assert.Empty(r.Patches);
    }

    [Fact]
    public void R8Negative_DoubleQuotedString_UnderQuotedIdentifierOff_IsUntouched()
    {
        const string sql = "SET @s = \"has @@ROWCOUNT\";";
        var r = Rewrite(sql, quotedIdentifiers: false);
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

public class SpanPatchEngineTests
{
    private static readonly RewriteContext Ctx = new("ab12");

    /// <summary>Test double: runs an arbitrary collect callback under RuleId.Test.</summary>
    private sealed class FakeRule : IRewriteRule
    {
        private readonly Action<TSqlFragment, RewriteContext, SpanPatchCollector, ISet<ShadowKind>> _collect;
        public FakeRule(Action<TSqlFragment, RewriteContext, SpanPatchCollector, ISet<ShadowKind>> collect) => _collect = collect;
        public RuleId Id => RuleId.Test;
        public void Collect(TSqlFragment s, RewriteContext c, SpanPatchCollector p, ISet<ShadowKind> sh) => _collect(s, c, p, sh);
    }

    private sealed class GlobalVarFinder : TSqlFragmentVisitor
    {
        public GlobalVariableExpression? Found;
        public override void Visit(GlobalVariableExpression node) => Found ??= node;
        public static GlobalVariableExpression FindIn(TSqlFragment f)
        {
            var v = new GlobalVarFinder();
            f.Accept(v);
            return v.Found ?? throw new InvalidOperationException("No @@intrinsic in test SQL.");
        }
    }

    [Fact]
    public void TwoRulesPatchingTheSameNode_ThrowOverlap_NamingBothRules()
    {
        const string sql = "SET @x = @@ROWCOUNT;";
        var stmt = ParseTestHelper.ParseSingle(sql);
        var clone = new FakeRule((s, c, p, _) => p.Add(GlobalVarFinder.FindIn(s), "@dup"));
        var engine = new RewriteEngine(new IRewriteRule[] { new R4RowcountRule(), clone });

        var ex = Assert.Throws<SpanOverlapException>(() => engine.Rewrite(stmt, sql, Ctx));
        Assert.Contains("R4", ex.Message);
        Assert.Contains("Test", ex.Message);
    }

    [Fact]
    public void SynthesizedFragment_IsRejected_ByConstruction()
    {
        const string sql = "SELECT 1 AS x;";
        var stmt = ParseTestHelper.ParseSingle(sql);
        var rule = new FakeRule((_, _, p, _) => p.Add(new IntegerLiteral { Value = "1" }, "nope"));

        var ex = Assert.Throws<InvalidOperationException>(
            () => new RewriteEngine(new IRewriteRule[] { rule }).Rewrite(stmt, sql, Ctx));
        Assert.Contains("source position", ex.Message);
    }

    [Fact]
    public void PatchOutsideTheSliceBeingRewritten_Throws()
    {
        // The patched node lives in statement 1; we rewrite statement 2's slice.
        const string sql = "SET @a = @@ROWCOUNT;\nSET @b = 2;";
        var (body, _) = ParseTestHelper.ParseBatch(sql);
        var nodeInFirst = GlobalVarFinder.FindIn(body[0]);
        var rule = new FakeRule((_, _, p, _) => p.Add(nodeInFirst, "@x"));

        Assert.Throws<SpanOutOfSliceException>(
            () => new RewriteEngine(new IRewriteRule[] { rule }).Rewrite(body[1], sql, Ctx));
    }

    [Fact]
    public void EmptyNewText_DeletesTheNodeText()
    {
        const string sql = "SELECT 1 + @@ROWCOUNT;";
        var stmt = ParseTestHelper.ParseSingle(sql);
        var rule = new FakeRule((s, _, p, _) => p.Add(GlobalVarFinder.FindIn(s), string.Empty));

        var r = new RewriteEngine(new IRewriteRule[] { rule }).Rewrite(stmt, sql, Ctx);
        Assert.Equal("SELECT 1 + ;", r.PatchedText);
    }

    [Fact]
    public void ShadowNamingContract_IsCentralized_AndNonceValidated()
    {
        var ctx = new RewriteContext("7f3a");
        Assert.Equal("@__dbg7f3a_sh_rowcount", ctx.ShadowVariable(ShadowKind.Rowcount));
        Assert.Equal("@__dbg7f3a_sh_err_message", ctx.ShadowVariable(ShadowKind.ErrMessage));
        Assert.Throws<ArgumentException>(() => new RewriteContext("bad nonce!"));
    }
}
