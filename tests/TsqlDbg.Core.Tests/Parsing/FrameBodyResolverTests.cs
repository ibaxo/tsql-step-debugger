using TsqlDbg.Core.Parsing;

namespace TsqlDbg.Core.Tests.Parsing;

// DESIGN §5.2: source-of-truth resolution for both launch modes.
public sealed class FrameBodyResolverTests
{
    [Fact]
    public void ResolveProcedureBody_ExtractsStatementsFromCreateProcedure()
    {
        const string sql = """
            CREATE PROCEDURE dbo.uspAdd
                @A int,
                @B int
            AS
            BEGIN
                SET NOCOUNT ON;
                SELECT @A + @B AS Sum;
            END
            """;

        var parsed = ScriptParser.Parse(sql, initialQuotedIdentifiers: true, compatLevel: 150, out var errors);
        Assert.Empty(errors);

        var statements = FrameBodyResolver.ResolveProcedureBody(parsed);

        // The body is a single BEGIN...END block at the top level.
        Assert.Single(statements);
    }

    [Fact]
    public void ResolveScriptBatches_SingleBatch_ReturnsOneBatchOfTopLevelStatements()
    {
        const string sql = "DECLARE @x int = 1;\nSELECT @x;";

        var parsed = ScriptParser.Parse(sql, initialQuotedIdentifiers: true, compatLevel: 150, out var errors);
        Assert.Empty(errors);

        var batches = FrameBodyResolver.ResolveScriptBatches(parsed);

        Assert.Single(batches);
        Assert.Equal(2, batches[0].Count);
    }

    // M8 (§5.4): multi-batch GO scripts are now supported — one statement list per batch,
    // with file-absolute lines (this replaces the M0-era single-batch refusal test).
    [Fact]
    public void ResolveScriptBatches_MultiBatch_ReturnsPerBatchStatements()
    {
        const string sql = "SELECT 1;\nGO\nDECLARE @y int = 2;\nSELECT @y;\nGO\nSELECT 3;";

        var parsed = ScriptParser.Parse(sql, initialQuotedIdentifiers: true, compatLevel: 150, out var errors);
        Assert.Empty(errors);

        var batches = FrameBodyResolver.ResolveScriptBatches(parsed);

        Assert.Equal(3, batches.Count);
        Assert.Single(batches[0]);          // SELECT 1;
        Assert.Equal(2, batches[1].Count);  // DECLARE @y; SELECT @y;
        Assert.Single(batches[2]);          // SELECT 3;

        // Lines are file-absolute (fact 32e): batch 2's DECLARE is on file line 3, not 1.
        Assert.Equal(3, batches[1][0].StartLine);
    }

    [Fact]
    public void ResolveScriptBatches_DropsEmptyBatches()
    {
        // Trailing GO and a GO\nGO leave zero-statement batches ScriptDom still lists —
        // the resolver drops them so the debugger never enters an empty scope.
        const string sql = "SELECT 1;\nGO\nGO\nSELECT 2;\nGO";

        var parsed = ScriptParser.Parse(sql, initialQuotedIdentifiers: true, compatLevel: 150, out var errors);
        Assert.Empty(errors);

        var batches = FrameBodyResolver.ResolveScriptBatches(parsed);

        Assert.Equal(2, batches.Count);
    }
}
