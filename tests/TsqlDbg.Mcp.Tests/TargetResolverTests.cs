namespace TsqlDbg.Mcp.Tests;

// DESIGN §24.1(1)/§16: the programmatic surface's default-deny allowlist gate.
public sealed class TargetResolverTests
{
    private const string SampleJson = """
        {
          "targets": {
            "DEVSQL01": { "env": "dev", "allowWrites": true, "options": "TrustServerCertificate=True" },
            "PRODSQL01": { "env": "prod", "allowWrites": false }
          }
        }
        """;

    private static string WriteTempTargetsFile(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"tsqldbg-mcp-tests-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void Resolve_NoPathConfigured_Refuses()
    {
        var config = new McpServerConfig();

        var ex = Assert.Throws<TargetResolver.RefusedException>(
            () => TargetResolver.Resolve(config, "DEVSQL01", _ => null));

        Assert.Contains("Refusing", ex.Message);
        Assert.Contains("default-deny", ex.Message);
    }

    [Fact]
    public void Resolve_TargetsFileMissing_Refuses()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), $"tsqldbg-mcp-tests-missing-{Guid.NewGuid():N}.json");
        var config = new McpServerConfig { TargetsFile = missingPath };

        var ex = Assert.Throws<TargetResolver.RefusedException>(
            () => TargetResolver.Resolve(config, "DEVSQL01", _ => null));

        Assert.Contains("Refusing", ex.Message);
    }

    [Fact]
    public void Resolve_ServerNotListed_Refuses()
    {
        var path = WriteTempTargetsFile(SampleJson);
        try
        {
            var config = new McpServerConfig { TargetsFile = path };

            var ex = Assert.Throws<TargetResolver.RefusedException>(
                () => TargetResolver.Resolve(config, "ROGUESQL", _ => null));

            Assert.Contains("Refusing", ex.Message);
            Assert.Contains("ROGUESQL", ex.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Resolve_ListedServer_ReturnsEntry_WithAllowWritesTrue()
    {
        var path = WriteTempTargetsFile(SampleJson);
        try
        {
            var config = new McpServerConfig { TargetsFile = path };

            var entry = TargetResolver.Resolve(config, "DEVSQL01", _ => null);

            Assert.Equal("DEVSQL01", entry.Server);
            Assert.Equal("dev", entry.Env);
            Assert.True(entry.AllowWrites);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Resolve_ListedServer_ReturnsEntry_WithAllowWritesFalse()
    {
        var path = WriteTempTargetsFile(SampleJson);
        try
        {
            var config = new McpServerConfig { TargetsFile = path };

            var entry = TargetResolver.Resolve(config, "PRODSQL01", _ => null);

            Assert.Equal("prod", entry.Env);
            Assert.False(entry.AllowWrites);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Resolve_UsesEnvVar_WhenNoExplicitTargetsFile()
    {
        var path = WriteTempTargetsFile(SampleJson);
        try
        {
            var config = new McpServerConfig();

            var entry = TargetResolver.Resolve(
                config, "DEVSQL01", name => name == "MSSQL_DEBUG_TARGETS" ? path : null);

            Assert.Equal("dev", entry.Env);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    public void CommitAuthorized_RequiresBothCommitModeAndAllowWrites(
        bool commitModeRequested, bool allowWrites, bool expected)
    {
        var target = new Core.Targets.TargetEntry("SRV", "dev", allowWrites, null);

        var result = TargetResolver.CommitAuthorized(commitModeRequested, target);

        Assert.Equal(expected, result);
    }
}
