using TsqlDbg.Core.Sessions;

namespace TsqlDbg.Mcp.Tests;

// DESIGN §24.9: the per-session option subset an agent supplies on start_session / trace_*.
public sealed class SessionArgsTests
{
    private static SessionArgs MinimalScriptArgs() => new("SRV", "DB", Script: "SELECT 1;");

    [Fact]
    public void ToSessionOptions_AllowConsoleWrites_DefaultsFalse()
    {
        var options = MinimalScriptArgs().ToSessionOptions();
        Assert.False(options.AllowConsoleWrites);
    }

    [Fact]
    public void ToSessionOptions_CommitMode_DefaultsRollback()
    {
        var options = MinimalScriptArgs().ToSessionOptions();
        Assert.Equal(CommitMode.Rollback, options.CommitMode);
    }

    [Fact]
    public void ToSessionOptions_CompatLevel_DefaultsZero()
    {
        var options = MinimalScriptArgs().ToSessionOptions();
        Assert.Equal(0, options.CompatLevel);
    }

    [Fact]
    public void ToSessionOptions_Mode_DefaultsScript()
    {
        var options = MinimalScriptArgs().ToSessionOptions();
        Assert.Equal(LaunchMode.Script, options.Mode);
    }

    [Theory]
    [InlineData("commit", true)]
    [InlineData("Commit", true)]
    [InlineData("COMMIT", true)]
    [InlineData("rollback", false)]
    [InlineData("Rollback", false)]
    [InlineData("bogus", false)]
    public void CommitModeRequested_TrueOnlyForCommit(string commitMode, bool expected)
    {
        var args = MinimalScriptArgs() with { CommitMode = commitMode };
        Assert.Equal(expected, args.CommitModeRequested);
    }

    [Theory]
    [InlineData("sql", true)]
    [InlineData("Sql", true)]
    [InlineData("SQL", true)]
    [InlineData("integrated", false)]
    [InlineData("Integrated", false)]
    [InlineData("bogus", false)]
    public void IsSqlAuth_TrueOnlyForSql(string authType, bool expected)
    {
        var args = MinimalScriptArgs() with { AuthType = authType };
        Assert.Equal(expected, args.IsSqlAuth);
    }

    [Fact]
    public void ToSessionOptions_ScriptMode_NoScriptOrScriptPath_Throws()
    {
        var args = new SessionArgs("SRV", "DB", Mode: "script");

        var ex = Assert.Throws<ArgumentException>(() => args.ToSessionOptions());
        Assert.Contains("script", ex.Message);
    }

    [Fact]
    public void ToSessionOptions_ProcedureMode_BlankProcedure_Throws()
    {
        var args = new SessionArgs("SRV", "DB", Mode: "procedure", Procedure: null);

        var ex = Assert.Throws<ArgumentException>(() => args.ToSessionOptions());
        Assert.Contains("procedure", ex.Message);
    }

    [Fact]
    public void ToSessionOptions_ProcedureMode_WhitespaceProcedure_Throws()
    {
        var args = new SessionArgs("SRV", "DB", Mode: "procedure", Procedure: "   ");

        Assert.Throws<ArgumentException>(() => args.ToSessionOptions());
    }

    [Fact]
    public void ToSessionOptions_ScriptMode_InlineText_Succeeds()
    {
        var args = new SessionArgs("SRV", "DB", Mode: "script", Script: "SELECT 1;");

        var options = args.ToSessionOptions();

        Assert.Equal(LaunchMode.Script, options.Mode);
        Assert.Equal("SELECT 1;", options.ScriptText);
    }

    [Fact]
    public void ToSessionOptions_ScriptMode_ScriptPath_ReadsFileContents()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tsqldbg-mcp-tests-{Guid.NewGuid():N}.sql");
        File.WriteAllText(path, "SELECT 2;");
        try
        {
            var args = new SessionArgs("SRV", "DB", Mode: "script", ScriptPath: path);

            var options = args.ToSessionOptions();

            Assert.Equal("SELECT 2;", options.ScriptText);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ToSessionOptions_ProcedureMode_WithName_Succeeds()
    {
        var args = new SessionArgs("SRV", "DB", Mode: "procedure", Procedure: "dbo.uspProcessOrder");

        var options = args.ToSessionOptions();

        Assert.Equal(LaunchMode.Procedure, options.Mode);
        Assert.Equal("dbo.uspProcessOrder", options.Procedure);
    }

    [Fact]
    public void ToSessionOptions_PassesThroughServerAndDatabase()
    {
        var args = MinimalScriptArgs();

        var options = args.ToSessionOptions();

        Assert.Equal("SRV", options.Server);
        Assert.Equal("DB", options.Database);
    }
}
