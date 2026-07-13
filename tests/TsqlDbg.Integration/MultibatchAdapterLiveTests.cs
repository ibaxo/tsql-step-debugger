using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;

namespace TsqlDbg.Integration;

// M8 (§5.4) + A43/A44 adapter lane: DapStdioHarness pins for the DAP-wire multi-batch
// items the design notes assign to the adapter — breakpoints span the WHOLE script file
// (§7/§13, A36); `GO N` runs the batch N times (A43); and `continue`/`stepOut` run
// *through* a GO boundary while `next`/`stepIn` stop at a batch entry (A44,
// docs/archive/reviews/multibatch-continue-boundary-opus.md). All drive the REAL compiled adapter
// over stdio (same DapStdioHarness pattern as BoostAdapterBreakpointRefusalLiveTests /
// PackagedSelfContainedSmokeTests), never the Core Session API directly — this proves the
// ADAPTER's own wiring (setBreakpoints line->(batch,SU) mapping, RunUntilAsync's
// BatchCompleted keep-going, StepOnceLockedAsync's per-step boundary stop, the annotation),
// not just the Core mechanism lane 1a/1b already unit-tested.
public sealed class MultibatchAdapterLiveTests
{
    private const string ConnEnvVar = "TSQLDBG_TEST_CONN";

    // A 4-batch script mirroring p26's shape (scope reset per GO, #temp persists, batch 3
    // fails and the client continues to batch 4) but sized so EVERY batch has more than
    // one statement where useful — batch 2 (line 6-7) and batch 4 (line 11-12) each have a
    // NON-entry line, so the batch-entry `step` stop and the breakpoint's own `breakpoint`
    // stop land on visibly DIFFERENT lines instead of coinciding. Batch 3 fails via an
    // explicit THROW (a batch-aborting, non-dooming RUNTIME fault reaching
    // PerformRouteAsync's terminal arm the ordinary way — §8.3) rather than an undeclared-
    // variable reference: a live probe while building this pin found that an undeclared
    // reference is instead caught CLIENT-SIDE by ControlFlowMap.BuildAndValidate
    // (script-mode compile-time validation, pre-existing since M2) when EnterBatchAsync
    // builds that batch's OWN StatementIndex — for batch ORDINAL 0 that's correctly a
    // launch failure, but for a batch entered mid-session via AdvanceToNextBatchAsync the
    // resulting ParseTimeDiagnosticException is not caught/classified as a batch-terminal
    // fault there and propagates uncaught, ending the whole session instead of advancing
    // (reproduced by tests/corpus/p26_multibatch_go_script.sql, which uses exactly that
    // shape per its ratified design-note spec and is RED on first run for this reason —
    // see docs/archive/reviews/m8-multibatch-adapter-sonnet.md; a Core/§10-gated escalation, out
    // of this adapter lane's scope, NOT fixed here). THROW sidesteps it entirely and
    // proves the DAP-level continuation UX this lane IS responsible for, using a fault
    // shape lane 1b's own reviewed tests already cover. Line numbers (1-based, exact —
    // verified against this literal text):
    //   1  SET NOCOUNT ON;
    //   2  CREATE TABLE #acc (label nvarchar(20), n int);
    //   3  DECLARE @acc int = 100;
    //   4  INSERT INTO #acc (label, n) VALUES (N'b1', @acc);
    //   5  GO                                          -- batch 1 (index 0): lines 1-4
    //   6  DECLARE @acc nvarchar(10) = N'two';
    //   7  INSERT INTO #acc (label, n) VALUES (@acc, 2);   -- breakpoint A
    //   8  GO                                          -- batch 2 (index 1): lines 6-7
    //   9  THROW 50000, N'deliberate batch-3 failure', 1;  -- unhandled, batch-terminal
    //  10  GO                                          -- batch 3 (index 2): line 9
    //  11  DECLARE @dummy int = 1;
    //  12  SELECT label, n FROM #acc ORDER BY label;       -- breakpoint B
    //  13  GO                                          -- batch 4 (index 3): lines 11-12
    private const string Script = """
        SET NOCOUNT ON;
        CREATE TABLE #acc (label nvarchar(20), n int);
        DECLARE @acc int = 100;
        INSERT INTO #acc (label, n) VALUES (N'b1', @acc);
        GO
        DECLARE @acc nvarchar(10) = N'two';
        INSERT INTO #acc (label, n) VALUES (@acc, 2);
        GO
        THROW 50000, N'deliberate batch-3 failure', 1;
        GO
        DECLARE @dummy int = 1;
        SELECT label, n FROM #acc ORDER BY label;
        GO
        """;

