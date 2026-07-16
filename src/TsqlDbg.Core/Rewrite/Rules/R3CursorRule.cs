// DESIGN §7.4 rule R3 — cursors become connection-global with frame-unique names (§9):
//   DECLARE c CURSOR [LOCAL] …  →  DECLARE [c__f{n}_c] CURSOR GLOBAL …
//   OPEN/FETCH/CLOSE/DEALLOCATE c and WHERE CURRENT OF c  →  patched via CursorId.
// A63: cursor VARIABLES (DECLARE @c CURSOR) ride the same model — a CursorId whose Name is a
// VariableReference (not an Identifier) is resolved here to the reified GLOBAL cursor registered
// at the `SET @c = CURSOR FOR` site (§9). That SET itself is NOT rewritten by R3 — its `SET…CURSOR`
// keywords are not AST fragments, so §7.4 invariant 1 forbids a span patch — the interpreter
// composes a generated `DECLARE [phys] CURSOR GLOBAL <options> FOR <select>` batch instead
// (ComposedBatchBuilder.BuildForCursorVariableAssign), which reconstructs the options + FOR and
// rewrites only the SELECT body (its #temp/@t refs) through the normal R1/R2 pipeline.
// A LOCAL cursor declared inside one of our composed batches would die at that batch's
// end — GLOBAL is what makes the per-SU model work; frame-unique names keep recursion
// safe (§11.4). When the DECLARE carries neither LOCAL nor GLOBAL the database's
// CURSOR_DEFAULT decides: the session checks it is GLOBAL once at init and refuses
// otherwise (M4 design notes D7 — a keyword insertion has no AST anchor to patch).
// FETCH … INTO @vars is untouched (ordinary frame vars, §9); @@FETCH_STATUS is live
// truth (§7.4 never-rewrite list) — our wrapper SELECTs don't FETCH, so it survives
// between batches on the shared connection.
using System.Collections.Generic;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using TsqlDbg.Core.Interpreter;

namespace TsqlDbg.Core.Rewrite.Rules;

public sealed class R3CursorRule : IRewriteRule
{
    public RuleId Id => RuleId.R3;

    public void Collect(TSqlFragment statement, RewriteContext context, SpanPatchCollector patches, ISet<ShadowKind> requiredShadows)
    {
        if (context.TempNames is null)
            return;

        if (statement is DeclareCursorStatement declare)
        {
            if (declare.Name is { } name)
            {
                var physical = RewriteContext.CursorPhysicalName(name.Value, context.TempNames.CurrentFrameOrdinal);
                patches.Add(name, RewriteContext.BracketIdentifier(physical));
            }

            foreach (var option in declare.CursorDefinition?.Options ?? (IList<CursorOption>)System.Array.Empty<CursorOption>())
            {
                if (option.OptionKind == CursorOptionKind.Local)
                    patches.Add(option, "GLOBAL");
            }

            // The FOR SELECT body may still reference #temps/@t — other rules cover it;
            // no CursorId exists inside a DECLARE, so the visitor below is creation-safe.
        }

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

        public override void Visit(CursorId node)
        {
            // A named cursor's CursorId carries a plain Identifier; a cursor VARIABLE's (A63)
            // carries a VariableReference in Name.ValueExpression instead. Both resolve through
            // the same §9 registry — the named cursor by its source name, the variable by its
            // '@'-prefixed name (registered at the SET site with the same key).
            if (node.Name?.Identifier is { Value: { Length: > 0 } } identifier)
            {
                var physical = _scope.ResolveReference(identifier.Value, TempObjectKind.Cursor);
                if (physical is not null)
                    _patches.Add(identifier, RewriteContext.BracketIdentifier(physical));
            }
            else if (node.Name?.ValueExpression is VariableReference { Name: { Length: > 0 } variableName } variableRef)
            {
                var physical = _scope.ResolveReference(variableName, TempObjectKind.Cursor);
                if (physical is not null)
                    _patches.Add(variableRef, RewriteContext.BracketIdentifier(physical));
            }
        }
    }
}
