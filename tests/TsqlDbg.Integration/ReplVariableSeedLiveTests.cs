using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Xunit;

namespace TsqlDbg.Integration;

// A45 (2026-07-12, docs/archive/reviews/repl-variable-seed-opus.md): the Debug Console must see
// the current frame's variables. `SELECT @x` used to fail with error 137 ("must declare
// the scalar variable @x") because BuildForRepl never declared/seeded the frame's
// variables — only Build (the interpreted-statement path) did. A45 seeds them read-only
// in the console batch too, mirroring Build exactly (healthy/detached: a plain table
// read; doomed: a snapshot parameter). These tests reproduce the EXACT user gesture over
// the real DAP wire (DEBUG CONSOLE -> evaluate context:"repl") and assert the seeded
// value comes back — the coverage the fidelity harness (RunToEnd, no console) can't give.
public sealed class ReplVariableSeedLiveTests
{
    private const string ConnEnvVar = "TSQLDBG_TEST_CONN";

    private static async Task<int> StepAsync(DapStdioHarness dap)
    {
        await dap.SendRequestAsync("next", new JsonObject { ["threadId"] = 1 });
        var stop = await dap.WaitForEventAsync("stopped");
        return stop["body"]!["threadId"]!.GetValue<int>();
    }

    private static async Task<string> EvaluateReplAsync(DapStdioHarness dap, string expression)
    {
        var frames = await dap.SendRequestAsync("stackTrace", new JsonObject { ["threadId"] = 1 });
        var frameId = frames["body"]!["stackFrames"]!.AsArray()[0]!["id"]!.GetValue<int>();
        var eval = await dap.SendRequestAsync("evaluate", new JsonObject
        {
            ["expression"] = expression,
            ["context"] = "repl",
            ["frameId"] = frameId,
        });
        return eval["body"]!["result"]!.GetValue<string>();
    }

    // The exact report: right-click a variable set earlier in the batch, open the Debug
    // Console, type `SELECT @x` -> it must return the value, not error 137. @x starts 41
    // and becomes 42, so asserting 42 (and NOT 41) proves the seed reads the LIVE value,
    // not the declare-time initializer.
    [SkippableFact]
    public async Task Healthy_ConsoleSelectOfAFrameVariable_ReturnsLiveValue()
    {
        var raw = Environment.GetEnvironmentVariable(ConnEnvVar);
        Skip.If(string.IsNullOrWhiteSpace(raw), $"{ConnEnvVar} is not set; skipping live probe (never fake a pass — CLAUDE.md).");

        var csb = new SqlConnectionStringBuilder(raw);
        var tracePath = Path.Combine(Path.GetTempPath(), "a45-repl-varseed-healthy.jsonl");
        if (File.Exists(tracePath))
        {
            File.Delete(tracePath);
        }

        var scriptPath = Path.Combine(Path.GetTempPath(), "a45-repl-varseed-healthy.sql");
        await File.WriteAllTextAsync(scriptPath, "DECLARE @x int = 41;\nSET @x = 42;\nSELECT @x;\n");
        var targetsFile = Path.Combine(AppContext.BaseDirectory, "targets.json");

        await using var dap = DapStdioHarness.Launch(tracePath);
        await dap.SendRequestAsync("initialize", new JsonObject { ["clientID"] = "a45-healthy", ["adapterID"] = "tsqldbg" });
        await dap.SendRequestAsync("launch", new JsonObject
        {
            ["server"] = csb.DataSource,
            ["database"] = csb.InitialCatalog,
            ["mode"] = "script",
            ["script"] = scriptPath,
            ["targetsFile"] = targetsFile,
            ["stopOnEntry"] = true,
        });
        await dap.WaitForEventAsync("initialized");
        await dap.SendRequestAsync("configurationDone", new JsonObject());
        await dap.WaitForEventAsync("stopped", e => e["body"]!["reason"]!.GetValue<string>() == "entry");

        await StepAsync(dap);   // DECLARE @x = 41
        await StepAsync(dap);   // SET @x = 42  (now stopped at SELECT @x, not yet executed)

        var rendered = await EvaluateReplAsync(dap, "SELECT @x AS x;");

        Assert.Contains("42", rendered);                       // the live value, seeded into the console batch
        Assert.DoesNotContain("41", rendered);                 // not the stale declare-time initializer
        Assert.DoesNotContain("137", rendered);                // NOT error 137 "must declare the scalar variable @x"
        Assert.DoesNotContain("must declare", rendered.ToLowerInvariant());

        await dap.DisposeAsync();
    }