    private const int Batch2BreakpointLine = 7;
    private const int Batch4BreakpointLine = 12;

    private static (string? ConnString, string TracePath, string ScriptPath) SkipUnlessLive(
        string traceFileName, string scriptText)
    {
        var raw = Environment.GetEnvironmentVariable(ConnEnvVar);
        Skip.If(string.IsNullOrWhiteSpace(raw), $"{ConnEnvVar} is not set; skipping live probe (never fake a pass — CLAUDE.md).");

        // M7 hygiene precedent (docs/archive/reviews/m8-multibatch-adapter-sonnet.md): trace
        // output goes to a SCRATCH path, never a committed docs/archive/reviews/*.jsonl path --
        // a live-test run must never dirty the tree. The m8 smoke trace artifact is a
        // one-time captured copy of this same scenario's trace, not regenerated here.
        var tracePath = Path.Combine(Path.GetTempPath(), traceFileName);
        if (File.Exists(tracePath))
        {
            File.Delete(tracePath);
        }

        var scriptPath = Path.Combine(Path.GetTempPath(), $"{Path.GetFileNameWithoutExtension(traceFileName)}.sql");
        File.WriteAllText(scriptPath, scriptText);
        return (raw, tracePath, scriptPath);
    }

    private static async Task<DapStdioHarness> LaunchScriptAsync(
        string connectionString, string tracePath, string scriptPath, string clientId)
    {
        var csb = new SqlConnectionStringBuilder(connectionString);
        var targetsFile = Path.Combine(AppContext.BaseDirectory, "targets.json");

        var dap = DapStdioHarness.Launch(tracePath);
        await dap.SendRequestAsync("initialize", new JsonObject { ["clientID"] = clientId, ["adapterID"] = "tsqldbg" });
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
        return dap;
    }

