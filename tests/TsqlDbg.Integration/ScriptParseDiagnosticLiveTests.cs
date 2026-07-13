using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using TsqlDbg.Core.Execution;
using TsqlDbg.Core.Sessions;
using TsqlDbg.Core.Targets;
using TsqlDbg.Core.Tracing;
using Xunit;

namespace TsqlDbg.Integration;

// DESIGN §5.4 / §20.3 (A47): when a script fails ScriptDom's parse, the debugger asks the
// LIVE server for its own native diagnostic (SET PARSEONLY ON, per GO batch) and leads with
// it — e.g. Msg 111, "'CREATE/ALTER PROCEDURE' must be the first statement in a query batch"
// — instead of the pre-A47 over-broad relabel that called EVERY syntax error (all report the
// generic 46010, "Incorrect syntax near X") a "GO N repeat count" problem. ScriptDom can
// never produce these native messages, so this behaviour is only observable LIVE — the
// fidelity harness (RunToEndAsync) never sees a launch refusal, the same coverage-gap lesson
// as A44/A45. Core-level tests drive Session.InitializeAsync directly; the adapter-level test
// proves the native message survives BuildLaunchErrorMessage out over the real DAP wire.
public sealed class ScriptParseDiagnosticLiveTests
{
    private const string ConnEnvVar = "TSQLDBG_TEST_CONN";

    private sealed class NullSink : ITraceSink
    {
        public void Event(string category, string message) { }
    }

