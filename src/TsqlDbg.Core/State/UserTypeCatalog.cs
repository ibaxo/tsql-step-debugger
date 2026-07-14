using Microsoft.SqlServer.TransactSql.ScriptDom;
using TsqlDbg.Core.Execution;
using TsqlDbg.Core.Rewrite;

namespace TsqlDbg.Core.State;

// DESIGN §4 step 2a (A59): the database's user-defined types, read once at session init
// and refreshed after an executed CREATE TYPE / DROP TYPE.
//
// Why the server has to be asked at all: `DECLARE @t dbo.X` carries NOTHING in its syntax
// that says whether dbo.X is an alias scalar type, a table type, or an assembly type
// (engine fact 34, rider 3) — ScriptDom hands back a UserDataTypeReference either way. The
// catalog is the only thing that can tell them apart, which is why the classification
// happens at frame init (server-connected) rather than at parse time.
//
// Only USER types are fetched: ScriptDom already separates a system type
// (SqlDataTypeReference) from a named one (UserDataTypeReference), so a system-type keyword
// list would be dead weight (fact 34g). A named type absent from the catalog is passed
// through to the server untouched — it errors exactly as it did before A59.
public enum UserTypeKind
{
    /// <summary>CREATE TYPE dbo.X FROM nvarchar(50) — a scalar with a base type (§8.1).</summary>
    Alias,

    /// <summary>CREATE TYPE dbo.X AS TABLE (…) — declares a table variable, not a scalar (§8.2/§9).</summary>
    Table,

    /// <summary>CREATE TYPE dbo.X EXTERNAL NAME … — refused with a named message (§8.2).</summary>
    Assembly,
}

/// <param name="IsDefaultSchema">The type's schema IS the session's current default schema
/// (<c>SCHEMA_NAME()</c>, evaluated in the session's — possibly §16-impersonated — security
/// context). Load-bearing for bare-name resolution; see <see cref="UserTypeCatalog.TryResolve"/>.</param>
public sealed record UserTypeEntry(string Schema, string Name, UserTypeKind Kind, bool IsDefaultSchema = false)
{
    /// <summary>Bracket-quoted two-part name — what the generated SQL always emits. A `]` in
    /// either part is doubled: an unescaped `[dbo].[we]ird]` is msg 102 (probed).</summary>
    public string QualifiedName =>
        $"{RewriteContext.BracketIdentifier(Schema)}.{RewriteContext.BracketIdentifier(Name)}";
}

public sealed class UserTypeCatalog
{
    // DESIGN §4 step 2a: piggybacked on the init round trip (never its own), so the
    // FakeStatementExecutor-scripted unit tests (§20.2) keep their exact call sequences.
    // SCHEMA_NAME() rides the SAME result set as a per-row flag rather than a second one,
    // for the same reason — an extra set would shift every scripted init sequence.
    public const string Query =
        "SELECT s.name AS type_schema, t.name AS type_name, t.is_table_type, t.is_assembly_type, " +
        "CASE WHEN s.name = SCHEMA_NAME() THEN 1 ELSE 0 END AS is_default_schema " +
        "FROM sys.types t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE t.is_user_defined = 1;";

    public static readonly UserTypeCatalog Empty = new(Array.Empty<UserTypeEntry>());

    private readonly Dictionary<string, UserTypeEntry> _byQualifiedName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, UserTypeEntry> _byBareName = new(StringComparer.OrdinalIgnoreCase);

    public UserTypeCatalog(IEnumerable<UserTypeEntry> entries)
    {
        foreach (var entry in entries)
        {
            _byQualifiedName[$"{entry.Schema}.{entry.Name}"] = entry;

            // Native bare-name resolution is EXACTLY two probes: the session's default
            // schema, then dbo — nothing else (probed: a type in some third schema is msg
            // 2715 even when its name is unique in the database). Resolving anything wider
            // would make the debugger accept `DECLARE @t MyType` where the engine refuses
            // it, and — worse — bind a same-named type from the wrong schema, silently
            // realizing the wrong table structure. So: default schema wins, dbo is the
            // fallback, and a name in neither does not resolve at all (it passes through
            // and the server produces its own 2715, which is the faithful outcome).
            if (entry.IsDefaultSchema
                || (!_byBareName.ContainsKey(entry.Name)
                    && string.Equals(entry.Schema, "dbo", StringComparison.OrdinalIgnoreCase)))
            {
                _byBareName[entry.Name] = entry;
            }
        }
    }

    public int Count => _byQualifiedName.Count;

    /// <summary>
    /// Resolves a DECLARE's data type against the catalog. Returns false for every system
    /// type (which never reaches here — ScriptDom models those as SqlDataTypeReference) and
    /// for any named type the database does not define, both of which pass through untouched.
    /// </summary>
    public bool TryResolve(DataTypeReference? dataType, out UserTypeEntry entry)
    {
        entry = null!;
        if (dataType is not UserDataTypeReference user)
        {
            return false;
        }

        var identifiers = user.Name?.Identifiers;
        if (identifiers is null || identifiers.Count == 0)
        {
            return false;
        }

        // A type name takes AT MOST one prefix: `db.schema.Type` is msg 117 natively
        // (probed), not a cross-database reference. Reading it positionally as schema.name
        // would run code the engine refuses — pass it through and let the server say 117.
        if (identifiers.Count > 2)
        {
            return false;
        }

        var name = identifiers[^1].Value;
        if (identifiers.Count == 2)
        {
            return _byQualifiedName.TryGetValue($"{identifiers[0].Value}.{name}", out entry!);
        }

        return _byBareName.TryGetValue(name, out entry!);
    }

    /// <summary>Reads the <see cref="Query"/> result set; a null/absent set yields <see cref="Empty"/>.</summary>
    public static UserTypeCatalog FromResultSet(ResultSet? resultSet)
    {
        if (resultSet is null || resultSet.Rows.Count == 0)
        {
            return Empty;
        }

        var entries = new List<UserTypeEntry>(resultSet.Rows.Count);
        foreach (var row in resultSet.Rows)
        {
            if (row.Count < 5 || row[0] is null || row[1] is null)
            {
                continue;
            }

            var kind = Convert.ToBoolean(row[2]) ? UserTypeKind.Table
                : Convert.ToBoolean(row[3]) ? UserTypeKind.Assembly
                : UserTypeKind.Alias;
            entries.Add(new UserTypeEntry(
                row[0]!.ToString()!, row[1]!.ToString()!, kind, IsDefaultSchema: Convert.ToBoolean(row[4])));
        }

        return new UserTypeCatalog(entries);
    }
}