    // A36/§7: "setBreakpoints spans the whole file ... a breakpoint in a not-yet-active
    // batch binds now and fires when that batch becomes the active scope." Both
    // breakpoints below are requested while batch 1 (index 0) is the only live frame --
    // proving Session.TryMapScriptBreakpointLine resolves batch 2's and batch 4's lines
    // without either batch ever having been entered yet. The continue sequence then proves
    // the A44 semantics (docs/archive/reviews/multibatch-continue-boundary-opus.md): `continue`
    // runs THROUGH every GO boundary (healthy AND fault-recovery) and halts only at a
    // breakpoint or an exception -- so the two breakpoints are the only planned stops, each
    // reached across ≥ 1 boundary, and a batch-terminal fault (batch 3's THROW) still lets
    // the client continue on to batch 4 rather than ending the session.
    [SkippableFact]
    public async Task BreakpointsAcrossBatches_ContinueRunsThroughBoundaries_StoppingOnlyAtBreakpoints()
    {
        var (connString, tracePath, scriptPath) = SkipUnlessLive("m8-breakpoints-across-batches.jsonl", Script);

        await using var dap = await LaunchScriptAsync(connString!, tracePath, scriptPath, "m8-multibatch-breakpoints-live-test");

        // Both requests bind while batch 1 (index 0) is the ONLY resolved live frame --
        // batch 2 (line 7) and batch 4 (line 12) are not-yet-active at this point.
        var setBp = await dap.SendRequestAsync("setBreakpoints", new JsonObject
        {
            ["source"] = new JsonObject { ["path"] = scriptPath },
            ["breakpoints"] = new JsonArray(
                new JsonObject { ["line"] = Batch2BreakpointLine },
                new JsonObject { ["line"] = Batch4BreakpointLine }),
        });
        var bps = setBp["body"]!["breakpoints"]!.AsArray();
        Assert.Equal(2, bps.Count);
        Assert.True(bps[0]!["verified"]!.GetValue<bool>(), "batch 2's line must verify immediately, before batch 2 is ever active");
        Assert.Equal(Batch2BreakpointLine, bps[0]!["line"]!.GetValue<int>());
        Assert.True(bps[1]!["verified"]!.GetValue<bool>(), "batch 4's line must verify immediately, before batch 4 is ever active");
        Assert.Equal(Batch4BreakpointLine, bps[1]!["line"]!.GetValue<int>());

        await dap.SendRequestAsync("configurationDone", new JsonObject());
        await dap.WaitForEventAsync("stopped", e => e["body"]!["reason"]!.GetValue<string>() == "entry");
        Assert.Equal(1, (await LineOfTopFrameAsync(dap))); // batch 1 (index 0) entry, line 1

        // A44: `continue` runs THROUGH GO boundaries -- each AssertNextStopAsync checks the
        // VERY NEXT stopped event (no reason filter), so a spurious batch-entry `step` stop
        // (the pre-A44 behavior) would fail the reason check here instead of being skipped.

        // continue #1: runs batch 1's remaining SUs (lines 2-4), crosses the batch 1->2
        // boundary WITHOUT stopping, runs line 6 (DECLARE), and halts at line 7's
        // breakpoint -- the FIRST stop is the breakpoint, never a batch-2-entry step.
        await dap.SendRequestAsync("continue", new JsonObject { ["threadId"] = 1 });
        await AssertNextStopAsync(dap, "breakpoint", Batch2BreakpointLine);

        // continue #2: runs line 7 (batch 2's last SU), crosses the batch 2->3 boundary
        // WITHOUT stopping, and executes line 9 -- THROW, unhandled, a batch-terminal
        // (non-dooming) fault (§8.3). FrameFaulted is a terminal always-stop (§10.6), so
        // the fault SITE stops (red exception UI) -- that is a fault stop, not a boundary
        // stop; the session is NOT broken (batch-terminal, not connection-fatal).
        await dap.SendRequestAsync("continue", new JsonObject { ["threadId"] = 1 });
        await AssertNextStopAsync(dap, "exception", 9);

        // continue #3: consumes PendingBatchAdvance (crosses the fault-recovery boundary
        // WITHOUT re-executing line 9), crosses ON into batch 4 WITHOUT stopping at its
        // entry (line 11), runs line 11 (DECLARE), and halts at line 12's breakpoint.
        // Reaching batch 4's breakpoint at all proves the client continued past the failed
        // batch 3; reaching it in ONE continue (no step@11) is the A44 proof.
        await dap.SendRequestAsync("continue", new JsonObject { ["threadId"] = 1 });
        await AssertNextStopAsync(dap, "breakpoint", Batch4BreakpointLine);

        // continue #4: executes line 12 (the final SELECT) -- the LAST SU of the LAST
        // batch -- ordinary session completion.
        await dap.SendRequestAsync("continue", new JsonObject { ["threadId"] = 1 });
        await dap.WaitForEventAsync("terminated", timeout: TimeSpan.FromSeconds(15));

        await dap.DisposeAsync();

        // §19: --trace output must parse 0-unparseable.
        var traceLines = await File.ReadAllLinesAsync(tracePath);
        Assert.NotEmpty(traceLines);
        var unparseable = traceLines.Where(l => !TryParseJsonLine(l)).ToList();
        Assert.True(unparseable.Count == 0, $"{unparseable.Count} unparseable trace line(s): {string.Join(" | ", unparseable.Take(3))}");
    }