    // The doomed arm: the console read while the transaction is doomed seeds @x from the
    // binary snapshot via a PARAMETER (the state table is stale/unwritable under 3930),
    // which flips the batch to sp_executesql AND carries the redoom prefix. This pins
    // that combination live end-to-end (the shape the debuggee doomed path already uses,
    // now exercised through the console). SET XACT_ABORT ON makes the caught 1/0 doom.
    [SkippableFact]
    public async Task Doomed_ConsoleSelectOfAFrameVariable_ReturnsSnapshotValue()
    {
        var raw = Environment.GetEnvironmentVariable(ConnEnvVar);
        Skip.If(string.IsNullOrWhiteSpace(raw), $"{ConnEnvVar} is not set; skipping live probe (never fake a pass — CLAUDE.md).");

        var csb = new SqlConnectionStringBuilder(raw);
        var tracePath = Path.Combine(Path.GetTempPath(), "a45-repl-varseed-doomed.jsonl");
        if (File.Exists(tracePath))
        {
            File.Delete(tracePath);
        }

        var scriptPath = Path.Combine(Path.GetTempPath(), "a45-repl-varseed-doomed.sql");
        await File.WriteAllTextAsync(scriptPath,
            "DECLARE @x int = 7;\n" +
            "SET XACT_ABORT ON;\n" +
            "BEGIN TRY\n" +
            "    SELECT 1/0;\n" +
            "END TRY\n" +
            "BEGIN CATCH\n" +
            "    SELECT ERROR_NUMBER() AS e;\n" +
            "END CATCH\n");
        var targetsFile = Path.Combine(AppContext.BaseDirectory, "targets.json");

        await using var dap = DapStdioHarness.Launch(tracePath);
        await dap.SendRequestAsync("initialize", new JsonObject { ["clientID"] = "a45-doomed", ["adapterID"] = "tsqldbg" });
        await dap.SendRequestAsync("launch", new JsonObject
        {
            ["server"] = csb.DataSource,
            ["database"] = csb.InitialCatalog,
            ["mode"] = "script",
            ["script"] = scriptPath,
            ["targetsFile"] = targetsFile,
            ["stopOnEntry"] = true,
        });
        await dap.WaitForEventAsync("initialized");
        await dap.SendRequestAsync("configurationDone", new JsonObject());
        await dap.WaitForEventAsync("stopped", e => e["body"]!["reason"]!.GetValue<string>() == "entry");

        await StepAsync(dap);   // DECLARE @x = 7
        await StepAsync(dap);   // SET XACT_ABORT ON
        await StepAsync(dap);   // SELECT 1/0 -> dooms (XACT_ABORT ON), routes to CATCH; now stopped in CATCH

        var rendered = await EvaluateReplAsync(dap, "SELECT @x AS x;");

        Assert.Contains("7", rendered);                        // the snapshot value, seeded via parameter while doomed
        Assert.DoesNotContain("137", rendered);                // NOT error 137
        Assert.DoesNotContain("must declare", rendered.ToLowerInvariant());

        await dap.DisposeAsync();
    }

    private static async Task<string> LocalValueAsync(DapStdioHarness dap, string variableName)
    {
        var frames = await dap.SendRequestAsync("stackTrace", new JsonObject { ["threadId"] = 1 });
        var frameId = frames["body"]!["stackFrames"]!.AsArray()[0]!["id"]!.GetValue<int>();
        var scopes = await dap.SendRequestAsync("scopes", new JsonObject { ["frameId"] = frameId });
        var localsRef = scopes["body"]!["scopes"]!.AsArray()
            .First(s => s!["name"]!.GetValue<string>() == "Locals")!["variablesReference"]!.GetValue<int>();
        var vars = await dap.SendRequestAsync("variables", new JsonObject { ["variablesReference"] = localsRef });
        return vars["body"]!["variables"]!.AsArray()
            .First(v => v!["name"]!.GetValue<string>() == variableName)!["value"]!.GetValue<string>();
    }

