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
    public static string? TryGetTargetTableName(TSqlFragment? fragment)
    {
        var target = fragment switch
        {
            InsertStatement { InsertSpecification.Target: var t } => t,
            UpdateStatement { UpdateSpecification.Target: var t } => t,
            DeleteStatement { DeleteSpecification.Target: var t } => t,
            MergeStatement { MergeSpecification.Target: var t } => t,
            _ => null,
        };

        if (target is not NamedTableReference { SchemaObject.BaseIdentifier.Value: { Length: > 0 } name } named
            || name[0] == '#')
        {
            return null;
        }

        var schema = named.SchemaObject.SchemaIdentifier?.Value;
        return schema is { Length: > 0 } ? $"{schema}.{name}" : name;
    }
}
