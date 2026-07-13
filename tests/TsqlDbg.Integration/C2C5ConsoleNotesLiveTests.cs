using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using TsqlDbg.Core.Sessions;
using TsqlDbg.Core.Targets;
using Xunit;

namespace TsqlDbg.Integration;

// M7 hardening (design note §4/§8-A29), updated for A56: live verification for C2
// (cached sys.triggers lookup, one note per DML target object that has triggers) and C5
// (one-time NOCOUNT-forced-ON note). Both are DEBUGGER DIAGNOSTIC ANNOTATIONS: as of A56
// they no longer flow through Session.StepAsync's debuggee `messages` list OR
// LaunchWarnings — they are routed to the logLevel-gated Session.DrainDiagnosticNotes()
// channel (C5 latched at session-init from the @@OPTIONS probe; C2 during the DML step).
// Exercises the REAL SQL (@@OPTIONS bit 512, the sys.triggers EXISTS probe) against a
// live instance rather than trusting the FakeStatementExecutor-driven unit tests alone
// (CLAUDE.md: never fake a pass). The second/third tests pin the ADAPTER gate end-to-end
// over the real DAP stdio wire: the note reaches the console under logLevel:"verbose" and
// is SUPPRESSED at the default "normal" (the fidelity harness cannot see either — an
// adapter presentation behavior, the A44/A50/A51/A54 lesson).
public sealed class C2C5ConsoleNotesLiveTests
{
    private const string ConnEnvVar = "TSQLDBG_TEST_CONN";

    private const string Script = """
        DECLARE @dummy int;
        INSERT dbo.m7_c2_probe_no_trigger (v) VALUES (1);
        INSERT dbo.m7_c2_probe_with_trigger (v) VALUES (1);
        INSERT dbo.m7_c2_probe_with_trigger (v) VALUES (2);
        SET @dummy = 1;
        """;

    [SkippableFact]
    public async Task DmlTriggerNote_FiresOnceForTheTriggeredTable_NeverForTheOther_AndNoCountNoteFiresAtInit()
    {
        var rawConnectionString = Environment.GetEnvironmentVariable(ConnEnvVar);
        Skip.If(
            string.IsNullOrWhiteSpace(rawConnectionString),
            $"{ConnEnvVar} is not set; skipping live probe (never fake a pass — CLAUDE.md).");

        var csb = new SqlConnectionStringBuilder(rawConnectionString);
        var server = csb.DataSource;
        var database = csb.InitialCatalog;

        await DeployFixtureAsync(rawConnectionString!);
        try
        {
            var target = new TargetEntry(server, "test", AllowWrites: false, Options: null);
            var options = new SessionOptions(server, database, LaunchMode.Script, null, null, Script);

            await using var liveSession = await LiveSession.OpenAsync(options, target);

            var diagnostics = new List<string>();
            var debuggeeMessages = new List<string>();

            // C5: this is a fresh ad-hoc connection — NOCOUNT is OFF by default, so the
            // session-init @@OPTIONS probe (before "SET ... NOCOUNT ON") latched the
            // one-time cosmetic note into the A56 diagnostic channel at open.
            diagnostics.AddRange(liveSession.Session.DrainDiagnosticNotes());
            Assert.Contains(diagnostics, m => m.Contains("NOCOUNT is forced ON"));
            // A56 routing change: it is NO LONGER on LaunchWarnings (which now carries
            // only always-shown §10.4 policy notes).
            Assert.DoesNotContain(liveSession.Session.LaunchWarnings, m => m.Contains("NOCOUNT is forced ON"));

            while (!liveSession.Session.IsCompleted)
            {
                var (_, stepMessages) = await liveSession.Session.StepAsync();
                debuggeeMessages.AddRange(stepMessages);
                diagnostics.AddRange(liveSession.Session.DrainDiagnosticNotes());
            }

            // C2: one note naming the triggered table, citing the caveat; the INSERT
            // fires TWICE against it but the sys.triggers lookup is cached — never a
            // second note (and never one for the trigger-free table at all). The C5
            // note does NOT repeat — one shared one-time flag, already latched at init.
            Assert.Single(diagnostics, m => m.Contains("m7_c2_probe_with_trigger") && m.Contains("triggers"));
            Assert.DoesNotContain(diagnostics, m => m.Contains("m7_c2_probe_no_trigger"));
            Assert.Single(diagnostics, m => m.Contains("NOCOUNT is forced ON"));

            // A56 separation invariant: neither diagnostic note ever leaked into the
            // debuggee `messages` stream (which carries PRINT/results/errors — always shown).
            Assert.DoesNotContain(debuggeeMessages, m => m.Contains("NOCOUNT is forced ON"));
            Assert.DoesNotContain(debuggeeMessages, m => m.Contains("triggers"));
        }
        finally
        {
            await DropFixtureAsync(rawConnectionString!);
        }
    }

