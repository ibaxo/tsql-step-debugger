using TsqlDbg.Core.Execution;
using TsqlDbg.Core.Interpreter;

namespace TsqlDbg.Core.Sessions;

// DESIGN §24.3 (A73): Mode A — drive a session to completion with the auto-stepper,
// capturing a per-statement record. Hoisted out of the MCP host so both hosts share one
// loop: the MCP `trace_*` tools (RunTraceAsync) and the DAP adapter's `traceRun` launch
// mode (§17/A73). The runner owns stepping, capture, the §24.8 "changed" variable diff,
// error routing, and the summary projection; hosts own teardown/commit gating, keep-alive,
// rendering, and file placement.

/// <summary>DESIGN §24.3/§24.4 (A73): the Mode A capture knobs (`trace_*` args / §17 traceRun).</summary>
public sealed record TraceRunOptions(
    StepKind StepKind,
    bool CaptureTempRowCounts,
    // §24.8/A70: false = "changed" (per-frame delta, the default), true = "full"
    // (complete per-step snapshot). The diff is client-side over the same read either way.
    bool FullVariableCapture);

/// <summary>DESIGN §24.8 (A73): one per-statement trace record, host-agnostic — raw Core
/// result sets (hosts apply their own row/char caps when rendering or serializing).</summary>
public sealed record TraceStepRecord(
    int Seq,
    int? FrameOrdinal,                                    // §24.5 frame id — null when no frame was live
    ModuleIdentity? Module,                               // full identity — hosts render Display / build Sources
    int Line,
    string? StatementText,
    IReadOnlyDictionary<string, string>? VariablesAfter,   // "full" capture only
    IReadOnlyDictionary<string, string>? VariablesChanged, // "changed" capture only; absent = state unreadable
    IReadOnlyDictionary<string, string>? TempRowCounts,
    IReadOnlyList<string> Output,
    IReadOnlyList<ResultSet> ResultSets,
    TraceErrorRecord? Error,                               // only in an active error context
    IReadOnlyList<string> Notes);

/// <summary>DESIGN §24.5/§10 (A73): the active error at a trace step (ErrorInfo's shape).</summary>
public sealed record TraceErrorRecord(
    int Number,
    int Severity,
    int State,
    int? Line,
    string? Procedure,
    string Message,
    string RoutedTo);                                      // "catch" | "unhandled" | "faultSite" | "terminal"

/// <summary>DESIGN §24.8 (A73): how the run ended.</summary>
public enum TraceFinalState
{
    Completed,
    Faulted,
    Incomplete,   // cancelled mid-run (pause/stop) — a partial trace, never commits
}

/// <summary>DESIGN §24.8 summary projection (A73): return code, frame-0 OUTPUT-param final
/// values (procedure mode, best-effort), deduped messages led by LaunchWarnings (A70).</summary>
public sealed record TraceRunResult(
    int ReturnCode,
    IReadOnlyDictionary<string, string>? OutputParams,
    IReadOnlyList<string> Messages,
    TraceFinalState FinalState,
    int StepCount);

