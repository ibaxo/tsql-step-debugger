using TsqlDbg.Core.Batches;
using TsqlDbg.Core.Execution;
using Xunit;

namespace TsqlDbg.Core.Tests.Batches;

// DESIGN §7.3: control row schema (fixed contract), parsed by column name.
public class ControlRowParserTests
{
    [Fact]
    public void OkPath_ParsesRcAndScopeIdentity_AndDisplayValues()
    {
        var controlSet = new ResultSet(
            new[] { "__dbg_ctl", "ok", "rc", "scope_identity", "trancount", "xact_state", "v_0", "v_0_isnull" },
            new IReadOnlyList<object?>[] { new object?[] { 1, true, 3, 7m, 1, 0, "hello", false } });

        var result = new BatchResult(new[] { controlSet }, Array.Empty<string>());

        var (control, userSets, _) = ControlRowParser.Parse(result, variableCount: 1);

        Assert.True(control.Ok);
        Assert.Equal(3, control.Rc);
        Assert.Equal(7m, control.ScopeIdentity);
        Assert.Equal(1, control.Trancount);
        Assert.Equal(0, control.XactState);
        Assert.Equal("hello", control.DisplayValues[0].Text);
        Assert.False(control.DisplayValues[0].IsNull);
        Assert.Empty(userSets);
    }

    [Fact]
    public void CatchPath_ParsesErrorFields()
    {
        var controlSet = new ResultSet(
            new[] { "__dbg_ctl", "ok", "err_number", "err_severity", "err_state", "err_line", "err_procedure", "err_message", "trancount", "xact_state" },
            new IReadOnlyList<object?>[] { new object?[] { 1, false, 547, 16, 1, 7, null, "FK violation", 1, 1 } });

        var result = new BatchResult(new[] { controlSet }, Array.Empty<string>());

        var (control, _, _) = ControlRowParser.Parse(result, variableCount: 0);

        Assert.False(control.Ok);
        Assert.Equal(547, control.ErrNumber);
        Assert.Equal(16, control.ErrSeverity);
        Assert.Equal("FK violation", control.ErrMessage);
        Assert.Null(control.ErrProcedure);
    }

    [Fact]
    public void UserResultSets_StreamBeforeControlRow_AreReturnedSeparately()
    {
        var userSet = new ResultSet(new[] { "x" }, new IReadOnlyList<object?>[] { new object?[] { 42 } });
        var controlSet = new ResultSet(
            new[] { "__dbg_ctl", "ok", "trancount", "xact_state" },
            new IReadOnlyList<object?>[] { new object?[] { 1, true, 1, 0 } });

        var result = new BatchResult(new[] { userSet, controlSet }, Array.Empty<string>());

        var (control, userSets, _) = ControlRowParser.Parse(result, variableCount: 0);

        Assert.True(control.Ok);
        Assert.Single(userSets);
        Assert.Same(userSet, userSets[0]);
    }

    [Fact]
    public void NoControlRow_Throws()
    {
        var result = new BatchResult(Array.Empty<ResultSet>(), Array.Empty<string>());
        Assert.Throws<InvalidOperationException>(() => ControlRowParser.Parse(result, variableCount: 0));
    }
}