    // §5.4 / A43: `GO N` repeat count is SUPPORTED (materialize-N). Proves, through the
    // ADAPTER, that a `GO N` script LAUNCHES successfully (the A31 refusal is lifted), runs
    // the batch N times, and the batch-frame annotation shows the live iteration
    // `[batch 1/1 ×i/3]` incrementing across the iteration boundaries. Walks the iterations
    // with `next`: under A44 `continue` runs straight THROUGH every GO boundary, so a single
    // continue here would execute all three iterations to completion in one go -- `next`
    // stops at each iteration entry (StepOnceLockedAsync's per-step boundary stop), which is
    // how a user observes each iteration. End-to-end stepping fidelity vs native is pinned by
    // P28GoRepeatCountFidelityTests.
    [SkippableFact]
    public async Task GoRepeatCount_NextStepsThroughEachIteration_AndAnnotatesIt()
    {
        // One physical batch (`SELECT 1;`) repeated 3x -> three materialized iterations.
        var (connString, tracePath, scriptPath) = SkipUnlessLive("m8-go-n-repeat.jsonl", "SELECT 1;\nGO 3\n");

        // Reaching past LaunchScriptAsync (it awaits `initialized`) proves the launch
        // SUCCEEDED — the A31 refusal is lifted.
        await using var dap = await LaunchScriptAsync(connString!, tracePath, scriptPath, "m8-go-n-repeat-live-test");

        await dap.SendRequestAsync("configurationDone", new JsonObject());

        // Iteration 1: entry at the SELECT (line 1), annotated with the live iteration.
        await dap.WaitForEventAsync("stopped", e => e["body"]!["reason"]!.GetValue<string>() == "entry");
        Assert.Equal(1, await LineOfTopFrameAsync(dap));
        Assert.Contains("[batch 1/1 ×1/3]", await NameOfTopFrameAsync(dap));

        // next -> iteration 1's SELECT (its last SU) runs -> GO boundary -> iteration 2
        // entry at line 1 again (a fresh scope), annotation now ×2/3.
        await dap.SendRequestAsync("next", new JsonObject { ["threadId"] = 1 });
        await AssertNextStopAsync(dap, "step", 1);
        Assert.Contains("[batch 1/1 ×2/3]", await NameOfTopFrameAsync(dap));

        // next -> iteration 3 entry, ×3/3.
        await dap.SendRequestAsync("next", new JsonObject { ["threadId"] = 1 });
        await AssertNextStopAsync(dap, "step", 1);
        Assert.Contains("[batch 1/1 ×3/3]", await NameOfTopFrameAsync(dap));

        // next -> iteration 3's SELECT (last SU of the last iteration) -> session completes.
        await dap.SendRequestAsync("next", new JsonObject { ["threadId"] = 1 });
        await dap.WaitForEventAsync("terminated", timeout: TimeSpan.FromSeconds(15));

        await dap.DisposeAsync();
    }

