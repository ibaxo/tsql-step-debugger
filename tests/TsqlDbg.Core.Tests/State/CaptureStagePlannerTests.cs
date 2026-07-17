using TsqlDbg.Core.Execution;
using TsqlDbg.Core.State;
using Xunit;

namespace TsqlDbg.Core.Tests.State;

// DESIGN §11.7 (C11/A65): the pure staging-plan logic — the describe query, the describe-row parse,
// and the DDL/flush assembly + refusal gates. The describe round-trip itself is exercised live by
// the p39 fidelity theory (never mocked).
public class CaptureStagePlannerTests
{
    // describe columns: column_ordinal[0], name[1], system_type_name[2], collation_name[3],
    // is_identity_column[4], is_computed_column[5], is_hidden[6], assembly_qualified_type_name[7]
    private static IReadOnlyList<object?> Row(
        int ordinal, string name, string type, string? collation = null,
        bool identity = false, bool computed = false, bool hidden = false, string? assembly = null)
        => new object?[] { ordinal, name, type, collation, identity, computed, hidden, assembly };

    private static ResultSet Describe(params IReadOnlyList<object?>[] rows) =>
        new(new[] { "column_ordinal", "name", "system_type_name", "collation_name",
            "is_identity_column", "is_computed_column", "is_hidden", "assembly_qualified_type_name" }, rows);

    [Fact]
    public void BuildDescribeQuery_EmbedsProjection_AndEscapesQuotes()
    {
        var query = CaptureStagePlanner.BuildDescribeQuery("SELECT [a] FROM [#t__f0]");
        Assert.Contains("sys.dm_exec_describe_first_result_set(N'SELECT [a] FROM [#t__f0]', NULL, 0)", query);
        Assert.Contains("is_identity_column", query);
        // A projection with a literal apostrophe is doubled for the N'…' embedding.
        Assert.Contains("N'SELECT ''x'''", CaptureStagePlanner.BuildDescribeQuery("SELECT 'x'"));
    }

    [Fact]
    public void ParseDescribe_ReadsFlagsAndCollation_SkipsHidden()
    {
        var described = CaptureStagePlanner.ParseDescribe(Describe(
            Row(1, "id", "int", identity: true),
            Row(2, "v", "nvarchar(50)", collation: "Latin1_General_CS_AS"),
            Row(3, "ts", "timestamp"),
            Row(4, "calc", "int", computed: true),
            Row(5, "u", "geometry", assembly: "Microsoft.SqlServer.Types..."),
            Row(6, "hk", "int", hidden: true)));

        Assert.Equal(5, described.Count);                    // hidden row dropped
        Assert.True(described[0].IsIdentity);
        Assert.Equal("Latin1_General_CS_AS", described[1].Collation);
        Assert.True(described[2].IsRowVersion);              // system_type_name = timestamp
        Assert.True(described[3].IsComputed);
        Assert.True(described[4].IsAssemblyType);
    }

    [Fact]
    public void BuildPlan_ExplicitList_TypesStageNullableWithSeqAndCollation()
    {
        var described = new[]
        {
            new CaptureStageColumn("a", "int", null, false, false, false, false),
            new CaptureStageColumn("b", "nvarchar(50)", "Latin1_General_CS_AS", false, false, false, false),
        };
        var resolution = CaptureStagePlanner.BuildPlan(
            "#__dbgcapN_2", "__dbgN_seq", "[#t__f0]", " ([a], [b])",
            hasExplicitColumnList: true, described);

        var plan = Assert.IsType<CaptureStagePlan>(resolution.Plan);
        Assert.Null(resolution.RefusalReason);
        // Stage: seq IDENTITY + each column typed-as-target, nullable, COLLATE preserved.
        Assert.Contains("[__dbgN_seq] bigint IDENTITY(1,1) NOT NULL", plan.StageCreateDdl);
        Assert.Contains("[a] int NULL", plan.StageCreateDdl);
        Assert.Contains("[b] nvarchar(50) COLLATE Latin1_General_CS_AS NULL", plan.StageCreateDdl);
        // Wrap target = stage + data columns (no seq); flush = target's own list, ordered by seq.
        Assert.Equal("[#__dbgcapN_2] ([a], [b])", plan.StageInsertTarget);
        Assert.Equal("INSERT INTO [#t__f0] ([a], [b]) SELECT [a], [b] FROM [#__dbgcapN_2] ORDER BY [__dbgN_seq]", plan.FlushCoreSql);
    }

