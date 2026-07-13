using TsqlDbg.Core.Targets;

namespace TsqlDbg.Core.Tests.Targets;

// DESIGN §16: "unknown server -> refuse to launch." M0 accept criterion: "refusal on
// non-allowlisted server."
public sealed class TargetsPolicyTests
{
    private const string SampleJson = """
        {
          "targets": {
            "DEVSQL01": { "env": "dev", "allowWrites": true, "options": "TrustServerCertificate=True" },
            "PRODSQL01": { "env": "prod", "allowWrites": false }
          }
        }
        """;

    [Fact]
    public void Resolve_KnownServer_ReturnsEntry()
    {
        var file = TargetsFile.Parse(SampleJson);

        var entry = TargetsPolicy.Resolve(file, "DEVSQL01");

        Assert.Equal("dev", entry.Env);
        Assert.True(entry.AllowWrites);
        Assert.Equal("TrustServerCertificate=True", entry.Options);
    }

    [Fact]
    public void Resolve_KnownServer_IsCaseInsensitive()
    {
        var file = TargetsFile.Parse(SampleJson);

        var entry = TargetsPolicy.Resolve(file, "devsql01");

        Assert.Equal("dev", entry.Env);
    }

    // DESIGN §16 (amendment A3, docs/archive/reviews/m0-gate-review-fable.md §2): targets.json
    // is a shared file with the mssql-proc-debug skill, which has its own additional
    // keys (`allowXe`, top-level `_readme`). The debugger's parser must ignore unknown
    // keys at every level rather than choking on a skill-authored file.
    [Fact]
    public void Parse_IgnoresUnknownKeys_FromSharedSkillSchema()
    {
        const string skillShapedJson = """
            {
              "_readme": [ "line one", "line two" ],
              "targets": {
                "DEVSQL01": { "env": "dev", "allowWrites": true, "allowXe": true, "options": "TrustServerCertificate=True" }
              }
            }
            """;

        var file = TargetsFile.Parse(skillShapedJson);
        var entry = TargetsPolicy.Resolve(file, "DEVSQL01");

        Assert.Equal("dev", entry.Env);
        Assert.True(entry.AllowWrites);
        Assert.Equal("TrustServerCertificate=True", entry.Options);
    }

    [Fact]
    public void Resolve_UnknownServer_Throws()
    {
        var file = TargetsFile.Parse(SampleJson);

        var ex = Assert.Throws<TargetsPolicyException>(() => TargetsPolicy.Resolve(file, "ROGUESQL"));
        Assert.Contains("ROGUESQL", ex.Message);
    }

    [Fact]
    public void Resolve_ProdWithoutAllowWrites_StillResolves_PolicyIsEnforcedElsewhere()
    {
        var file = TargetsFile.Parse(SampleJson);

        var entry = TargetsPolicy.Resolve(file, "PRODSQL01");

        Assert.False(entry.AllowWrites);
    }

    [Fact]
    public void Load_MissingFile_Throws()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");

        Assert.Throws<TargetsPolicyException>(() => TargetsFile.Load(missingPath));
    }

    [Fact]
    public void Parse_InvalidJson_Throws()
    {
        Assert.Throws<TargetsPolicyException>(() => TargetsFile.Parse("{ not json"));
    }

    [Fact]
    public void ResolvePath_ExplicitWins()
    {
        var resolved = TargetsPolicy.ResolvePath("explicit.json", "/ws", _ => "/env/targets.json");
        Assert.Equal("explicit.json", resolved);
    }

    [Fact]
    public void ResolvePath_EnvVarUsed_WhenNoExplicitPath()
    {
        var resolved = TargetsPolicy.ResolvePath(null, "/ws", name => name == "MSSQL_DEBUG_TARGETS" ? "/env/targets.json" : null);
        Assert.Equal("/env/targets.json", resolved);
    }

    [Fact]
    public void ResolvePath_WorkspaceFallback_WhenNoExplicitOrEnv()
    {
        var resolved = TargetsPolicy.ResolvePath(null, "/ws", _ => null);
        Assert.Equal(Path.Combine("/ws", "targets.json"), resolved);
    }

    [Fact]
    public void ResolvePath_NoneAvailable_Throws()
    {
        Assert.Throws<TargetsPolicyException>(() => TargetsPolicy.ResolvePath(null, null, _ => null));
    }
}