    // A44 regression -- the exact report: "Run to Cursor into the next batch just stays at
    // the first line of that batch." VS Code implements Run to Cursor as *set a temporary
    // breakpoint at the cursor + continue*, which at the DAP wire is indistinguishable from
    // this: stopped at batch 1's entry, set ONE breakpoint on a NON-entry line of batch 2,
    // then continue. Pre-A44 the continue stopped at batch 2's ENTRY line (the GO-boundary
    // over-stop) and never reached the cursor; A44 makes it run through the boundary and halt
    // exactly on the breakpoint line. Asserting the VERY NEXT stop (no reason filter) is what
    // makes this a true regression: a batch-entry `step` stop would fail here.
    [SkippableFact]
    public async Task ContinueIntoLaterBatch_StopsAtBreakpoint_NotAtBatchEntry()
    {
        //   1  SELECT 1;          -- batch 1 (index 0)
        //   2  GO
        //   3  SELECT 2;          -- batch 2 (index 1) ENTRY line
        //   4  SELECT 3;          -- batch 2 -- the "cursor" (breakpoint) target, NOT the entry
        //   5  GO
        const string script = "SELECT 1;\nGO\nSELECT 2;\nSELECT 3;\nGO\n";
        const int cursorLine = 4;   // a non-entry line of batch 2
        var (connString, tracePath, scriptPath) = SkipUnlessLive("a44-run-to-cursor.jsonl", script);

        await using var dap = await LaunchScriptAsync(connString!, tracePath, scriptPath, "a44-run-to-cursor-live-test");

        // The "Run to Cursor" gesture: a single breakpoint deep in batch 2, set while batch 1
        // is the only live frame (it binds via TryMapScriptBreakpointLine, A36).
        var setBp = await dap.SendRequestAsync("setBreakpoints", new JsonObject
        {
            ["source"] = new JsonObject { ["path"] = scriptPath },
            ["breakpoints"] = new JsonArray(new JsonObject { ["line"] = cursorLine }),
        });
        Assert.True(setBp["body"]!["breakpoints"]!.AsArray()[0]!["verified"]!.GetValue<bool>());

        await dap.SendRequestAsync("configurationDone", new JsonObject());
        await dap.WaitForEventAsync("stopped", e => e["body"]!["reason"]!.GetValue<string>() == "entry");
        Assert.Equal(1, await LineOfTopFrameAsync(dap)); // batch 1 entry

        // continue: runs batch 1 (line 1), crosses the GO boundary WITHOUT stopping at batch
        // 2's entry (line 3), runs line 3, and halts on the cursor breakpoint at line 4. The
        // VERY NEXT stop must be reason:breakpoint @4 -- never a batch-entry step @3.
        await dap.SendRequestAsync("continue", new JsonObject { ["threadId"] = 1 });
        await AssertNextStopAsync(dap, "breakpoint", cursorLine);

        // continue: runs line 4 (last SU of the last batch) -> session completes.
        await dap.SendRequestAsync("continue", new JsonObject { ["threadId"] = 1 });
        await dap.WaitForEventAsync("terminated", timeout: TimeSpan.FromSeconds(15));

        await dap.DisposeAsync();
    }

    // Strict: assert the VERY NEXT `stopped` event (any reason) matches. Because
    // WaitForEventAsync with no predicate returns the next `stopped` regardless of reason, a
    // spurious stop between two expected ones (e.g. the pre-A44 batch-entry step) fails the
    // reason assertion here instead of being silently filtered out and skipped.
    private static async Task AssertNextStopAsync(DapStdioHarness dap, string expectedReason, int expectedLine)
    {
        var stop = await dap.WaitForEventAsync("stopped");
        Assert.Equal(expectedReason, stop["body"]!["reason"]!.GetValue<string>());
        Assert.Equal(1, stop["body"]!["threadId"]!.GetValue<int>());
        Assert.Equal(expectedLine, await LineOfTopFrameAsync(dap));
    }

    private static async Task<int> LineOfTopFrameAsync(DapStdioHarness dap)
    {
        var trace = await dap.SendRequestAsync("stackTrace", new JsonObject { ["threadId"] = 1 });
        var top = trace["body"]!["stackFrames"]!.AsArray()[0]!;
        return top["line"]!.GetValue<int>();
    }

    // §5.4/A43: the bottom batch frame's NAME carries the `[batch k/N ×i/M]` annotation.
    private static async Task<string> NameOfTopFrameAsync(DapStdioHarness dap)
    {
        var trace = await dap.SendRequestAsync("stackTrace", new JsonObject { ["threadId"] = 1 });
        var top = trace["body"]!["stackFrames"]!.AsArray()[0]!;
        return top["name"]!.GetValue<string>();
    }

    private static bool TryParseJsonLine(string line)
    {
        try
        {
            JsonNode.Parse(line);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
