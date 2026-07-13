// DESIGN §7.4 rule R2 (A20, ratified 2026-07-06 — renames only on COLLISION):
// Two distinct treatments inside one statement:
//   - the CREATE TABLE #x target is renamed to `#x__f{frame}` ONLY when a live outer
//     registry entry already claims #x (the flattened connection cannot hold two
//     same-named session-scoped temps, where native nested proc scopes can); every
//     other create — including all of frame 0's — keeps its ORIGINAL physical name.
//     Original names are what make a STEPPED-OVER callee fully native: its compiled
//     body references caller #temps by original name, and its own CREATE #x nests
//     under the engine's real proc-scoping (docs/archive/reviews/m5-d5-oracle-free-exec-fable.md §4).
//   - every other #-reference (FROM/JOIN targets, DML targets, DROP TABLE) resolves
//     through the frame chain innermost-first (a non-collided entry's physical name IS
//     its original name — the patch is skipped as a no-op); a miss stays UNPATCHED —
//     faithful by construction: the server then raises the same not-found class
//     (208 / compile, §10.1 fact-1b path) the native run raises when the table
//     genuinely doesn't exist at that point.
// R8: #names inside string literals (OBJECT_ID('tempdb..#x'), dynamic SQL) are never
// patched — StringLiteral nodes are not SchemaObjectName; under A20 the residual only
// bites COLLIDED (renamed) tables — original-named ones probe correctly by nature.
using System.Collections.Generic;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using TsqlDbg.Core.Interpreter;

namespace TsqlDbg.Core.Rewrite.Rules;

public sealed class R2TempTableRule : IRewriteRule
{
    public RuleId Id => RuleId.R2;

    public void Collect(TSqlFragment statement, RewriteContext context, SpanPatchCollector patches, ISet<ShadowKind> requiredShadows)
    {
        if (context.TempNames is null)
            return;

        // The creation target (when this statement IS a temp-table create site: a
        // CREATE TABLE #x, or A24's SELECT ... INTO #x): A20 — minted `__f{frame}`
        // name only on collision with a live outer entry; original name (no patch)
        // otherwise. The visitor below must skip this identifier instance either way
        // (a colliding create must NOT resolve to the outer entry).
        Identifier? createTarget = statement switch
        {
            CreateTableStatement { SchemaObjectName.BaseIdentifier: { } id } => id,
            SelectStatement { Into.BaseIdentifier: { } id } => id,
            _ => null,
        };

        if (createTarget is not null && IsTempName(createTarget.Value))
        {
            if (context.TempNames.HasLiveTempTable(createTarget.Value))
            {
                var physical = RewriteContext.TempTablePhysicalName(createTarget.Value, context.TempNames.CurrentFrameOrdinal);
                patches.Add(createTarget, RewriteContext.BracketIdentifier(physical));
            }
        }
        else
        {
            createTarget = null;
        }

        statement.Accept(new Visitor(context.TempNames, patches, createTarget));
    }

    private static bool IsTempName(string? name)
        => name is { Length: > 1 } && name[0] == '#' && name[1] != '#';   // ## global temp: never renamed (shared by design)

    private sealed class Visitor : TSqlFragmentVisitor
    {
        private readonly ITempNameScope _scope;
        private readonly SpanPatchCollector _patches;
        private readonly Identifier? _createTarget;

        public Visitor(ITempNameScope scope, SpanPatchCollector patches, Identifier? createTarget)
        {
            _scope = scope;
            _patches = patches;
            _createTarget = createTarget;
        }

        public override void Visit(SchemaObjectName node)
        {
            var baseId = node.BaseIdentifier;
            if (baseId is null || ReferenceEquals(baseId, _createTarget) || !IsTempName(baseId.Value))
                return;

            // A20: a non-collided entry's physical name IS the original — patching
            // would be a no-op bracket; skip it (the resolve call itself still runs,
            // which is what keeps A14's doomed-capture live for original-named tables).
            var physical = _scope.ResolveReference(baseId.Value, TempObjectKind.TempTable);
            if (physical is not null && !string.Equals(physical, baseId.Value, StringComparison.OrdinalIgnoreCase))
                _patches.Add(baseId, RewriteContext.BracketIdentifier(physical));
        }
    }
}