    // Drives Session.InitializeAsync against a REAL connection for a script that must fail to
    // parse, and returns the launch-refusal message. On this path BEGIN TRANSACTION never runs
    // and no state table is created (the failure is before §4 step 5), so closing the
    // connection is the whole teardown — nothing to roll back.
    private static async Task<string> LaunchScriptExpectingFailureAsync(string script)
    {
        var raw = Environment.GetEnvironmentVariable(ConnEnvVar);
        Skip.If(string.IsNullOrWhiteSpace(raw),
            $"{ConnEnvVar} is not set; skipping live probe (never fake a pass — CLAUDE.md).");

        var csb = new SqlConnectionStringBuilder(raw);
        var options = new SessionOptions(csb.DataSource, csb.InitialCatalog, LaunchMode.Script, null, null, script);
        var target = new TargetEntry(csb.DataSource, "test", AllowWrites: false, Options: null);
        var nonce = SqlConnectionStringFactory.NewNonce();

        await using var connection = new SqlConnection(SqlConnectionStringFactory.Build(options, target, nonce));
        await connection.OpenAsync();
        var executor = new SqlStatementExecutor(connection, options.CommandTimeoutSeconds);
        var session = new Session(options, executor, new NullSink(), nonce);
        try
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => session.InitializeAsync());
            return ex.Message;
        }
        finally
        {
            await executor.DisposeAsync();
        }
    }

    [SkippableFact]
    public async Task Reported_CreateProcNotFirst_LaunchFails_WithNativeMsg111()
    {
        // Ivan's exact report: a CREATE OR ALTER PROCEDURE that is not the first statement of
        // its batch. Native truth (msg 111), not the pre-A47 GO-N mislabel.
        var message = await LaunchScriptExpectingFailureAsync(
            "SET ANSI_NULLS ON\n" +
            "CREATE OR ALTER PROCEDURE dbo.p9_probe_isolation AS\n" +
            "BEGIN\n" +
            "    SET TRANSACTION ISOLATION LEVEL SERIALIZABLE;\n" +
            "    SELECT 1 AS x;\n" +
            "END\n");

        Assert.Contains("must be the first statement in a query batch", message);
        Assert.Contains("Msg 111", message);
        // The defect this replaces: EVERY syntax error was labelled a GO repeat count.
        Assert.DoesNotContain("repeat count", message);
        Assert.DoesNotContain("GO N", message);
    }

    [SkippableFact]
    public async Task GenericSyntaxError_LaunchFails_WithNativeServerMessage()
    {
        // A syntax error unrelated to GO or CREATE PROC: the server's own wording (156,
        // "Incorrect syntax near the keyword 'FROM'"), sharper than ScriptDom's terse 46010.
        var message = await LaunchScriptExpectingFailureAsync("SELECT * FROM FROM t\n");

        Assert.Contains("Incorrect syntax", message);
        Assert.Contains("Msg 156", message);
        Assert.DoesNotContain("repeat count", message);
    }

    [SkippableFact]
    public async Task MultiBatch_ErrorInSecondBatch_MapsServerLineToWholeScript()
    {
        // Batch 1 parses; batch 2 (after GO, script line 3) has the syntax error. The server
        // reports it at ITS batch-local line 1; the oracle must map that back to script line 3
        // via the GO-offset StartLine — proving multi-batch line mapping, not just single-batch.
        var message = await LaunchScriptExpectingFailureAsync(
            "SELECT 1;\n" +   // line 1  (batch 1)
            "GO\n" +          // line 2
            "SELECT * FROM FROM t\n");   // line 3  (batch 2, server-local line 1)

        Assert.Contains("Msg 156", message);
        Assert.Contains("Line 3", message);   // mapped: batch-2 start line 3 + server line 1 - 1
    }

    [SkippableFact]
    public async Task MalformedGoCount_FallsBackToHonestScriptDomMessage()
    {
        // A malformed `GO 1.5` is a client-side GO-directive problem the server can't see (the
        // tokenizer splits it away), so the oracle finds nothing and we fall back to ScriptDom's
        // own honest message — NOT the pre-A47 "GO N is not supported" (which was wrong anyway:
        // a whole-number GO N has been supported since A43).
        var message = await LaunchScriptExpectingFailureAsync("SELECT 1\nGO 1.5\nSELECT 2\n");

        Assert.Contains("ScriptDom parse error", message);
        Assert.Contains("1.5", message);
        Assert.DoesNotContain("not supported", message);
        Assert.DoesNotContain("must be the first statement", message);
    }

    // A44 lesson: the adapter layer is only exercisable over the real DAP wire. A failed launch
    // returns success:false with the message in `message`; DapStdioHarness.SendRequestAsync
    // surfaces that as an InvalidOperationException carrying it — so this pins that the native
    // Msg 111 survives BuildLaunchErrorMessage all the way to the DAP client (VS Code shows it
    // verbatim on the failed launch), which no Core-level test can.
    [SkippableFact]
    public async Task AdapterLaunch_CreateProcNotFirst_FailsWithNativeMessageOverTheWire()
    {
        var raw = Environment.GetEnvironmentVariable(ConnEnvVar);
        Skip.If(string.IsNullOrWhiteSpace(raw),
            $"{ConnEnvVar} is not set; skipping live probe (never fake a pass — CLAUDE.md).");

        var csb = new SqlConnectionStringBuilder(raw);
        var tracePath = Path.Combine(Path.GetTempPath(), "a47-createproc-notfirst.jsonl");
        if (File.Exists(tracePath))
        {
            File.Delete(tracePath);
        }

        var scriptPath = Path.Combine(Path.GetTempPath(), "a47-createproc-notfirst.sql");
        File.WriteAllText(scriptPath,
            "SET ANSI_NULLS ON\n" +
            "CREATE OR ALTER PROCEDURE dbo.p9_probe_isolation AS\n" +
            "BEGIN\n" +
            "    SELECT 1 AS x;\n" +
            "END\n");

        await using var dap = DapStdioHarness.Launch(tracePath);
        await dap.SendRequestAsync("initialize", new JsonObject { ["clientID"] = "a47-parse-diagnostic-live", ["adapterID"] = "tsqldbg" });

        var launchFailure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            dap.SendRequestAsync("launch", new JsonObject
            {
                ["server"] = csb.DataSource,
                ["database"] = csb.InitialCatalog,
                ["mode"] = "script",
                ["script"] = scriptPath,
                ["targetsFile"] = Path.Combine(AppContext.BaseDirectory, "targets.json"),
                ["stopOnEntry"] = true,
            }));

        Assert.Contains("must be the first statement in a query batch", launchFailure.Message);
    }
}
