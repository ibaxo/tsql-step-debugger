using TsqlDbg.Core.Rewrite;

namespace TsqlDbg.Core.State;

// DESIGN §9 (A59): a user table type's structure, reconstructed from the catalog
// (fact 34f), and the #temp realization DDL generated from it.
//
// A `DECLARE @t dbo.MyTable` is a table variable whose shape lives in the DATABASE rather
// than in the source text — so where R1's realization of `DECLARE @t TABLE(…)` can slice
// the definition straight out of the script, this one has to rebuild it. Everything after
// the DDL (R1 rewriting, the registry, batch-boundary teardown, C25) is the existing
// table-variable path verbatim.

public sealed record TableTypeKeyColumn(string Column, bool Descending);

public sealed record TableTypeUniqueKey(
    bool IsPrimaryKey, bool IsClustered, IReadOnlyList<TableTypeKeyColumn> Keys);

public sealed record TableTypeColumn(
    string Name,
    string TypeSql,                    // base-resolved + COLLATE'd (§8.1); empty for a computed column
    bool IsNullable,
    bool IsIdentity,
    long IdentitySeed,
    long IdentityIncrement,
    string? ComputedDefinition,
    bool IsPersisted,
    string? DefaultDefinition)
{
    /// <summary>
    /// A column a value can be supplied for. IDENTITY and computed columns cannot be: a
    /// table variable rejects `SET IDENTITY_INSERT` outright (syntax error, fact 34e), which
    /// is what makes the §9 TVP materialization regenerate identity values — caveat C28.
    /// </summary>
    public bool IsInsertable => !IsIdentity && ComputedDefinition is null;
}

public sealed record TableTypeDefinition(
    UserTypeEntry Type,
    IReadOnlyList<TableTypeColumn> Columns,
    IReadOnlyList<TableTypeUniqueKey> UniqueKeys,
    IReadOnlyList<string> CheckDefinitions)
{
    public IReadOnlyList<string> InsertableColumns =>
        Columns.Where(c => c.IsInsertable).Select(c => c.Name).ToList();

    /// <summary>
    /// The IDENTITY column's name, or null. Two consumers, both in §9's TVP materialization:
    /// it is the <c>ORDER BY</c> that makes C28's "contiguous rows keep their values" a
    /// guarantee rather than a plan-shape accident (identity is assigned in insert order,
    /// and only an explicit ORDER BY fixes that order in an INSERT…SELECT); and its mere
    /// presence means the materialization MOVES the connection's identity chain (fact 34h),
    /// which the session must account for.
    /// </summary>
    public string? IdentityColumn => Columns.FirstOrDefault(c => c.IsIdentity)?.Name;

    /// <summary>
    /// The inside of the realization's `CREATE TABLE #… (<here>)` — the same slot R1 fills
    /// with the source slice of a `DECLARE @t TABLE(…)` definition, so the realization path
    /// is shared unchanged.
    ///
    /// Constraints are emitted UNNAMED. A table type's constraint names are unique in the
    /// user database; two frames (or two sessions) realizing the same type into tempdb would
    /// collide on them, so the engine's auto-generated names are used instead.
    /// </summary>
    public string BuildColumnDdl()
    {
        var parts = new List<string>(Columns.Count + UniqueKeys.Count + CheckDefinitions.Count);

        foreach (var column in Columns)
        {
            if (column.ComputedDefinition is { } computed)
            {
                parts.Add($"{Bracket(column.Name)} AS {computed}{(column.IsPersisted ? " PERSISTED" : string.Empty)}");
                continue;
            }

            var identity = column.IsIdentity
                ? $" IDENTITY({column.IdentitySeed},{column.IdentityIncrement})"
                : string.Empty;
            var nullability = column.IsNullable ? " NULL" : " NOT NULL";
            var @default = column.DefaultDefinition is { } d ? $" DEFAULT {d}" : string.Empty;
            parts.Add($"{Bracket(column.Name)} {column.TypeSql}{identity}{nullability}{@default}");
        }

        foreach (var key in UniqueKeys)
        {
            var kind = key.IsPrimaryKey ? "PRIMARY KEY" : "UNIQUE";
            var clustering = key.IsClustered ? "CLUSTERED" : "NONCLUSTERED";
            var columns = string.Join(", ", key.Keys.Select(k => $"{Bracket(k.Column)} {(k.Descending ? "DESC" : "ASC")}"));
            parts.Add($"{kind} {clustering} ({columns})");
        }

        foreach (var check in CheckDefinitions)
        {
            // sys.check_constraints.definition is already parenthesized: ([qty]>(0)).
            parts.Add($"CHECK {check}");
        }

        return string.Join(", ", parts);
    }

    /// <summary>A column name is a catalog string, not a source slice — it can contain a
    /// `]` (or a space, or a quote), and an unescaped one is msg 102.</summary>
    public static string Bracket(string name) => RewriteContext.BracketIdentifier(name);
}