    // M7 FYI(a) (orchestrator ruling 2026-07-08), updated for A56: the anti-hollow pin
    // (the p04 lesson) that C2's note is ACTUALLY observable in the live adapter's Debug
    // Console — end-to-end over the real DAP stdio wire. As of A56 the note is a
    // logLevel-gated diagnostic, so this launches with logLevel:"verbose" to exercise the
    // shown path; the companion test below pins that "normal" SUPPRESSES it. Steps OVER
    // the INSERT into the triggered table and waits for the note as a genuine Console
    // OutputEvent in the adapter's own event stream.
    [SkippableFact]
    public async Task DmlTriggerNote_ReachesTheDebugConsole_UnderVerbose_OverTheRealDapWire()
    {
        var rawConnectionString = Environment.GetEnvironmentVariable(ConnEnvVar);
        Skip.If(
            string.IsNullOrWhiteSpace(rawConnectionString),
            $"{ConnEnvVar} is not set; skipping live probe (never fake a pass — CLAUDE.md).");

        var csb = new SqlConnectionStringBuilder(rawConnectionString);

        await DeployFixtureAsync(rawConnectionString!);
        try
        {
            var tracePath = Path.Combine(Path.GetTempPath(), "m7-c2-console-wire.jsonl");
            if (File.Exists(tracePath))
            {
                File.Delete(tracePath);
            }

            var scriptPath = Path.Combine(Path.GetTempPath(), "m7-c2-console-wire.sql");
            // Line 1: the INSERT into the triggered table (the C2 trigger source).
            // Line 2: a trailing stop target so `next` past the INSERT lands
            // somewhere inspectable rather than ending the session.
            await File.WriteAllTextAsync(scriptPath, "INSERT dbo.m7_c2_probe_with_trigger (v) VALUES (1);\nSELECT 1;\n");

            var targetsFile = Path.Combine(AppContext.BaseDirectory, "targets.json");

            await using var dap = DapStdioHarness.Launch(tracePath);
            await dap.SendRequestAsync("initialize", new JsonObject { ["clientID"] = "m7-c2-console-wire-live-test", ["adapterID"] = "tsqldbg" });
            await dap.SendRequestAsync("launch", new JsonObject
            {
                ["server"] = csb.DataSource,
                ["database"] = csb.InitialCatalog,
                ["mode"] = "script",
                ["script"] = scriptPath,
                ["targetsFile"] = targetsFile,
                ["stopOnEntry"] = true,
                ["logLevel"] = "verbose",   // A56: the C2 note is a gated diagnostic — verbose shows it
            });
            await dap.WaitForEventAsync("initialized");
            await dap.SendRequestAsync("configurationDone", new JsonObject());
            await dap.WaitForEventAsync("stopped", e => e["body"]!["reason"]!.GetValue<string>() == "entry");

            // Step OVER the INSERT — this is the step whose returned Messages carry
            // the C2 note; the forwarding fix emits it as a Console OutputEvent BEFORE
            // the resulting step-stop.
            await dap.SendRequestAsync("next", new JsonObject { ["threadId"] = 1 });

            var note = await dap.WaitForEventAsync("output", e =>
                e["body"]!["category"]?.GetValue<string>() == "console"
                && (e["body"]!["output"]?.GetValue<string>()?.Contains("m7_c2_probe_with_trigger") ?? false)
                && (e["body"]!["output"]?.GetValue<string>()?.Contains("triggers") ?? false),
                TimeSpan.FromSeconds(15));

            // The exact assertion this pin exists for: the C2 note reached the console
            // stream, end-to-end, as a real DAP OutputEvent (category=console).
            Assert.Equal("console", note["body"]!["category"]!.GetValue<string>());
            Assert.Contains("C2", note["body"]!["output"]!.GetValue<string>());

            // ...and the session still stopped normally right after (the note precedes
            // the stop, never replaces it).
            await dap.WaitForEventAsync("stopped", e => e["body"]!["reason"]!.GetValue<string>() == "step", TimeSpan.FromSeconds(15));

            await dap.DisposeAsync();
        }
        finally
        {
            await DropFixtureAsync(rawConnectionString!);
        }
    }