public static class TraceRunner
{
    // DESIGN §24.3/§24.8 (A73): step to completion, invoking `onStep` once per executed
    // statement. `keepAlive` fires each iteration (the MCP host's idle-sweep Touch; the
    // adapter passes null). Cancellation ends the trace cleanly (FinalState.Incomplete) —
    // the in-flight statement dies via the §10.5 attention and the cursor is unchanged.
    public static async Task<TraceRunResult> RunAsync(
        Session session, TraceRunOptions options, Func<TraceStepRecord, Task> onStep,
        Action? keepAlive, CancellationToken ct)
    {
        var seq = 0;
        // A70: launch warnings lead the summary messages — Mode A has no entry stop, so the
        // summary is the only place the reader can learn e.g. that an OUTPUT param was
        // NULL-seeded.
        var messages = new List<string>(session.LaunchWarnings);
        // A70: per-frame baseline for the "changed" diff, keyed by frame identity (a re-entered
        // frame is a new object, so recursion gets a fresh full baseline). Popped frames are
        // pruned each iteration so a deep step-into trace cannot accumulate dead baselines.
        var previousVars = new Dictionary<Frame, Dictionary<string, string>>();
        while (!session.IsCompleted)
        {
            keepAlive?.Invoke();
            if (session.AtImplicitReturn)
            {
                // §11.5/A54: the parked implicit-return stop is an inspection artifact, not a
                // statement — consume it without a record. Notes are drained (not recorded) so
                // they never leak into the next step's line.
                IReadOnlyList<string> retMsgs;
                try
                {
                    (_, retMsgs) = await session.StepAsync(StepKind.Over, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                messages.AddRange(retMsgs);
                session.DrainDiagnosticNotes();
                continue;
            }

            var frame = session.TopFrame;
            var current = session.Current;
            IReadOnlyList<ResultSet> sets;
            IReadOnlyList<string> msgs;
            try
            {
                (sets, msgs) = await session.StepAsync(options.StepKind, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            seq++;
            messages.AddRange(msgs);
            var notes = session.DrainDiagnosticNotes();

            Dictionary<string, string>? vars = null;
            Dictionary<string, string>? changed = null;
            Dictionary<string, string>? tempCounts = null;
            // Capture the frame's post-statement state, but never let an inspection read
            // abort the whole trace: a step that popped/faulted the frame (FrameCompleted,
            // FrameFaulted) can leave its state table gone. Record "unavailable" instead
            // (A70: as an ABSENT variables field — nulls are omitted from the line).
            if (!session.IsBroken && frame is not null && session.Frames.Any(f => ReferenceEquals(f, frame)))
            {
                try
                {
                    vars = await CaptureVariablesAsync(session, frame, ct).ConfigureAwait(false);
                    if (!options.FullVariableCapture)
                    {
                        changed = DiffVariables(previousVars.TryGetValue(frame, out var prev) ? prev : null, vars);
                        previousVars[frame] = vars;
                        vars = null;   // the line carries the delta, not the snapshot
                    }

                    if (options.CaptureTempRowCounts)
                    {
                        tempCounts = await CaptureTempCountsAsync(session, frame, ct).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    // Do NOT update previousVars — the next successful read diffs against the
                    // last good baseline, so no change is silently swallowed by the error step.
                    var failure = new Dictionary<string, string> { ["__capture_error"] = ex.Message };
                    vars = options.FullVariableCapture ? failure : null;
                    changed = options.FullVariableCapture ? null : failure;
                }
            }

            if (previousVars.Count > session.Frames.Count)
            {
                foreach (var dead in previousVars.Keys.Where(f => !session.Frames.Any(live => ReferenceEquals(live, f))).ToList())
                {
                    previousVars.Remove(dead);
                }
            }

            await onStep(new TraceStepRecord(
                seq, frame?.Ordinal, frame?.Module, current?.Span.StartLine ?? 0, current?.Text,
                vars, changed, tempCounts, msgs, sets, CurrentError(session), notes)).ConfigureAwait(false);
        }

        var returnCode = session.Frames.Count > 0 ? session.Frames[0].ReturnCode : 0;
        var finalState = session.IsBroken ? TraceFinalState.Faulted
            : session.IsCompleted ? TraceFinalState.Completed
            : TraceFinalState.Incomplete;
        var outputParams = await CaptureOutputParamsAsync(session, ct).ConfigureAwait(false);
        return new TraceRunResult(returnCode, outputParams, DedupeMessages(messages), finalState, seq);
    }

    private static async Task<Dictionary<string, string>> CaptureVariablesAsync(
        Session session, Frame frame, CancellationToken ct)
    {
        var snap = await session.GetStateSnapshotAsync(frame, ct).ConfigureAwait(false);
        var map = new Dictionary<string, string>();
        foreach (var slot in frame.Variables.All)
        {
            var has = snap.TryGet(slot.Declaration.Name, out var v);
            map[slot.Declaration.Name] = !has || v is null ? "NULL" : v.ToString() ?? "NULL";
        }

        return map;
    }

    // A70 (§24.8): the "changed" projection — variables whose rendered value differs from the
    // same frame's previous stop. A frame's first stop (previous == null) is a full baseline.
    // Catalog order is preserved (current iterates in registration order); a variable can never
    // LEAVE the map mid-frame (§8.2 hoisting — the catalog is fixed at frame init), so absence
    // from `changed` always means "same value as the last time it appeared".
    internal static Dictionary<string, string> DiffVariables(
        Dictionary<string, string>? previous, Dictionary<string, string> current)
    {
        if (previous is null)
        {
            return current;
        }

        var delta = new Dictionary<string, string>();
        foreach (var (name, value) in current)
        {
            if (!previous.TryGetValue(name, out var before) || !string.Equals(before, value, StringComparison.Ordinal))
            {
                delta[name] = value;
            }
        }

        return delta;
    }

    // A70 (§24.8): collapse repeated identical messages ("Cannot step into X — stepping over"
    // fires per call site) into one entry with a count, preserving first-occurrence order.
    public static IReadOnlyList<string> DedupeMessages(IReadOnlyList<string> messages)
    {
        var counts = new Dictionary<string, int>();
        var order = new List<string>();
        foreach (var m in messages)
        {
            if (counts.TryGetValue(m, out var c))
            {
                counts[m] = c + 1;
            }
            else
            {
                counts[m] = 1;
                order.Add(m);
            }
        }

        return order.Select(m => counts[m] == 1 ? m : $"{m} (occurred {counts[m]}×)").ToList();
    }

    private static async Task<Dictionary<string, string>> CaptureTempCountsAsync(
        Session session, Frame frame, CancellationToken ct)
    {
        var map = new Dictionary<string, string>();
        if (session.IsDoomed || session.IsBroken)
        {
            return map;
        }

        foreach (var entry in frame.TempObjects.All)
        {
            if (entry.IsDead || entry.Kind == TempObjectKind.Cursor)
            {
                continue;
            }

            var (count, fault) = await session.GetTempObjectRowCountAsync(entry.PhysicalName, ct).ConfigureAwait(false);
            map[entry.OriginalName] = fault is not null ? $"error: {fault}" : (count?.ToString() ?? "?");
        }

        return map;
    }

    // §10.2/§10.6: the error active at this step — the ErrorContextStack top inside a CATCH,
    // else whatever fault LastStep carried, mapped to the §24.5 routedTo label.
    private static TraceErrorRecord? CurrentError(Session session)
    {
        var active = session.ActiveErrorContext?.Values;
        var error = active ?? session.LastStep.Error;
        if (error is null)
        {
            return null;
        }

        var disp = session.LastStep.Disposition;
        var routedTo = active is not null ? "catch"
            : disp == StepDisposition.FrameFaulted ? "terminal"
            : disp is StepDisposition.FaultAtSite or StepDisposition.DoomedTempPreflight ? "faultSite"
            : "unhandled";

        return new TraceErrorRecord(error.Number, error.Severity, error.State, error.Line, error.Procedure, error.Message, routedTo);
    }

    // A70 (§24.8/§24.4): the frame-0 OUTPUT parameters' final values for the summary —
    // procedure mode only (frame 0 is a module, not the script root), best-effort (null
    // when frame 0 is gone or its state unreadable; a summary field must never fail the
    // trace or block teardown). Read BEFORE teardown. Public (A73): the MCP end_session
    // summary reads the same projection outside a trace run.
    public static async Task<Dictionary<string, string>?> CaptureOutputParamsAsync(
        Session session, CancellationToken ct)
    {
        if (session.IsBroken || session.Frames.Count == 0 || session.Frames[0].Module.IsScript)
        {
            return null;
        }

        var frameZero = session.Frames[0];
        var outputSlots = frameZero.Variables.All.Where(s => s.Declaration.IsOutputParameter).ToList();
        if (outputSlots.Count == 0)
        {
            return null;
        }

        try
        {
            var snap = await session.GetStateSnapshotAsync(frameZero, ct).ConfigureAwait(false);
            var map = new Dictionary<string, string>();
            foreach (var slot in outputSlots)
            {
                var has = snap.TryGet(slot.Declaration.Name, out var v);
                map[slot.Declaration.Name] = !has || v is null ? "NULL" : v.ToString() ?? "NULL";
            }

            return map;
        }
        catch
        {
            return null;
        }
    }
}