    // A46: under allowConsoleWrites, a console `SET @x = …` PERSISTS — proven three ways:
    // a follow-up console read returns the new value, and the Variables panel (scopes ->
    // Locals -> variables) shows it too (the adapter invalidated + refetched).
    [SkippableFact]
    public async Task WriteMode_ConsoleSetVariable_PersistsForReadAndVariablesPanel()
    {
        var raw = Environment.GetEnvironmentVariable(ConnEnvVar);
        Skip.If(string.IsNullOrWhiteSpace(raw), $"{ConnEnvVar} is not set; skipping live probe (never fake a pass — CLAUDE.md).");

        var csb = new SqlConnectionStringBuilder(raw);
        var tracePath = Path.Combine(Path.GetTempPath(), "a46-console-setvar.jsonl");
        if (File.Exists(tracePath))
        {
            File.Delete(tracePath);
        }

        var scriptPath = Path.Combine(Path.GetTempPath(), "a46-console-setvar.sql");
        await File.WriteAllTextAsync(scriptPath, "DECLARE @x int = 1;\nSELECT @x;\n");
        var targetsFile = Path.Combine(AppContext.BaseDirectory, "targets.json");

        await using var dap = DapStdioHarness.Launch(tracePath);
        await dap.SendRequestAsync("initialize", new JsonObject { ["clientID"] = "a46-setvar", ["adapterID"] = "tsqldbg" });
        await dap.SendRequestAsync("launch", new JsonObject
        {
            ["server"] = csb.DataSource,
            ["database"] = csb.InitialCatalog,
            ["mode"] = "script",
            ["script"] = scriptPath,
            ["targetsFile"] = targetsFile,
            ["stopOnEntry"] = true,
            ["allowConsoleWrites"] = true,
        });
        await dap.WaitForEventAsync("initialized");
        await dap.SendRequestAsync("configurationDone", new JsonObject());
        await dap.WaitForEventAsync("stopped", e => e["body"]!["reason"]!.GetValue<string>() == "entry");

        await StepAsync(dap);   // DECLARE @x = 1 (now stopped at SELECT @x)
        Assert.Equal("1", await LocalValueAsync(dap, "@x"));

        // The gesture: change @x in the console.
        var setRendered = await EvaluateReplAsync(dap, "SET @x = 42;");
        Assert.DoesNotContain("faulted", setRendered);

        // Persisted: a follow-up console read sees 42, and so does the Variables panel.
        Assert.Contains("42", await EvaluateReplAsync(dap, "SELECT @x AS x;"));
        Assert.Equal("42", await LocalValueAsync(dap, "@x"));

        await dap.DisposeAsync();
    }

    // A46 / Ivan's other ask: editing temp-table CONTENTS via the console persists (the
    // backing table is a real object in the session) — a console UPDATE #t then a console
    // read of #t shows the new value.
    [SkippableFact]
    public async Task WriteMode_ConsoleTempTableEdit_Persists()
    {
        var raw = Environment.GetEnvironmentVariable(ConnEnvVar);
        Skip.If(string.IsNullOrWhiteSpace(raw), $"{ConnEnvVar} is not set; skipping live probe (never fake a pass — CLAUDE.md).");

        var csb = new SqlConnectionStringBuilder(raw);
        var tracePath = Path.Combine(Path.GetTempPath(), "a46-console-tempedit.jsonl");
        if (File.Exists(tracePath))
        {
            File.Delete(tracePath);
        }

        var scriptPath = Path.Combine(Path.GetTempPath(), "a46-console-tempedit.sql");
        await File.WriteAllTextAsync(scriptPath,
            "CREATE TABLE #t (v int);\nINSERT INTO #t (v) VALUES (7);\nSELECT v FROM #t;\n");
        var targetsFile = Path.Combine(AppContext.BaseDirectory, "targets.json");

        await using var dap = DapStdioHarness.Launch(tracePath);
        await dap.SendRequestAsync("initialize", new JsonObject { ["clientID"] = "a46-tempedit", ["adapterID"] = "tsqldbg" });
        await dap.SendRequestAsync("launch", new JsonObject
        {
            ["server"] = csb.DataSource,
            ["database"] = csb.InitialCatalog,
            ["mode"] = "script",
            ["script"] = scriptPath,
            ["targetsFile"] = targetsFile,
            ["stopOnEntry"] = true,
            ["allowConsoleWrites"] = true,
        });
        await dap.WaitForEventAsync("initialized");
        await dap.SendRequestAsync("configurationDone", new JsonObject());
        await dap.WaitForEventAsync("stopped", e => e["body"]!["reason"]!.GetValue<string>() == "entry");

        await StepAsync(dap);   // CREATE TABLE #t
        await StepAsync(dap);   // INSERT #t (v = 7); now stopped at SELECT

        Assert.Contains("7", await EvaluateReplAsync(dap, "SELECT v FROM #t;"));   // baseline

        var updRendered = await EvaluateReplAsync(dap, "UPDATE #t SET v = 99;");
        Assert.DoesNotContain("faulted", updRendered);

        var afterEdit = await EvaluateReplAsync(dap, "SELECT v FROM #t;");
        Assert.Contains("99", afterEdit);                 // the edit persisted to the real temp table
        Assert.DoesNotContain("7", afterEdit);            // and the old value is gone

        await dap.DisposeAsync();
    }

