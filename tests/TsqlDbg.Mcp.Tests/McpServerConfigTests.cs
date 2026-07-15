namespace TsqlDbg.Mcp.Tests;

// DESIGN §24.9: server-level configuration for the MCP host process.
public sealed class McpServerConfigTests
{
    [Fact]
    public void FromArgsAndEnvironment_Defaults_WhenNoArgsOrEnv()
    {
        var config = McpServerConfig.FromArgsAndEnvironment(Array.Empty<string>(), _ => null);

        Assert.Null(config.TargetsFile);
        Assert.Equal(4, config.MaxSessions);
        Assert.Equal(300, config.SessionIdleTimeoutSeconds);
        Assert.Null(config.HostTracePath);
        Assert.Equal(Path.Combine(Path.GetTempPath(), "tsqldbg-mcp-traces"), config.TraceOutputDirectory);
    }

    [Fact]
    public void FromArgsAndEnvironment_ParsesTargetsFlag()
    {
        var config = McpServerConfig.FromArgsAndEnvironment(new[] { "--targets", "C:\\path\\targets.json" }, _ => null);

        Assert.Equal("C:\\path\\targets.json", config.TargetsFile);
    }

    [Fact]
    public void FromArgsAndEnvironment_ParsesMaxSessionsFlag()
    {
        var config = McpServerConfig.FromArgsAndEnvironment(new[] { "--max-sessions", "10" }, _ => null);

        Assert.Equal(10, config.MaxSessions);
    }

    [Fact]
    public void FromArgsAndEnvironment_ParsesIdleTimeoutFlag()
    {
        var config = McpServerConfig.FromArgsAndEnvironment(new[] { "--idle-timeout-sec", "60" }, _ => null);

        Assert.Equal(60, config.SessionIdleTimeoutSeconds);
    }

    [Fact]
    public void FromArgsAndEnvironment_ParsesTraceFlag()
    {
        var config = McpServerConfig.FromArgsAndEnvironment(new[] { "--trace", "C:\\trace.jsonl" }, _ => null);

        Assert.Equal("C:\\trace.jsonl", config.HostTracePath);
    }

    [Fact]
    public void FromArgsAndEnvironment_ParsesTraceDirFlag()
    {
        var config = McpServerConfig.FromArgsAndEnvironment(new[] { "--trace-dir", "C:\\traces" }, _ => null);

        Assert.Equal("C:\\traces", config.TraceOutputDirectory);
    }

    [Fact]
    public void FromArgsAndEnvironment_SeedsTargetsFile_FromEnvVar()
    {
        var config = McpServerConfig.FromArgsAndEnvironment(
            Array.Empty<string>(), name => name == "MSSQL_DEBUG_TARGETS" ? "/env/targets.json" : null);

        Assert.Equal("/env/targets.json", config.TargetsFile);
    }

    [Fact]
    public void FromArgsAndEnvironment_ArgOverridesEnvVar_ForTargetsFile()
    {
        var config = McpServerConfig.FromArgsAndEnvironment(
            new[] { "--targets", "explicit.json" },
            name => name == "MSSQL_DEBUG_TARGETS" ? "/env/targets.json" : null);

        Assert.Equal("explicit.json", config.TargetsFile);
    }

    [Fact]
    public void FromArgsAndEnvironment_IgnoresNonPositiveMaxSessions()
    {
        var config = McpServerConfig.FromArgsAndEnvironment(new[] { "--max-sessions", "0" }, _ => null);

        Assert.Equal(4, config.MaxSessions);
    }

    [Fact]
    public void FromArgsAndEnvironment_IgnoresUnparseableIdleTimeout()
    {
        var config = McpServerConfig.FromArgsAndEnvironment(new[] { "--idle-timeout-sec", "not-a-number" }, _ => null);

        Assert.Equal(300, config.SessionIdleTimeoutSeconds);
    }

    [Fact]
    public void ResolveTargetsPath_ExplicitWins_OverEnvVar()
    {
        var config = new McpServerConfig { TargetsFile = "explicit.json" };

        var resolved = config.ResolveTargetsPath(_ => "/env/targets.json");

        Assert.Equal("explicit.json", resolved);
    }

    [Fact]
    public void ResolveTargetsPath_UsesEnvVar_WhenNoExplicitTargetsFile()
    {
        var config = new McpServerConfig();

        var resolved = config.ResolveTargetsPath(name => name == "MSSQL_DEBUG_TARGETS" ? "/env/targets.json" : null);

        Assert.Equal("/env/targets.json", resolved);
    }

    [Fact]
    public void ResolveTargetsPath_ReturnsNull_WhenNeitherSet()
    {
        var config = new McpServerConfig();

        var resolved = config.ResolveTargetsPath(_ => null);

        Assert.Null(resolved);
    }

    [Fact]
    public void DefaultConfig_HasDefaultMaxSessionsAndIdleTimeout()
    {
        var config = new McpServerConfig();

        Assert.Equal(4, config.MaxSessions);
        Assert.Equal(300, config.SessionIdleTimeoutSeconds);
    }
}
