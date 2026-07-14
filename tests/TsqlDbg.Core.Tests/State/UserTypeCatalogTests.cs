using Microsoft.SqlServer.TransactSql.ScriptDom;
using TsqlDbg.Core.Execution;
using TsqlDbg.Core.State;
using TsqlDbg.Core.Tests.Interpreter;
using Xunit;

namespace TsqlDbg.Core.Tests.State;

// DESIGN §4 step 2a (A59): the catalog is the ONLY thing that can tell an alias type from a
// table type — `DECLARE @t dbo.X` says nothing either way (fact 34, rider 3). These tests
// pin the resolution rules, including the two shapes that must resolve to NOTHING (a system
// type, and a named type the database does not define), because those pass straight through
// to the server and any false positive there would corrupt a perfectly ordinary DECLARE.
public class UserTypeCatalogTests
{
    private static readonly UserTypeCatalog Catalog = new(new[]
    {
        new UserTypeEntry("dbo", "CustomerName", UserTypeKind.Alias),
        new UserTypeEntry("dbo", "OrderRows", UserTypeKind.Table),
        new UserTypeEntry("dbo", "Point", UserTypeKind.Assembly),
        new UserTypeEntry("sales", "Region", UserTypeKind.Alias),
    });

    private static DataTypeReference? DeclaredType(string declareSql)
    {
        var declare = (DeclareVariableStatement)ParseTestHelper.ParseSingle(declareSql);
        return declare.Declarations[0].DataType;
    }

    [Theory]
    [InlineData("DECLARE @n dbo.CustomerName;", UserTypeKind.Alias)]
    [InlineData("DECLARE @n [dbo].[CustomerName];", UserTypeKind.Alias)]
    [InlineData("DECLARE @n CustomerName;", UserTypeKind.Alias)]           // unqualified -> dbo
    [InlineData("DECLARE @t dbo.OrderRows;", UserTypeKind.Table)]
    [InlineData("DECLARE @p dbo.Point;", UserTypeKind.Assembly)]
    [InlineData("DECLARE @r sales.Region;", UserTypeKind.Alias)]           // non-dbo schema
    public void Resolves_UserTypes_ByEveryNameForm(string declareSql, UserTypeKind expected)
    {
        Assert.True(Catalog.TryResolve(DeclaredType(declareSql), out var entry));
        Assert.Equal(expected, entry.Kind);
    }

    [Theory]
    [InlineData("DECLARE @x int;")]
    [InlineData("DECLARE @x nvarchar(50);")]
    [InlineData("DECLARE @x decimal(18, 2);")]
    [InlineData("DECLARE @x [int];")]                                       // bracket-quoted system type
    [InlineData("DECLARE @x sysname;")]                                     // a SYSTEM alias type
    public void SystemTypes_NeverResolve_AndPassThroughUntouched(string declareSql)
    {
        Assert.False(Catalog.TryResolve(DeclaredType(declareSql), out _));
    }

    [Fact]
    public void NamedTypeAbsentFromTheDatabase_DoesNotResolve()
    {
        // The server raises its own error for this, exactly as it did before A59 — the
        // debugger must not invent a resolution for a type that does not exist.
        Assert.False(Catalog.TryResolve(DeclaredType("DECLARE @x dbo.NoSuchType;"), out _));
        Assert.False(Catalog.TryResolve(DeclaredType("DECLARE @x other.CustomerName;"), out _));
    }

    // ---- bare-name resolution: the caller's DEFAULT SCHEMA, then dbo. Nothing else. --------
    // Native resolution probes exactly those two (a type in any third schema is msg 2715 even
    // when its name is unique in the database). The first A59 cut resolved a bare name to any
    // schema that held it uniquely, "which covers every real shape" — it does not: it made the
    // debugger run code the engine refuses, and, when the same name lived in two schemas, bind
    // the WRONG one and silently realize the wrong table structure.

    [Fact]
    public void Bare_PrefersTheDefaultSchema_OverDbo()
    {
        // The dangerous case: `sales` is the session's default schema and both schemas define
        // `Code`. Native binds sales.Code (a TABLE type); resolving to dbo.Code would realize a
        // scalar where the debuggee has a table — wrong structure, silently.
        var catalog = new UserTypeCatalog(new[]
        {
            new UserTypeEntry("dbo", "Code", UserTypeKind.Alias),
            new UserTypeEntry("sales", "Code", UserTypeKind.Table, IsDefaultSchema: true),
        });

        Assert.True(catalog.TryResolve(DeclaredType("DECLARE @c Code;"), out var entry));
        Assert.Equal("sales", entry.Schema);
        Assert.Equal(UserTypeKind.Table, entry.Kind);
    }

