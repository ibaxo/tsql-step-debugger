// M6 item 1 (DESIGN §14/§15): boost threads through LaunchConfig.Parse -> SessionOptions,
// default false until fidelity pass 3 is green.
using Newtonsoft.Json.Linq;
using TsqlDbg.Adapter;
using TsqlDbg.Core.Sessions;
using Xunit;

namespace TsqlDbg.Adapter.Tests;

public sealed class LaunchConfigTests
{
    private static Dictionary<string, JToken> BaseProperties() => new()
    {
        ["server"] = "DEVSQL01",
        ["database"] = "SalesDb",
    };

    [Fact]
    public void Parse_NoBoostProperty_DefaultsFalse()
    {
        var config = LaunchConfig.Parse(BaseProperties());

        Assert.False(config.Boost);
    }

    [Fact]
    public void Parse_BoostTrue_Threads()
    {
        var properties = BaseProperties();
        properties["boost"] = true;

        var config = LaunchConfig.Parse(properties);

        Assert.True(config.Boost);
    }

    [Fact]
    public void Parse_NoScriptText_DefaultsNull()
    {
        // A60: absent scriptText => null (adapter falls back to reading the file path).
        var config = LaunchConfig.Parse(BaseProperties());

        Assert.Null(config.ScriptText);
    }

    [Fact]
    public void Parse_ScriptText_ThreadsVerbatim()
    {
        // A60: an unsaved buffer's inline body threads through untouched.
        var properties = BaseProperties();
        properties["script"] = "untitled:Untitled-1";
        properties["scriptText"] = "SELECT 1;\nGO\nSELECT 2;";

        var config = LaunchConfig.Parse(properties);

        Assert.Equal("SELECT 1;\nGO\nSELECT 2;", config.ScriptText);
        Assert.Equal("untitled:Untitled-1", config.ScriptPath);
    }

    [Fact]
    public void Parse_SI15Knobs_ThreadThroughWithDefaults()
    {
        var config = LaunchConfig.Parse(BaseProperties());

        Assert.Equal(50, config.TempTablePageSize);
        Assert.Equal(256, config.DisplayValueChars);
        Assert.True(config.AllowConsoleWrites);   // product default TRUE (writable console out of the box)
        Assert.Equal(30, config.ConsoleTimeoutSeconds);
        Assert.Equal(200, config.MaxConsoleRows);
        Assert.Equal(2000, config.WatchBudgetMs);
    }
}
