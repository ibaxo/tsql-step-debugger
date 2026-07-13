// DESIGN §7.4 rule R7 — ERROR_NUMBER/SEVERITY/STATE/LINE/PROCEDURE/MESSAGE() → shadow
// substitutes from the ACTIVE error context (§10.2). Applies only "while an error
// context is active for the current execution point" (dynamic extent) — the session
// flips RewriteContext.ErrorContextActive as its ErrorContextStack changes; when
// inactive this rule is silent and an unrewritten ERROR_*() call inside our synthetic
// TRY faithfully reads NULL (Appendix C fact 7's confirmed half). Mirrors R4's shape.
// R8 falls out structurally: ERROR_*() inside a string literal is a StringLiteral node,
// never a FunctionCall — the visitor is silent; indirect consumers (stepped-over
// modules, dynamic SQL) are covered by §10.7 re-materialization instead (M3, fact 19).
using System;
using System.Collections.Generic;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace TsqlDbg.Core.Rewrite.Rules;

public sealed class R7ErrorFunctionsRule : IRewriteRule
{
    public RuleId Id => RuleId.R7;

    private static readonly Dictionary<string, ShadowKind> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ERROR_NUMBER"] = ShadowKind.ErrNumber,
        ["ERROR_SEVERITY"] = ShadowKind.ErrSeverity,
        ["ERROR_STATE"] = ShadowKind.ErrState,
        ["ERROR_LINE"] = ShadowKind.ErrLine,
        ["ERROR_PROCEDURE"] = ShadowKind.ErrProcedure,
        ["ERROR_MESSAGE"] = ShadowKind.ErrMessage,
    };

    public void Collect(TSqlFragment statement, RewriteContext context, SpanPatchCollector patches, ISet<ShadowKind> requiredShadows)
    {
        if (!context.ErrorContextActive)
            return;

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
            // The ERROR_*() intrinsics are bare, zero-argument calls: no CallTarget
            // (a schema-qualified dbo.ERROR_MESSAGE() is a user UDF, not the intrinsic)
            // and no parameters.
            if (node.CallTarget is not null || node.Parameters.Count > 0)
                return;

            if (Map.TryGetValue(node.FunctionName.Value, out var kind))
            {
                _patches.Add(node, _context.ShadowVariable(kind));
                _shadows.Add(kind);
            }
        }
    }
}