    [Fact]
    public void Bare_FallsBackToDbo_WhenTheDefaultSchemaDoesNotDefineTheName()
    {
        var catalog = new UserTypeCatalog(new[]
        {
            new UserTypeEntry("sales", "Region", UserTypeKind.Alias, IsDefaultSchema: true),
            new UserTypeEntry("dbo", "Code", UserTypeKind.Alias),
        });

        Assert.True(catalog.TryResolve(DeclaredType("DECLARE @c Code;"), out var entry));
        Assert.Equal("dbo", entry.Schema);
    }

    [Fact]
    public void Bare_DoesNotResolve_WhenTheNameLivesOnlyInSomeThirdSchema()
    {
        // Unique in the database, and STILL msg 2715 natively (probed). Not resolving is what
        // makes the debugger refuse it exactly as the engine does.
        var catalog = new UserTypeCatalog(new[]
        {
            new UserTypeEntry("sales", "Region", UserTypeKind.Alias),   // default schema is dbo
        });

        Assert.False(catalog.TryResolve(DeclaredType("DECLARE @r Region;"), out _));
        Assert.True(catalog.TryResolve(DeclaredType("DECLARE @r sales.Region;"), out _));
    }

    [Fact]
    public void ThreePartName_DoesNotResolve()
    {
        // `db.schema.Type` is msg 117 natively — "contains more than the maximum number of
        // prefixes. The maximum is 1" (probed). Reading it positionally as schema.name (which
        // the first cut did) would resolve a shape the engine refuses.
        //
        // Hand-built, because ScriptDom will not parse a 3-part type name either ("Incorrect
        // syntax near '.'") — so this guard is unreachable through the parser today and is
        // kept as a belt: the rule lives in the resolver, not in an accident of the grammar.
        var threePart = new UserDataTypeReference { Name = new SchemaObjectName() };
        foreach (var part in new[] { "scratch", "dbo", "CustomerName" })
        {
            threePart.Name.Identifiers.Add(new Identifier { Value = part });
        }

        Assert.False(Catalog.TryResolve(threePart, out _));
    }

    [Fact]
    public void QualifiedName_EscapesABracketInEitherPart()
    {
        // A catalog string, not a source slice: `[dbo].[we]ird]` is msg 102 (probed). Every
        // generated site — the describe formals, TYPE_ID(), the §9 preamble DECLARE — takes
        // this string.
        var entry = new UserTypeEntry("od]d", "we]ird", UserTypeKind.Table);
        Assert.Equal("[od]]d].[we]]ird]", entry.QualifiedName);
    }

    [Fact]
    public void FromResultSet_ReadsTheCatalogQuery()
    {
        var resultSet = new ResultSet(
            new[] { "type_schema", "type_name", "is_table_type", "is_assembly_type", "is_default_schema" },
            new IReadOnlyList<object?>[]
            {
                new object?[] { "dbo", "CustomerName", false, false, true },
                new object?[] { "dbo", "OrderRows", true, false, true },
                new object?[] { "dbo", "Point", false, true, true },
                new object?[] { "sales", "Region", false, false, false },
            });

        var catalog = UserTypeCatalog.FromResultSet(resultSet);

        Assert.Equal(4, catalog.Count);
        Assert.True(catalog.TryResolve(DeclaredType("DECLARE @t dbo.OrderRows;"), out var table));
        Assert.Equal(UserTypeKind.Table, table.Kind);
        Assert.True(catalog.TryResolve(DeclaredType("DECLARE @p dbo.Point;"), out var clr));
        Assert.Equal(UserTypeKind.Assembly, clr.Kind);

        // SCHEMA_NAME() rides the same result set as a per-row flag — no second set, so no
        // scripted init sequence shifts (§20.2).
        Assert.True(catalog.TryResolve(DeclaredType("DECLARE @n CustomerName;"), out var bare));
        Assert.Equal("dbo", bare.Schema);
        Assert.False(catalog.TryResolve(DeclaredType("DECLARE @r Region;"), out _));   // not in dbo, not default
    }

    [Fact]
    public void FromResultSet_TreatsAnAbsentSetAsEmpty()
    {
        // The load rides the init round trip (§4 step 2a), and every pre-A59 unit test's
        // FakeStatementExecutor scripts only the NOCOUNT set — an empty catalog is the
        // correct answer there, not a crash.
        Assert.Equal(0, UserTypeCatalog.FromResultSet(null).Count);
        Assert.Equal(0, UserTypeCatalog.Empty.Count);
    }
}