    // A46 doomed arm: while the transaction is DOOMED, a console `SET @x` is allowed (it
    // touches no table) and persists to the frame snapshot — proven by a follow-up console
    // read and the Variables panel, exactly like the healthy case but through the doomed
    // (sp_executesql + redoom + snapshot) path. A database write would still be refused.
    [SkippableFact]
    public async Task Doomed_ConsoleSetVariable_Persists_ViaSnapshot()
    {
        var raw = Environment.GetEnvironmentVariable(ConnEnvVar);
        Skip.If(string.IsNullOrWhiteSpace(raw), $"{ConnEnvVar} is not set; skipping live probe (never fake a pass — CLAUDE.md).");

        var csb = new SqlConnectionStringBuilder(raw);
        var tracePath = Path.Combine(Path.GetTempPath(), "a46-doomed-setvar.jsonl");
        if (File.Exists(tracePath))
        {
            File.Delete(tracePath);
        }

        var scriptPath = Path.Combine(Path.GetTempPath(), "a46-doomed-setvar.sql");
        await File.WriteAllTextAsync(scriptPath,
            "DECLARE @x int = 7;\n" +
            "SET XACT_ABORT ON;\n" +
            "BEGIN TRY\n" +
            "    SELECT 1/0;\n" +
            "END TRY\n" +
            "BEGIN CATCH\n" +
            "    SELECT ERROR_NUMBER() AS e;\n" +
            "END CATCH\n");
        var targetsFile = Path.Combine(AppContext.BaseDirectory, "targets.json");

        await using var dap = DapStdioHarness.Launch(tracePath);
        await dap.SendRequestAsync("initialize", new JsonObject { ["clientID"] = "a46-doomed-setvar", ["adapterID"] = "tsqldbg" });
        await dap.SendRequestAsync("launch", new JsonObject
        {
            ["server"] = csb.DataSource,
            ["database"] = csb.InitialCatalog,
            ["mode"] = "script",
            ["script"] = scriptPath,
            ["targetsFile"] = targetsFile,
            ["stopOnEntry"] = true,
            ["allowConsoleWrites"] = true,
        });
        await dap.WaitForEventAsync("initialized");
        await dap.SendRequestAsync("configurationDone", new JsonObject());
        await dap.WaitForEventAsync("stopped", e => e["body"]!["reason"]!.GetValue<string>() == "entry");

        await StepAsync(dap);   // DECLARE @x = 7
        await StepAsync(dap);   // SET XACT_ABORT ON
        await StepAsync(dap);   // SELECT 1/0 -> dooms (XACT_ABORT ON), routes to CATCH

        Assert.Contains("7", await EvaluateReplAsync(dap, "SELECT @x AS x;"));   // doomed read baseline

        // The gesture, while doomed: change @x. Must NOT be refused (it's a variable-only
        // write), and must persist.
        var setRendered = await EvaluateReplAsync(dap, "SET @x = 42;");
        Assert.DoesNotContain("faulted", setRendered);
        Assert.DoesNotContain("3930", setRendered);

        Assert.Contains("42", await EvaluateReplAsync(dap, "SELECT @x AS x;"));   // persisted (doomed snapshot)
        Assert.Equal("42", await LocalValueAsync(dap, "@x"));                     // and the Variables panel shows it

        await dap.DisposeAsync();
    }
}