    [Fact]
    public void BuildPlan_NoList_FiltersGeneratedColumns_AndDerivesFlushList()
    {
        var described = new[]
        {
            new CaptureStageColumn("id", "int", null, IsIdentity: true, false, false, false),
            new CaptureStageColumn("a", "int", null, false, false, false, false),
            new CaptureStageColumn("calc", "int", null, false, IsComputed: true, false, false),
            new CaptureStageColumn("ts", "timestamp", null, false, false, IsRowVersion: true, false),
            new CaptureStageColumn("b", "nvarchar(10)", "X", false, false, false, false),
        };
        var resolution = CaptureStagePlanner.BuildPlan(
            "#s", "__seq", "[#t__f0]", targetColumnListSql: "",
            hasExplicitColumnList: false, described);

        var plan = Assert.IsType<CaptureStagePlan>(resolution.Plan);
        // Only the insertable columns (a, b) — identity/computed/rowversion are auto-generated (native).
        Assert.Equal("[#s] ([a], [b])", plan.StageInsertTarget);
        Assert.Equal("INSERT INTO [#t__f0] ([a], [b]) SELECT [a], [b] FROM [#s] ORDER BY [__seq]", plan.FlushCoreSql);
        Assert.DoesNotContain("[id]", plan.StageCreateDdl);
        Assert.DoesNotContain("[calc]", plan.StageCreateDdl);
        Assert.DoesNotContain("[ts]", plan.StageCreateDdl);
    }

    [Fact]
    public void BuildPlan_Refuses_AssemblyColumn()
    {
        var described = new[] { new CaptureStageColumn("g", "geometry", null, false, false, false, IsAssemblyType: true) };
        var resolution = CaptureStagePlanner.BuildPlan("#s", "__seq", "[t]", " ([g])", true, described);
        Assert.Null(resolution.Plan);
        Assert.Contains("CLR/assembly", resolution.RefusalReason);
    }

    [Fact]
    public void BuildPlan_Refuses_ExplicitListNamingGeneratedColumn()
    {
        var described = new[]
        {
            new CaptureStageColumn("a", "int", null, false, false, false, false),
            new CaptureStageColumn("id", "int", null, IsIdentity: true, false, false, false),
        };
        var resolution = CaptureStagePlanner.BuildPlan("#s", "__seq", "[t]", " ([a], [id])", true, described);
        Assert.Null(resolution.Plan);
        Assert.Contains("auto-generated column 'id'", resolution.RefusalReason);
    }

    [Fact]
    public void BuildPlan_Refuses_DuplicateProjectionColumns()
    {
        // F5: the DMF describes `SELECT a, a` without error; a stage with two [a] columns is msg 2705.
        var described = new[]
        {
            new CaptureStageColumn("a", "int", null, false, false, false, false),
            new CaptureStageColumn("A", "int", null, false, false, false, false),   // case-insensitive dup
        };
        var resolution = CaptureStagePlanner.BuildPlan("#s", "__seq", "[t]", " ([a], [A])", true, described);
        Assert.Null(resolution.Plan);
        Assert.Contains("more than once", resolution.RefusalReason);
    }

    [Fact]
    public void BuildPlan_FlagsTargetHasIdentity_EvenWhenFilteredFromNoListProjection()
    {
        // F6: identity is filtered from a no-list projection but the flag must still be set (a zero-row
        // flush into an identity target must not move the caller's SCOPE_IDENTITY).
        var described = new[]
        {
            new CaptureStageColumn("id", "int", null, IsIdentity: true, false, false, false),
            new CaptureStageColumn("a", "int", null, false, false, false, false),
        };
        var plan = CaptureStagePlanner.BuildPlan("#s", "__seq", "[t]", "", false, described).Plan;
        Assert.NotNull(plan);
        Assert.True(plan!.TargetHasIdentity);

        var noId = CaptureStagePlanner.BuildPlan("#s", "__seq", "[t]", " ([a])", true,
            new[] { new CaptureStageColumn("a", "int", null, false, false, false, false) }).Plan;
        Assert.False(noId!.TargetHasIdentity);
    }

    [Fact]
    public void BuildPlan_Refuses_EmptyDescribe_AndNoInsertableColumns()
    {
        Assert.Contains("could not describe", CaptureStagePlanner.BuildPlan(
            "#s", "__seq", "[t]", "", false, Array.Empty<CaptureStageColumn>()).RefusalReason);

        var onlyGenerated = new[] { new CaptureStageColumn("id", "int", null, IsIdentity: true, false, false, false) };
        Assert.Contains("no insertable columns", CaptureStagePlanner.BuildPlan(
            "#s", "__seq", "[t]", "", false, onlyGenerated).RefusalReason);
    }
}
