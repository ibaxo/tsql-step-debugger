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

    // ---- §17 traceRun (A73) -----------------------------------------------------

    [Fact]
    public void Parse_NoTraceRun_DefaultsNull()
    {
        Assert.Null(LaunchConfig.Parse(BaseProperties()).TraceRun);
    }

    [Fact]
    public void Parse_TraceRunFalse_IsNull()
    {
        var properties = BaseProperties();
        properties["traceRun"] = false;

        Assert.Null(LaunchConfig.Parse(properties).TraceRun);
    }

    [Fact]
    public void Parse_TraceRunTrue_AllDefaults()
    {
        var properties = BaseProperties();
        properties["traceRun"] = true;

        var traceRun = LaunchConfig.Parse(properties).TraceRun;

        Assert.NotNull(traceRun);
        Assert.Equal(StepKind.Over, traceRun!.StepMode);        // §24.4 trace_* default
        Assert.False(traceRun.CaptureTempRowCounts);
        Assert.False(traceRun.FullVariableCapture);             // "changed" (§24.8/A70)
        Assert.Null(traceRun.File);
    }

    [Fact]
    public void Parse_TraceRunObject_ThreadsAllKnobs()
    {
        var properties = BaseProperties();
        properties["traceRun"] = JObject.Parse(
            "{ \"stepMode\": \"Into\", \"captureTempRowCounts\": true, \"variableCapture\": \"Full\", \"file\": \"C:/tmp/t.jsonl\" }");

        var traceRun = LaunchConfig.Parse(properties).TraceRun;

        Assert.NotNull(traceRun);
        Assert.Equal(StepKind.Into, traceRun!.StepMode);        // case-insensitive, like the MCP arg
        Assert.True(traceRun.CaptureTempRowCounts);
        Assert.True(traceRun.FullVariableCapture);
        Assert.Equal("C:/tmp/t.jsonl", traceRun.File);
    }

    [Fact]
    public void Parse_TraceRunUnrecognizedStepMode_FallsBackToOver()
    {
        // Same unrecognized-defaults-safe discipline as waitfor/commitMode.
        var properties = BaseProperties();
        properties["traceRun"] = JObject.Parse("{ \"stepMode\": \"sideways\" }");

        Assert.Equal(StepKind.Over, LaunchConfig.Parse(properties).TraceRun!.StepMode);
    }

    [Fact]
    public void Parse_TraceRunInvalidVariableCapture_RefusesTheLaunch()
    {
        // §24.9 parity with the MCP tool arg: a silently-wrong capture shape is worse
        // than a failed launch.
        var properties = BaseProperties();
        properties["traceRun"] = JObject.Parse("{ \"variableCapture\": \"delta\" }");

        Assert.Throws<ProtocolLaunchException>(() => LaunchConfig.Parse(properties));
    }
}
