// DESIGN §7.4 rule R1 — table-variable references `@t` → the frame-renamed #temp
// realization (`#__dbgtv_{frame}_{name}`, §9). The DECLARE itself is interpreted, never
// rewritten (SuSubKind.TableVarDeclare is a no-op SU; the realization is hoisted to
// frame init/push — M4 design notes D7), so this rule only ever patches REFERENCES:
// ScriptDom represents a table variable used in a table position (FROM @t, INSERT INTO
// @t, UPDATE @t, DELETE @t …) as VariableTableReference — a scalar @x in an expression
// is a VariableReference under an ExpressionNode and never reaches this visitor, so
// scalars are structurally safe. Resolution walks the frame chain innermost-first
// (ITempNameScope); a miss stays unpatched, which faithfully produces the engine's own
// "must declare the table variable" error (1087) exactly where native would.
using System.Collections.Generic;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using TsqlDbg.Core.Interpreter;

namespace TsqlDbg.Core.Rewrite.Rules;

public sealed class R1TableVariableRule : IRewriteRule
{
    public RuleId Id => RuleId.R1;

    public void Collect(TSqlFragment statement, RewriteContext context, SpanPatchCollector patches, ISet<ShadowKind> requiredShadows)
    {
        if (context.TempNames is null)
            return;

        statement.Accept(new Visitor(context.TempNames, patches));
    }

    private sealed class Visitor : TSqlFragmentVisitor
    {
        private readonly ITempNameScope _scope;
        private readonly SpanPatchCollector _patches;

        public Visitor(ITempNameScope scope, SpanPatchCollector patches)
        {
            _scope = scope;
            _patches = patches;
        }

        public override void Visit(VariableTableReference node)
        {
            var name = node.Variable?.Name;
            if (string.IsNullOrEmpty(name))
                return;

            var physical = _scope.ResolveReference(name, TempObjectKind.TableVariable);
            if (physical is not null)
                _patches.Add(node.Variable!, RewriteContext.BracketIdentifier(physical));
        }
    }
}
