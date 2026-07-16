// DESIGN §21 C2 (M7 hardening): "Triggers/UDFs/computed cols/cascades atomic |
// won't-fix | step-over; console note when a DML target has triggers (lazy
// sys.triggers check, cached)". A small pure sibling of InsertFamilyClassifier.
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace TsqlDbg.Core.Interpreter;

/// <summary>
/// Extracts the target table/view name for a plain DML statement against a NAMED
/// object — never a local temp table (<c>#…</c>, cannot carry triggers in SQL Server
/// at all) or a table variable (<see cref="VariableTableReference"/>, same reason —
/// the type pattern below already excludes it structurally). Session caches the
/// <c>sys.triggers</c> lookup per resolved name and fires the C2 console note at most
/// once per object per session; this classifier only ever answers "what is the
/// target," never "does it have triggers."
/// </summary>
public static class DmlTargetClassifier
{
    // The DML statements whose target this classifier reads — INSERT/UPDATE/DELETE/MERGE.
    private static TableReference? DmlTarget(TSqlFragment? fragment) => fragment switch
    {
        InsertStatement { InsertSpecification.Target: var t } => t,
        UpdateStatement { UpdateSpecification.Target: var t } => t,
        DeleteStatement { DeleteSpecification.Target: var t } => t,
        MergeStatement { MergeSpecification.Target: var t } => t,
        _ => null,
    };

    public static string? TryGetTargetTableName(TSqlFragment? fragment)
    {
        if (DmlTarget(fragment) is not NamedTableReference { SchemaObject.BaseIdentifier.Value: { Length: > 0 } name } named
            || name[0] == '#')
        {
            return null;
        }

        var schema = named.SchemaObject.SchemaIdentifier?.Value;
        return schema is { Length: > 0 } ? $"{schema}.{name}" : name;
    }

    /// <summary>
    /// A62 (§11.3 step 2 / C9): the <c>@var</c> name a DML statement WRITES when its target is a
    /// table VARIABLE (<see cref="VariableTableReference"/>) — the complement of
    /// <see cref="TryGetTargetTableName"/>, which deliberately excludes variable targets. Used by
    /// the step-into TVP guard: a READONLY table-valued parameter cannot be written (native msg
    /// 10700, a compile error), so a body that writes one must be stepped over, not into.
    /// Returns the variable name (with its leading <c>@</c>), or null when the DML target is not a
    /// table variable (a named table, a <c>#temp</c>) or the statement is not DML at all.
    /// </summary>
    public static string? TryGetTargetVariableName(TSqlFragment? fragment)
        => DmlTarget(fragment) is VariableTableReference { Variable.Name: { Length: > 0 } name }
            ? name
            : null;

    /// <summary>
    /// A62 (§11.3 step 2 / C9, F3): every table VARIABLE a DML statement WRITES. That is more
    /// than <see cref="TryGetTargetVariableName"/>'s direct <c>INSERT/UPDATE/DELETE/MERGE @r</c>
    /// target: it also includes an <b>aliased</b> target (<c>UPDATE x … FROM @r AS x</c> — the
    /// one-part target name resolves through the FROM clause to a <see cref="VariableTableReference"/>)
    /// and an <c>OUTPUT … INTO @r</c> target. The step-into TVP guard uses this: a READONLY
    /// table-valued parameter cannot be written (native msg 10700, a COMPILE error — the whole
    /// batch never runs), so a body that writes one is stepped OVER, letting the engine
    /// compile-fail it. A table variable merely READ in a FROM/JOIN (legal — the callee only
    /// reads its TVP) is deliberately NOT returned.
    /// </summary>
    public static IEnumerable<string> GetWrittenTableVariableNames(TSqlFragment? fragment)
    {
        if (ModificationSpecification(fragment) is not { } spec)
        {
            yield break;
        }

        if (WrittenTargetVariable(spec) is { } target)
        {
            yield return target;
        }

        if (spec.OutputIntoClause?.IntoTable is VariableTableReference { Variable.Name: { Length: > 0 } into })
        {
            yield return into;
        }
    }

    private static DataModificationSpecification? ModificationSpecification(TSqlFragment? fragment) => fragment switch
    {
        InsertStatement s => s.InsertSpecification,
        UpdateStatement s => s.UpdateSpecification,
        DeleteStatement s => s.DeleteSpecification,
        MergeStatement s => s.MergeSpecification,
        _ => null,
    };

    private static string? WrittenTargetVariable(DataModificationSpecification spec) => spec.Target switch
    {
        // Direct: INSERT/UPDATE/DELETE/MERGE @r.
        VariableTableReference { Variable.Name: { Length: > 0 } direct } => direct,

        // Aliased: a one-part target name is a FROM-clause alias — only the VariableTableReference
        // it aliases is written (other tables in the FROM are read). Only UPDATE/DELETE have a
        // FROM clause to alias a target through; a real one-part table name (no matching alias)
        // resolves to null, which is correct (not a table-variable write).
        NamedTableReference { SchemaObject: { SchemaIdentifier: null, BaseIdentifier.Value: { Length: > 0 } alias } }
            when spec is UpdateDeleteSpecificationBase { FromClause: { } from }
            => ResolveAliasedVariable(from, alias),

        _ => null,
    };

    private static string? ResolveAliasedVariable(FromClause from, string alias)
    {
        var finder = new VariableTableReferenceFinder();
        from.Accept(finder);
        foreach (var vtr in finder.Found)
        {
            if (vtr.Alias?.Value is { } a
                && string.Equals(a, alias, StringComparison.OrdinalIgnoreCase)
                && vtr.Variable?.Name is { Length: > 0 } name)
            {
                return name;
            }
        }

        return null;
    }

    private sealed class VariableTableReferenceFinder : TSqlFragmentVisitor
    {
        public List<VariableTableReference> Found { get; } = new();

        public override void ExplicitVisit(VariableTableReference node)
        {
            Found.Add(node);
            base.ExplicitVisit(node);
        }
    }
}
