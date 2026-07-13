// DESIGN §7.4 rule R6 — SCOPE_IDENTITY() -> shadow substitute. SCOPE_IDENTITY() parses
// as a niladic FunctionCall (verified against the installed ScriptDom 180.37.3:
// FunctionName.Value == "SCOPE_IDENTITY", empty Parameters) rather than a
// GlobalVariableExpression like @@ROWCOUNT/@@ERROR (R4/R5).
using System.Collections.Generic;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace TsqlDbg.Core.Rewrite.Rules;

public sealed class R6ScopeIdentityRule : IRewriteRule
{
    public RuleId Id => RuleId.R6;

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

        public override void Visit(FunctionCall node)
        {
            if (node.Parameters.Count == 0 &&
                string.Equals(node.FunctionName?.Value, "SCOPE_IDENTITY", System.StringComparison.OrdinalIgnoreCase))
            {
                _patches.Add(node, _context.ShadowVariable(ShadowKind.ScopeIdentity));
                _shadows.Add(ShadowKind.ScopeIdentity);
            }
        }
    }
}
