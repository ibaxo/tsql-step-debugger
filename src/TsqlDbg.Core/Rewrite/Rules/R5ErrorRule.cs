// DESIGN §7.4 rule R5 — @@ERROR -> shadow substitute. Mirrors R4RowcountRule.cs's
// shape exactly, per phase0-integration-notes.md's instruction for Sonnet-authored
// rules on top of the Phase-0 span-patch engine.
using System.Collections.Generic;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace TsqlDbg.Core.Rewrite.Rules;

public sealed class R5ErrorRule : IRewriteRule
{
    public RuleId Id => RuleId.R5;

    public void Collect(TSqlFragment statement, RewriteContext context, SpanPatchCollector patches, ISet<ShadowKind> requiredShadows)
    {
        var visitor = new Visitor(context, patches, requiredShadows);
        statement.Accept(visitor);
    }

    private sealed class Visitor : TSqlFragmentVisitor
    {
        private readonly RewriteContext _context;
        private readonly SpanPatchCollector _patches;
        private readonly ISet<ShadowKind> _shadows;

        public Visitor(RewriteContext context, SpanPatchCollector patches, ISet<ShadowKind> shadows)
        {
            _context = context;
            _patches = patches;
            _shadows = shadows;
        }

        public override void Visit(GlobalVariableExpression node)
        {
            if (string.Equals(node.Name, "@@ERROR", System.StringComparison.OrdinalIgnoreCase))
            {
                _patches.Add(node, _context.ShadowVariable(ShadowKind.Error));
                _shadows.Add(ShadowKind.Error);
            }
        }
    }
}
