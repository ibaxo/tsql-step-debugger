using TsqlDbg.Core.Batches;
using TsqlDbg.Core.Interpreter;
using TsqlDbg.Core.Rewrite;
using TsqlDbg.Core.Tests.Interpreter;
using Xunit;

namespace TsqlDbg.Core.Tests.Batches;

// M6 R3 (docs/archive/reviews/m6-boost-design-notes-fable.md §5-R3): the redoom prefix was
// deliberately DUPLICATED between Build and BuildForRepl at M5 (lane discipline); the
// M6 boost pass extracts it into one shared AppendRedoomPrefix. This pin captures the
// EXACT bytes both call sites emit — written and verified green BEFORE the extraction,
// unchanged after it — so the refactor is provably behavior-identical at both sites.
public class RedoomPrefixPinTests
{
    // The shared block, byte-exact (nonce 7f3a, trancount 2). The trailing XACT_ABORT
    // restore line differs by call site and is pinned separately below.
    private const string SharedRedoomBlock =
        "SET XACT_ABORT ON;\n" +
        "BEGIN TRANSACTION;\n" +
        "BEGIN TRANSACTION;\n" +
        "DECLARE @__dbg7f3a_doom int;\n" +
        "BEGIN TRY SET @__dbg7f3a_doom = 1/0; END TRY BEGIN CATCH END CATCH;\n";

    private static Frame BuildFrame(out RewriteContext ctx, out string script)
    {
        ctx = new RewriteContext("7f3a");
        var (body, fullScript) = ParseTestHelper.ParseBatch("SELECT 1 AS x;");
        script = fullScript;
        var cursor = ExecutionCursor.Create(body, fullScript);
        return new Frame(0, ModuleIdentity.Script(), cursor, SetOptionEnvironment.Default);
    }

    [Fact]
    public void DoomedDebuggeeBatch_EmitsExactRedoomBlock_ThenFrameEnvRestore()
    {
        var frame = BuildFrame(out var ctx, out var script);
        var composition = BatchComposition.Default with
        {
            RedoomTrancount = 2,
            XactAbortOn = true,
            IncludeStateWrite = false,
        };

        var batch = ComposedBatchBuilder.BuildForUnit(
            frame, RewriteEngine.CreateDefault(), ctx, frame.Cursor.Index.All[0], script,
            ShadowValues.Initial(), composition);

        // The shared block followed immediately by the debuggee-site restore (frame
        // env ON — the site-specific tail).
        Assert.Contains(SharedRedoomBlock + "SET XACT_ABORT ON;\n", batch.Text);
    }

    [Fact]
    public void DoomedDebuggeeBatch_FrameEnvOff_RestoresOff()
    {
        var frame = BuildFrame(out var ctx, out var script);
        var composition = BatchComposition.Default with
        {
            RedoomTrancount = 2,
            XactAbortOn = false,
            IncludeStateWrite = false,
        };

        var batch = ComposedBatchBuilder.BuildForUnit(
            frame, RewriteEngine.CreateDefault(), ctx, frame.Cursor.Index.All[0], script,
            ShadowValues.Initial(), composition);

        Assert.Contains(SharedRedoomBlock + "SET XACT_ABORT OFF;\n", batch.Text);
    }

    [Fact]
    public void DoomedRepl_EmitsExactRedoomBlock_ThenSandwichOff()
    {
        var frame = BuildFrame(out var ctx, out var script);
        var statement = ParseTestHelper.ParseSingle("SELECT 2 AS y;");
        var composition = BatchComposition.Default with
        {
            RedoomTrancount = 2,
            XactAbortOn = true,
            DebuggerInitiated = true,
        };

        var batch = ComposedBatchBuilder.BuildForRepl(
            frame, RewriteEngine.CreateDefault(), ctx, statement, "SELECT 2 AS y;",
            ShadowValues.Initial(), composition, includeTrailingProbe: false);

        // REPL site: the block's tail is always OFF (the §12.3 DebuggerInitiated
        // sandwich stays off until the batch-final restore). (A45: BuildForRepl now
        // returns a ComposedBatch; the redoom bytes are unchanged — the frame here has
        // no variables, so no seed is emitted between the redoom and BEGIN TRY.)
        Assert.Contains(SharedRedoomBlock + "SET XACT_ABORT OFF;\n", batch.Text);
    }

    [Fact]
    public void BothSites_RejectSubOneTrancount_WithTheSameMessage()
    {
        var frame = BuildFrame(out var ctx, out var script);
        var composition = BatchComposition.Default with { RedoomTrancount = 0, IncludeStateWrite = false };

        var fromBuild = Assert.Throws<ArgumentException>(() => ComposedBatchBuilder.BuildForUnit(
            frame, RewriteEngine.CreateDefault(), ctx, frame.Cursor.Index.All[0], script,
            ShadowValues.Initial(), composition));

        var statement = ParseTestHelper.ParseSingle("SELECT 2 AS y;");
        var fromRepl = Assert.Throws<ArgumentException>(() => ComposedBatchBuilder.BuildForRepl(
            frame, RewriteEngine.CreateDefault(), ctx, statement, "SELECT 2 AS y;",
            ShadowValues.Initial(), composition with { DebuggerInitiated = true }, includeTrailingProbe: false));

        Assert.Equal(fromBuild.Message, fromRepl.Message);
    }
}