    // A56: the core of Ivan's request — at the DEFAULT logLevel ("normal") the debugger's
    // diagnostic annotations do NOT reach the Debug Console. Same scenario as above with
    // no logLevel (default normal); collects every event up to the step-stop and asserts
    // NO console note about triggers arrived. Uses CollectEventsUntilAsync because the
    // discard-on-mismatch WaitForEventAsync cannot assert an absence. This is invisible to
    // the fidelity harness (adapter presentation) — must be observed over the real wire.
    [SkippableFact]
    public async Task DmlTriggerNote_IsSuppressedAtNormalLogLevel_OverTheRealDapWire()
    {
        var rawConnectionString = Environment.GetEnvironmentVariable(ConnEnvVar);
        Skip.If(
            string.IsNullOrWhiteSpace(rawConnectionString),
            $"{ConnEnvVar} is not set; skipping live probe (never fake a pass — CLAUDE.md).");

        var csb = new SqlConnectionStringBuilder(rawConnectionString);

        await DeployFixtureAsync(rawConnectionString!);
        try
        {
            var tracePath = Path.Combine(Path.GetTempPath(), "a56-c2-console-normal.jsonl");
            if (File.Exists(tracePath))
            {
                File.Delete(tracePath);
            }

            var scriptPath = Path.Combine(Path.GetTempPath(), "a56-c2-console-normal.sql");
            await File.WriteAllTextAsync(scriptPath, "INSERT dbo.m7_c2_probe_with_trigger (v) VALUES (1);\nSELECT 1;\n");

            var targetsFile = Path.Combine(AppContext.BaseDirectory, "targets.json");

            await using var dap = DapStdioHarness.Launch(tracePath);
            await dap.SendRequestAsync("initialize", new JsonObject { ["clientID"] = "a56-c2-console-normal-live-test", ["adapterID"] = "tsqldbg" });
            await dap.SendRequestAsync("launch", new JsonObject
            {
                ["server"] = csb.DataSource,
                ["database"] = csb.InitialCatalog,
                ["mode"] = "script",
                ["script"] = scriptPath,
                ["targetsFile"] = targetsFile,
                ["stopOnEntry"] = true,
                // No logLevel key -> default "normal" -> diagnostic notes suppressed.
            });
            await dap.WaitForEventAsync("initialized");
            await dap.SendRequestAsync("configurationDone", new JsonObject());
            await dap.WaitForEventAsync("stopped", e => e["body"]!["reason"]!.GetValue<string>() == "entry");

            await dap.SendRequestAsync("next", new JsonObject { ["threadId"] = 1 });

            // Collect everything up to and including the step-stop that follows `next`.
            var events = await dap.CollectEventsUntilAsync(
                "stopped", e => e["body"]!["reason"]!.GetValue<string>() == "step", TimeSpan.FromSeconds(15));

            // The gate: no console OutputEvent carrying the C2 trigger note arrived before
            // the stop (the INSERT still executed and still stopped — only the note is hidden).
            Assert.DoesNotContain(events, e =>
                e["event"]?.GetValue<string>() == "output"
                && e["body"]?["category"]?.GetValue<string>() == "console"
                && (e["body"]?["output"]?.GetValue<string>()?.Contains("triggers") ?? false));

            await dap.DisposeAsync();
        }
        finally
        {
            await DropFixtureAsync(rawConnectionString!);
        }
    }

    private static async Task DeployFixtureAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            IF OBJECT_ID('dbo.m7_c2_probe_with_trigger') IS NOT NULL DROP TABLE dbo.m7_c2_probe_with_trigger;
            IF OBJECT_ID('dbo.m7_c2_probe_no_trigger') IS NOT NULL DROP TABLE dbo.m7_c2_probe_no_trigger;
            CREATE TABLE dbo.m7_c2_probe_with_trigger (id int IDENTITY PRIMARY KEY, v int NULL);
            CREATE TABLE dbo.m7_c2_probe_no_trigger (id int IDENTITY PRIMARY KEY, v int NULL);
            """;
        await command.ExecuteNonQueryAsync();

        await using var triggerCommand = connection.CreateCommand();
        triggerCommand.CommandText = """
            CREATE TRIGGER dbo.trg_m7_c2_probe ON dbo.m7_c2_probe_with_trigger AFTER INSERT AS
            BEGIN
                SET NOCOUNT ON;
            END
            """;
        await triggerCommand.ExecuteNonQueryAsync();
    }

    private static async Task DropFixtureAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            IF OBJECT_ID('dbo.m7_c2_probe_with_trigger') IS NOT NULL DROP TABLE dbo.m7_c2_probe_with_trigger;
            IF OBJECT_ID('dbo.m7_c2_probe_no_trigger') IS NOT NULL DROP TABLE dbo.m7_c2_probe_no_trigger;
            """;
        await command.ExecuteNonQueryAsync();
    }
}
