using TsqlDbg.Core.Execution;
using TsqlDbg.Core.State;
using Xunit;

namespace TsqlDbg.Core.Tests.State;

// DESIGN §8.1/§9 (A59): the server formats the type; the client only asks and reads. These
// tests pin the asking (the describe batch) and the reading (metadata -> realization DDL).
// The live halves are P29/P30 and Fact34UserTypeLiveTests.
public class UserTypeResolutionTests
{
    private static readonly UserTypeEntry Name = new("dbo", "CustomerName", UserTypeKind.Alias);
    private static readonly UserTypeEntry Rows = new("dbo", "OrderRows", UserTypeKind.Table);

    [Fact]
    public void AliasBaseTypeQuery_DescribesOneColumnPerType()
    {
        var sql = UserTypeResolution.BuildAliasBaseTypeQuery(new[]
        {
            Name,
            new UserTypeEntry("sales", "Region", UserTypeKind.Alias),
        });

        // One describe call for the whole frame, not one per type.
        Assert.Contains("sys.dm_exec_describe_first_result_set", sql);
        Assert.Contains("SELECT @a0 AS c0, @a1 AS c1", sql);
        Assert.Contains("@a0 [dbo].[CustomerName], @a1 [sales].[Region]", sql);
        Assert.Contains("ORDER BY column_ordinal", sql);
    }

    [Fact]
    public void ParseBaseTypes_KeepsOrdinalAlignment_AndKeepsCollationSeparate()
    {
        var resultSet = new ResultSet(
            new[] { "column_ordinal", "system_type_name", "collation_name" },
            new IReadOnlyList<object?>[]
            {
                new object?[] { 1, "nvarchar(50)", "SQL_Latin1_General_CP1_CI_AS" },
                new object?[] { 2, "decimal(9,2)", null },
            });

        var types = UserTypeResolution.ParseBaseTypes(resultSet, 2);

        // Type and collation stay APART. The state-table column needs the collation (tempdb's
        // collation may differ from the user database's, and a varchar would transcode on the
        // round trip); a CONVERT target must NOT have it — `CONVERT(nvarchar(50) COLLATE …,
        // @p)` is msg 156. Found live: the doomed-seed CONVERT is the site that says so.
        Assert.Equal("nvarchar(50)", types[0].TypeSql);
        Assert.Equal("SQL_Latin1_General_CP1_CI_AS", types[0].Collation);
        Assert.Equal("decimal(9,2)", types[1].TypeSql);
        Assert.Null(types[1].Collation);              // non-character: no collation at all
    }

    [Fact]
    public void ParseBaseTypes_Throws_WhenTheServerDescribedNothing()
    {
        var empty = new ResultSet(new[] { "column_ordinal" }, Array.Empty<IReadOnlyList<object?>>());
        var ex = Assert.Throws<InvalidOperationException>(() => UserTypeResolution.ParseBaseTypes(empty, 1));
        Assert.Contains("did not describe a base type", ex.Message);
    }

    [Fact]
    public void ParseTableType_ThenBuildColumnDdl_RebuildsTheWholeStructure()
    {
        // The four result sets of BuildTableTypeQuery, transcribed from the live shape of
        // fact 34f (docs/engine-facts/fact34_user_defined_types.sql).
        var describe = new ResultSet(
            new[] { "column_ordinal", "name", "system_type_name", "collation_name" },
            new IReadOnlyList<object?>[]
            {
                new object?[] { 1, "id", "int", null },
                // NOTE: an alias-typed column of the table type — the describe oracle has
                // ALREADY resolved it to its base type (fact 34d), which is what makes the
                // realization legal in tempdb at all.
                new object?[] { 2, "nm", "nvarchar(50)", "SQL_Latin1_General_CP1_CI_AS" },
                new object?[] { 3, "qty", "decimal(9,3)", null },
                new object?[] { 4, "calc", "decimal(11,3)", null },
            });

        var columns = new ResultSet(
            new[] { "name", "is_nullable", "is_identity", "seed_value", "increment_value", "computed_definition", "is_persisted", "default_definition" },
            new IReadOnlyList<object?>[]
            {
                new object?[] { "id", false, true, 10L, 5L, null, null, null },
                new object?[] { "nm", false, false, null, null, null, null, null },
                new object?[] { "qty", true, false, null, null, null, null, "((1.5))" },
                new object?[] { "calc", true, false, null, null, "([qty]*(2))", false, null },
            });

        var keys = new ResultSet(
            new[] { "index_id", "is_primary_key", "type_desc", "name", "is_descending_key" },
            new IReadOnlyList<object?>[]
            {
                new object?[] { 1, true, "CLUSTERED", "id", false },
                new object?[] { 2, false, "NONCLUSTERED", "nm", false },
            });

        var checks = new ResultSet(
            new[] { "definition" },
            new IReadOnlyList<object?>[] { new object?[] { "([qty]>(0))" } });

        var definition = UserTypeResolution.ParseTableType(Rows, new[] { describe, columns, keys, checks });
        var ddl = definition.BuildColumnDdl();

        Assert.Equal(
            "[id] int IDENTITY(10,5) NOT NULL, " +
            "[nm] nvarchar(50) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL, " +
            "[qty] decimal(9,3) NULL DEFAULT ((1.5)), " +
            "[calc] AS ([qty]*(2)), " +
            "PRIMARY KEY CLUSTERED ([id] ASC), " +
            "UNIQUE NONCLUSTERED ([nm] ASC), " +
            "CHECK ([qty]>(0))",
            ddl);

        // Constraints are UNNAMED on purpose: a table type's constraint names are unique in
        // the user database, but two frames realizing the same type into tempdb would collide.
        Assert.DoesNotContain("CONSTRAINT", ddl);

        // IDENTITY and computed columns cannot be supplied to a table variable (fact 34e),
        // so they are excluded from the §9 TVP materialization insert — which is exactly why
        // identity values are regenerated there (C28).
        Assert.Equal(new[] { "nm", "qty" }, definition.InsertableColumns);
    }

    [Fact]
    public void ParseTableType_Throws_WhenTheMetadataIsIncomplete()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            UserTypeResolution.ParseTableType(Rows, new[]
            {
                new ResultSet(Array.Empty<string>(), Array.Empty<IReadOnlyList<object?>>()),
            }));
        Assert.Contains("of the 4 expected", ex.Message);
    }

    [Fact]
    public void TableTypeQuery_AsksForEveryStructuralFeature()
    {
        var sql = UserTypeResolution.BuildTableTypeQuery(Rows);

        Assert.Contains("sys.table_types", sql);
        Assert.Contains("sys.identity_columns", sql);      // IDENTITY(seed, increment)
        Assert.Contains("sys.computed_columns", sql);      // AS (expr)
        Assert.Contains("sys.default_constraints", sql);   // DEFAULT (expr)
        Assert.Contains("sys.indexes", sql);               // PRIMARY KEY / UNIQUE
        Assert.Contains("sys.check_constraints", sql);     // CHECK (expr)
        Assert.Contains("@t [dbo].[OrderRows] READONLY", sql);
    }
}
