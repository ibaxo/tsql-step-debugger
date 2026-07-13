// DESIGN §7.4 rule R4 — @@ROWCOUNT → shadow substitute. This is the REFERENCE RULE:
// R5 (@@ERROR) and R6 (SCOPE_IDENTITY()) are Sonnet work and must mirror this shape —
// a stateless TSqlFragmentVisitor keyed on a specific AST node type, patches added only
// via SpanPatchCollector.Add(fragment, …). Phase-0 reference implementation (Fable).
//
// Why the R8-negatives fall out structurally (§7.4 invariant 1):
//   N'… @@ROWCOUNT …'   → StringLiteral node, not GlobalVariableExpression → visitor silent.
//   [@@ROWCOUNT]        → a (quoted) Identifier in a column/object reference → silent.
//   -- @@ROWCOUNT       → comment trivia, absent from the AST entirely → silent.
using System.Collections.Generic;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace TsqlDbg.Core.Rewrite.Rules;

public sealed class R4RowcountRule : IRewriteRule
{
    public RuleId Id => RuleId.R4;

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
            // GlobalVariableExpression covers all @@intrinsics; Name includes the '@@'.
            if (string.Equals(node.Name, "@@ROWCOUNT", System.StringComparison.OrdinalIgnoreCase))
            {
                _patches.Add(node, _context.ShadowVariable(ShadowKind.Rowcount));
                _shadows.Add(ShadowKind.Rowcount);
            }
        }
    }
}
