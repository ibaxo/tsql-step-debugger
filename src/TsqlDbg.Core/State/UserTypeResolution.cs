using TsqlDbg.Core.Execution;

namespace TsqlDbg.Core.State;

// DESIGN §8.1/§9 (A59): the server is the type oracle.
//
// An alias type's base type and a table type's column types are BOTH resolved by
// sys.dm_exec_describe_first_result_set, which returns `system_type_name` already
// formatted — `nvarchar(50)`, `decimal(9,3)`, `nvarchar(max)` — plus `collation_name`
// (engine fact 34d). That keeps §8.1's standing rule intact: the debugger performs no
// client-side SQL-type mapping, it asks. (Same server-as-oracle move as A47's PARSEONLY
// diagnostic.) Formatting these strings by hand from sys.types.max_length/precision/scale
// would mean re-implementing the engine's own type grammar — bytes-vs-characters, -1 =
// MAX, per-family scale rules — for no gain.
/// <summary>
/// DESIGN §8.1 (A59): an alias type's base type, as the server formats it, with its
/// collation kept apart — a column definition takes <c>COLLATE</c>, a <c>CONVERT</c> target
/// does not.
/// </summary>
public sealed record AliasBaseType(string TypeSql, string? Collation);

public static class UserTypeResolution
{
    /// <summary>
    /// One describe call covering every alias type a frame declares: a dummy SELECT with one
    /// parameter per type. Result: column_ordinal (1-based, ordered as <paramref name="aliasTypes"/>),
    /// system_type_name, collation_name.
    /// </summary>
    public static string BuildAliasBaseTypeQuery(IReadOnlyList<UserTypeEntry> aliasTypes)
    {
        if (aliasTypes.Count == 0)
        {
            throw new ArgumentException("No alias types to resolve.", nameof(aliasTypes));
        }

        var select = "SELECT " + string.Join(", ", aliasTypes.Select((_, i) => $"@a{i} AS c{i}"));
        var formals = string.Join(", ", aliasTypes.Select((t, i) => $"@a{i} {t.QualifiedName}"));
        return "SELECT column_ordinal, system_type_name, collation_name FROM " +
               $"sys.dm_exec_describe_first_result_set(N'{Quote(select)}', N'{Quote(formals)}', 0) " +
               "ORDER BY column_ordinal;";
    }

    /// <summary>
    /// Reads <see cref="BuildAliasBaseTypeQuery"/>'s result, ordinally aligned with the alias
    /// types passed in.
    ///
    /// Type and collation stay SEPARATE, because the two places the result is emitted do not
    /// accept the same string: a state-table COLUMN takes the collation (and needs it — the
    /// table lives in tempdb, and a user database with a different collation would otherwise
    /// transcode a <c>varchar</c> value on every round trip), while a <c>CONVERT</c> target
    /// takes only the bare type — <c>CONVERT(nvarchar(50) COLLATE …, @p)</c> is msg 156.
    /// </summary>
    public static IReadOnlyList<AliasBaseType> ParseBaseTypes(ResultSet resultSet, int expectedCount)
    {
        var types = new AliasBaseType?[expectedCount];
        foreach (var row in resultSet.Rows)
        {
            var ordinal = Convert.ToInt32(row[0]) - 1;      // describe is 1-based
            if (ordinal < 0 || ordinal >= expectedCount || row[1] is null)
            {
                continue;
            }

            var collation = row[2]?.ToString();
            types[ordinal] = new AliasBaseType(
                row[1]!.ToString()!,
                string.IsNullOrEmpty(collation) ? null : collation);
        }

        for (var i = 0; i < expectedCount; i++)
        {
            if (types[i] is null)
            {
                throw new InvalidOperationException(
                    $"The server did not describe a base type for alias type #{i} (§8.1) — " +
                    "the type exists in sys.types but could not be described.");
            }
        }

        return types!;
    }

    /// <summary>
    /// The four result sets that reconstruct a table type's structure (fact 34f): describe
    /// (column types), columns (nullability/identity/computed/default), unique keys, checks.
    /// </summary>
    public static string BuildTableTypeQuery(UserTypeEntry tableType)
    {
        var qualified = tableType.QualifiedName;
        return
            $"DECLARE @tt int = TYPE_ID(N'{Quote(qualified)}');\n" +
            "SELECT column_ordinal, name, system_type_name, collation_name FROM " +
            $"sys.dm_exec_describe_first_result_set(N'SELECT * FROM @t', N'@t {Quote(qualified)} READONLY', 0) " +
            "ORDER BY column_ordinal;\n" +
            "SELECT c.name, c.is_nullable, c.is_identity, ic.seed_value, ic.increment_value, " +
            "cc.definition AS computed_definition, cc.is_persisted, dc.definition AS default_definition\n" +
            "FROM sys.table_types tt\n" +
            "JOIN sys.columns c ON c.object_id = tt.type_table_object_id\n" +
            "LEFT JOIN sys.identity_columns ic ON ic.object_id = c.object_id AND ic.column_id = c.column_id\n" +
            "LEFT JOIN sys.computed_columns cc ON cc.object_id = c.object_id AND cc.column_id = c.column_id\n" +
            "LEFT JOIN sys.default_constraints dc ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id\n" +
            "WHERE tt.user_type_id = @tt ORDER BY c.column_id;\n" +
            "SELECT i.index_id, i.is_primary_key, i.type_desc, col.name, ic2.is_descending_key\n" +
            "FROM sys.table_types tt\n" +
            "JOIN sys.indexes i ON i.object_id = tt.type_table_object_id\n" +
            "JOIN sys.index_columns ic2 ON ic2.object_id = i.object_id AND ic2.index_id = i.index_id\n" +
            "JOIN sys.columns col ON col.object_id = ic2.object_id AND col.column_id = ic2.column_id\n" +
            // Non-unique indexes are a performance property only (§15) — a #temp realization
            // without them computes every identical result, so they are not reproduced.
            "WHERE tt.user_type_id = @tt AND i.is_unique = 1 ORDER BY i.index_id, ic2.key_ordinal;\n" +
            "SELECT ch.definition FROM sys.table_types tt\n" +
            "JOIN sys.check_constraints ch ON ch.parent_object_id = tt.type_table_object_id\n" +
            "WHERE tt.user_type_id = @tt;";
    }

    /// <summary>Reads <see cref="BuildTableTypeQuery"/>'s four result sets into a definition.</summary>
    public static TableTypeDefinition ParseTableType(UserTypeEntry tableType, IReadOnlyList<ResultSet> resultSets)
    {
        if (resultSets.Count < 4)
        {
            throw new InvalidOperationException(
                $"Table type {tableType.QualifiedName} returned {resultSets.Count} of the 4 expected " +
                "metadata result sets (§9/fact 34f).");
        }

        var typesByColumn = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in resultSets[0].Rows)
        {
            if (row[1] is null || row[2] is null)
            {
                continue;
            }

            typesByColumn[row[1]!.ToString()!] = WithCollation(row[2]!.ToString()!, row[3]);
        }

        var columns = new List<TableTypeColumn>(resultSets[1].Rows.Count);
        foreach (var row in resultSets[1].Rows)
        {
            var name = row[0]!.ToString()!;
            var computed = row[5] is null ? null : row[5]!.ToString();
            columns.Add(new TableTypeColumn(
                Name: name,
                // A computed column's type is its expression's — never emitted (§9).
                TypeSql: computed is not null ? string.Empty : typesByColumn.GetValueOrDefault(name)
                    ?? throw new InvalidOperationException(
                        $"The server described no type for column [{name}] of {tableType.QualifiedName} (§9)."),
                IsNullable: Convert.ToBoolean(row[1]),
                IsIdentity: Convert.ToBoolean(row[2]),
                IdentitySeed: row[3] is null ? 0 : Convert.ToInt64(row[3]),
                IdentityIncrement: row[4] is null ? 0 : Convert.ToInt64(row[4]),
                ComputedDefinition: computed,
                IsPersisted: row[6] is not null && Convert.ToBoolean(row[6]),
                DefaultDefinition: row[7] is null ? null : row[7]!.ToString()));
        }

        var keys = new List<TableTypeUniqueKey>();
        var keyColumnsByIndex = new Dictionary<int, List<TableTypeKeyColumn>>();
        var keyMeta = new Dictionary<int, (bool IsPrimaryKey, bool IsClustered)>();
        foreach (var row in resultSets[2].Rows)
        {
            var indexId = Convert.ToInt32(row[0]);
            keyMeta[indexId] = (Convert.ToBoolean(row[1]),
                string.Equals(row[2]?.ToString(), "CLUSTERED", StringComparison.OrdinalIgnoreCase));
            if (!keyColumnsByIndex.TryGetValue(indexId, out var list))
            {
                keyColumnsByIndex[indexId] = list = new List<TableTypeKeyColumn>();
            }

            list.Add(new TableTypeKeyColumn(row[3]!.ToString()!, Convert.ToBoolean(row[4])));
        }

        foreach (var (indexId, keyColumns) in keyColumnsByIndex.OrderBy(kv => kv.Key))
        {
            var (isPrimaryKey, isClustered) = keyMeta[indexId];
            keys.Add(new TableTypeUniqueKey(isPrimaryKey, isClustered, keyColumns));
        }

        var checks = resultSets[3].Rows
            .Where(r => r[0] is not null)
            .Select(r => r[0]!.ToString()!)
            .ToList();

        return new TableTypeDefinition(tableType, columns, keys, checks);
    }

    private static string WithCollation(string systemTypeName, object? collation)
    {
        var name = collation?.ToString();
        return string.IsNullOrEmpty(name) ? systemTypeName : $"{systemTypeName} COLLATE {name}";
    }

    /// <summary>Escapes a string for embedding in an N'…' literal.</summary>
    private static string Quote(string value) => value.Replace("'", "''");
}
