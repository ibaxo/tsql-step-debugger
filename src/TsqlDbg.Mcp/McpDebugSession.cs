using TsqlDbg.Core.Execution;
using TsqlDbg.Core.Interpreter;
using TsqlDbg.Core.Sessions;
using TsqlDbg.Core.State;
using TsqlDbg.Core.Targets;
using TsqlDbg.Core.Tracing;

namespace TsqlDbg.Mcp;

// DESIGN §24.2/§24.3: one live debug session on the programmatic surface. Wraps a Core
// LiveSession behind a single async gate (MARS off / C20: one command at a time per
// connection). Unlike the adapter there is no separate protocol thread racing a background
// stepper — MCP is request/response, so each tool runs its Core work inline under the gate
// and returns the resulting stop state (§24.5). No new interpreter logic lives here: every
// driver mirrors the adapter's own loops (StepOnceLockedAsync / RunUntilAsync / teardown)
// over the same public Session API (§24.0).
public sealed class McpDebugSession : IAsyncDisposable
{
    private readonly Session _session;
    // Teardown seam (§24.1(2)): in production this is LiveSession.DisposeAsync (rolls back —
    // or commits when the decision says so — then disposes the executor + connection). The
    // Session/teardown split (rather than holding a LiveSession directly) is what lets the
    // driver be unit-tested against a fake IStatementExecutor with no real connection.
    private readonly Func<Func<Task<bool>>?, ValueTask> _teardownAsync;
    private readonly TargetEntry _target;
    private readonly SessionArgs _args;
    private readonly SemaphoreSlim _gate = new(1, 1);

    // DESIGN §24.6/§13: breakpoints keyed per module identity (a script's batches all share
    // ModuleIdentity.Script(), so a breakpoint verifies across batches, §5.4/A36). Add-only
    // relative to §13.
    // M11 gate (MINOR): keyed with a CASE-INSENSITIVE identity comparer. An agent types a
    // procedure name ("dbo.Proc") whose case may differ from the catalog-cased identity the
    // pushed frame carries; SQL resolves names case-insensitively, so the breakpoint verifies,
    // but a case-sensitive record-equality lookup in the run loop would then silently never fire.
    private readonly Dictionary<ModuleIdentity, Dictionary<int, BreakpointEntry>> _breakpointsByModule =
        new(ModuleIdentityComparer.Instance);
    private StatementUnit? _lastBreakpointStopUnit;   // skip-once gate (§6 continue)

    // DESIGN §10.6 exception filters — same defaults as the adapter (unhandled on). The 'all'
    // filter is applied directly via Session.BreakOnAllErrors (→ FaultAtSite disposition), so no
    // separate host-side flag is kept for it.
    private bool _stopOnCaught;
    private bool _stopOnUnhandled = true;

    private int _stepCount;
    private string _lastStopReason = "entry";

    public string SessionId { get; }
    public string Server => _args.Server;
    public string Database => _args.Database;
    public string Mode => _args.Mode;
    public DateTime LastActivityUtc { get; private set; } = DateTime.UtcNow;
    public bool Committed { get; private set; }

    // DESIGN §24.4 list_sessions: best-effort, lock-free status for the (cross-session) listing.
    // Slightly stale under a concurrent step is acceptable — it never drives control flow.
    public int? CurrentLine
    {
        get
        {
            try { return S.TopFrame?.Cursor.Current?.Span.StartLine; }
            catch { return null; }
        }
    }

    public string StateLabel => S.IsBroken ? "faulted" : S.IsCompleted ? "completed" : "stopped";

    // M11 gate (MINOR): case-insensitive ModuleIdentity equality for the breakpoint store —
    // Database/Schema/Name compared ordinal-ignore-case (SQL name semantics), IsScript/IsDynamic
    // exact. Without this an agent-typed procedure name whose case differs from the catalog's
    // would verify a breakpoint that then never fires.
    private sealed class ModuleIdentityComparer : IEqualityComparer<ModuleIdentity>
    {
        public static readonly ModuleIdentityComparer Instance = new();
        private static readonly StringComparer Ci = StringComparer.OrdinalIgnoreCase;

        public bool Equals(ModuleIdentity? x, ModuleIdentity? y)
        {
            if (ReferenceEquals(x, y))
            {
                return true;
            }

            if (x is null || y is null)
            {
                return false;
            }

            return Ci.Equals(x.Database, y.Database)
                && Ci.Equals(x.Schema, y.Schema)
                && Ci.Equals(x.Name, y.Name)
                && x.IsScript == y.IsScript
                && x.IsDynamic == y.IsDynamic;
        }

        public int GetHashCode(ModuleIdentity obj) => HashCode.Combine(
            obj.Database is null ? 0 : Ci.GetHashCode(obj.Database),
            obj.Schema is null ? 0 : Ci.GetHashCode(obj.Schema),
            Ci.GetHashCode(obj.Name),
            obj.IsScript,
            obj.IsDynamic);
    }

    private sealed class BreakpointEntry
    {
        public required int Line { get; init; }
        public string? Condition { get; init; }
        public HitCondition? Hit { get; init; }
        public int HitCount { get; set; }
    }

    private McpDebugSession(string sessionId, LiveSession live, TargetEntry target, SessionArgs args)
        : this(sessionId, live.Session, live.DisposeAsync, target, args)
    {
    }

    // Test/DI seam: construct over an already-initialized Session and an explicit teardown
    // delegate, so the driver can be exercised against a fake IStatementExecutor (no connection).
    internal McpDebugSession(
        string sessionId, Session session, Func<Func<Task<bool>>?, ValueTask> teardownAsync,
        TargetEntry target, SessionArgs args)
    {
        SessionId = sessionId;
        _session = session;
        _teardownAsync = teardownAsync;
        _target = target;
        _args = args;
    }

    private Session S => _session;

    // DESIGN §24.2 open: allowlist-gate is the caller's job (TargetResolver); this opens the
    // Core LiveSession (which runs §4 init: connect, parse, state table, BEGIN TRAN).
    public static async Task<McpDebugSession> OpenAsync(
        string sessionId, SessionArgs args, TargetEntry target, ITraceSink? trace, string? password, CancellationToken ct)
    {
        var options = args.ToSessionOptions();
        var live = await LiveSession.OpenAsync(options, target, trace, ct, password).ConfigureAwait(false);
        return new McpDebugSession(sessionId, live, target, args);
    }

    // ---- entry stop -------------------------------------------------------------

    // DESIGN §24.2: the initial stop, positioned at entry (InitializeAsync already put the
    // cursor on the first statement). A pure publish — no step.
    public async Task<StopState> GetEntryStateAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            Touch();
            _lastStopReason = "entry";
            // A70: the entry stop is the launch report — surface Core's LaunchWarnings (empty-TVP
            // start, OUTPUT-param NULL seed, compat clamp, …) as the StopState.Warnings the DTO
            // has always documented. Previously only the adapter surfaced them (§12.3 console).
            return BuildStopState("entry", Array.Empty<string>(), Array.Empty<ResultSet>(), S.LaunchWarnings);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<StopState> GetStateAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            Touch();
            return BuildStopState(_lastStopReason, Array.Empty<string>(), Array.Empty<ResultSet>());
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---- stepping (mirrors StepOnceLockedAsync) ---------------------------------

    // DESIGN §24.4 step: granularity over|in|out. over/in are a single StepAsync; out is the
    // adapter's continue-until-depth-shrinks loop (§11.5, no session verb).
    public async Task<StopState> StepAsync(string granularity, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Touch();
            if (string.Equals(granularity, "out", StringComparison.OrdinalIgnoreCase))
            {
                var entryDepth = S.Frames.Count;
                return await RunUntilLockedAsync(() => S.Frames.Count >= entryDepth, "step", ct).ConfigureAwait(false);
            }

            var kind = string.Equals(granularity, "in", StringComparison.OrdinalIgnoreCase) ? StepKind.Into : StepKind.Over;
            return await StepOnceLockedAsync(kind, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<StopState> StepOnceLockedAsync(StepKind kind, CancellationToken ct)
    {
        if (S.IsCompleted)
        {
            return BuildCompleted();
        }

        IReadOnlyList<string> messages;
        IReadOnlyList<ResultSet> resultSets;
        try
        {
            (resultSets, messages) = await S.StepAsync(kind, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // §10.5: cursor unchanged — publish a pause stop.
            return BuildStopStateReason("pause", Array.Empty<string>(), Array.Empty<ResultSet>());
        }

        var warnings = DrainNotes();

        if (S.IsCompleted)
        {
            return BuildCompleted(messages, resultSets, warnings);
        }

        _stepCount++;
        var disp = S.LastStep.Disposition;
        var reason = disp == StepDisposition.EngineAttention ? "pause"
            : IsExceptionDisposition(disp) ? "exception"
            : "step";
        _lastStopReason = reason;
        return BuildStopState(reason, messages, resultSets, warnings);
    }

    // ---- continue (mirrors RunUntilAsync) ---------------------------------------

    public async Task<StopState> ContinueAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Touch();
            return await RunUntilLockedAsync(() => true, "step", ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    // Shared by continue and stepOut. Steps (StepKind.Over) while keepGoing() holds, honoring
    // breakpoints, the COMMIT stop, exception filters, GO boundaries, and implicit-return
    // pass-through exactly as the adapter's RunUntilAsync does. Accumulates output/result sets
    // across the run so the agent sees everything the continued span produced.
    private async Task<StopState> RunUntilLockedAsync(Func<bool> keepGoing, string boundaryReason, CancellationToken ct)
    {
        if (S.IsCompleted)
        {
            return BuildCompleted();
        }

        var output = new List<string>();
        var resultSets = new List<ResultSet>();
        var warnings = new List<string>();

        while (!S.IsCompleted && keepGoing())
        {
            Touch();   // M11 gate (MAJOR): keep a progressing continue/stepOut from being swept as idle
            // A54 (§6/§11.5): a module parked at its implicit return — under continue/stepOut
            // this is NOT a stop; consume it and keep going (Current is null while parked, so
            // this must run before the breakpoint check reads Current).
            if (S.AtImplicitReturn)
            {
                try
                {
                    var (_, retMessages) = await S.StepAsync(StepKind.Over, ct).ConfigureAwait(false);
                    output.AddRange(retMessages);
                    warnings.AddRange(DrainNotes());
                }
                catch (OperationCanceledException)
                {
                    return Accumulate("pause", output, resultSets, warnings);
                }

                continue;
            }

            var current = S.Current!;
            var skipCheck = ReferenceEquals(current, _lastBreakpointStopUnit);
            _lastBreakpointStopUnit = null;

            if (!skipCheck)
            {
                var topModule = S.TopFrame?.Module;
                if (topModule is not null
                    && _breakpointsByModule.TryGetValue(topModule, out var moduleBps)
                    && moduleBps.TryGetValue(current.Span.StartLine, out var bp)
                    && await ShouldBreakAsync(bp).ConfigureAwait(false))
                {
                    _lastBreakpointStopUnit = current;
                    _lastStopReason = "breakpoint";
                    return Accumulate("breakpoint", output, resultSets, warnings);
                }

                if (current.SubKind == SuSubKind.Commit)
                {
                    // §10.4: stop-before-COMMIT exactly once (skip-once gate). The agent must
                    // continue again to actually commit the debugger's own safety transaction.
                    _lastBreakpointStopUnit = current;
                    output.Add(
                        $"About to execute COMMIT at line {current.Span.StartLine} (§10.4) — this would durably commit " +
                        "the debugger's own safety transaction. Continue again to confirm, or use goto to skip it.");
                    _lastStopReason = "breakpoint";
                    return Accumulate("breakpoint", output, resultSets, warnings);
                }
            }

            try
            {
                var (stepSets, stepMessages) = await S.StepAsync(StepKind.Over, ct).ConfigureAwait(false);
                output.AddRange(stepMessages);
                resultSets.AddRange(stepSets);
                warnings.AddRange(DrainNotes());
            }
            catch (OperationCanceledException)
            {
                return Accumulate("pause", output, resultSets, warnings);
            }

            var disp = S.LastStep.Disposition;
            if (disp == StepDisposition.EngineAttention)
            {
                return Accumulate("pause", output, resultSets, warnings);
            }

            // §10.6: FaultAtSite / FrameFaulted always stop; RoutedToCatch / UnhandledContinued
            // / DoomedTempPreflight stop only per the active filters. A GO boundary
            // (BatchCompleted) is keep-going, like native (§5.4/A44).
            if (ShouldStopForExceptionFilters(disp))
            {
                _lastStopReason = "exception";
                return Accumulate("exception", output, resultSets, warnings);
            }
        }

        if (S.IsCompleted)
        {
            return BuildCompleted(output, resultSets, warnings);
        }

        _lastStopReason = boundaryReason;
        return Accumulate(boundaryReason, output, resultSets, warnings);
    }

    private StopState Accumulate(string reason, List<string> output, List<ResultSet> resultSets, List<string> warnings)
        => BuildStopState(reason, output, resultSets, warnings);

    // ---- goto -------------------------------------------------------------------

    // DESIGN §24.4/§13 goto: move the cursor to a line in the current (top) frame without executing
    // skipped code (loud note).
    public async Task<StopState> GotoAsync(int line, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Touch();
            var index = S.Index;
            if (index is null)
            {
                throw new InvalidOperationException("No active statement index.");
            }

            if (!index.TryMapBreakpointLine(line, out var unit))
            {
                throw new ArgumentException($"No statement maps to line {line} in the current frame.");
            }

            S.JumpTo(unit);
            var note =
                $"Jumped to line {unit.Span.StartLine} — state does not change; any skipped statements did not execute (§13).";
            _lastStopReason = "goto";
            return BuildStopState("goto", new[] { note }, Array.Empty<ResultSet>());
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---- breakpoints ------------------------------------------------------------

    // DESIGN §24.6/§13: verify a whole set for one location (replaces that location's set).
    public async Task<IReadOnlyList<BreakpointInfo>> SetBreakpointsAsync(
        BreakpointLocation location, IReadOnlyList<BreakpointRequest> requests, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Touch();
            var (identity, error) = await ResolveLocationAsync(location).ConfigureAwait(false);
            if (identity is null)
            {
                return requests.Select(r => new BreakpointInfo(r.Line, false, null, error)).ToList();
            }

            var results = new List<BreakpointInfo>();
            var mapped = new Dictionary<int, BreakpointEntry>();
            foreach (var r in requests)
            {
                StatementUnit? unit = null;
                bool ok;
                if (identity.IsScript)
                {
                    ok = S.TryMapScriptBreakpointLine(r.Line, out _, out unit);
                }
                else
                {
                    var (idx, _) = await S.TryGetModuleIndexAsync(identity).ConfigureAwait(false);
                    ok = idx is not null && idx.TryMapBreakpointLine(r.Line, out unit);
                }

                if (ok && unit is not null)
                {
                    mapped[unit.Span.StartLine] = new BreakpointEntry
                    {
                        Line = unit.Span.StartLine,
                        Condition = string.IsNullOrWhiteSpace(r.Condition) ? null : r.Condition,
                        Hit = HitCondition.Parse(r.HitCondition),
                    };
                    results.Add(new BreakpointInfo(r.Line, true, unit.Span.StartLine, null));
                }
                else
                {
                    results.Add(new BreakpointInfo(r.Line, false, null, "No statement maps at or after this line in the target."));
                }
            }

            _breakpointsByModule[identity] = mapped;
            return results;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ClearBreakpointsAsync(BreakpointLocation location, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Touch();
            var (identity, _) = await ResolveLocationAsync(location).ConfigureAwait(false);
            if (identity is not null)
            {
                _breakpointsByModule.Remove(identity);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetExceptionFiltersAsync(bool all, bool caught, bool unhandled, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Touch();
            _stopOnCaught = caught;
            _stopOnUnhandled = unhandled;
            S.BreakOnAllErrors = all;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<(ModuleIdentity? Identity, string? Error)> ResolveLocationAsync(BreakpointLocation location)
    {
        switch (location.Kind?.ToLowerInvariant())
        {
            case "script":
                var scriptRoot = S.Frames.Count > 0 ? S.Frames[0].Module : null;
                return scriptRoot is { IsScript: true }
                    ? (scriptRoot, null)
                    : (ModuleIdentity.Script(), null);

            case "procedure":
                if (string.IsNullOrWhiteSpace(location.Name))
                {
                    return (null, "location kind 'procedure' requires 'name' (e.g. 'dbo.uspChild').");
                }

                var dot = location.Name.IndexOf('.');
                var schema = dot >= 0 ? location.Name[..dot] : "dbo";
                var name = dot >= 0 ? location.Name[(dot + 1)..] : location.Name;
                var procIdentity = new ModuleIdentity(_args.Database, schema, name, IsScript: false);
                var (index, msg) = await S.TryGetModuleIndexAsync(procIdentity).ConfigureAwait(false);
                return index is not null ? (procIdentity, null) : (null, msg ?? "Could not resolve the procedure definition.");

            case "file":
                if (string.IsNullOrWhiteSpace(location.Path))
                {
                    return (null, "location kind 'file' requires 'path'.");
                }

                return S.TryResolveModuleBySourceFile(location.Path, out var matched) && matched is not null
                    ? (matched, null)
                    : (null, "No live module's server definition matches this file yet (sourceMap, §5.2).");

            default:
                return (null, "location.kind must be 'script', 'procedure', or 'file'.");
        }
    }

    // DESIGN §13: condition first (Core-backed exact eval, touches no shadow state), then
    // hit count. A faulting condition breaks anyway (never silently past a breakpoint).
    private async Task<bool> ShouldBreakAsync(BreakpointEntry bp)
    {
        if (bp.Condition is not null)
        {
            var (value, fault) = await S.EvaluateConditionAsync(bp.Condition).ConfigureAwait(false);
            if (fault is not null)
            {
                return true;
            }

            if (value != true)
            {
                return false;
            }
        }

        if (bp.Hit is { } hit)
        {
            bp.HitCount++;
            return hit.ShouldStop(bp.HitCount);
        }

        return true;
    }

    // ---- inspection -------------------------------------------------------------

    public async Task<IReadOnlyList<VariableInfo>> GetVariablesAsync(int frameId, string scope, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Touch();
            var frame = S.Frames.FirstOrDefault(f => f.Ordinal == frameId)
                ?? throw new ArgumentException($"No frame with id {frameId}.");

            switch (scope?.ToLowerInvariant())
            {
                case "locals":
                    var snap = await S.GetStateSnapshotAsync(frame, ct).ConfigureAwait(false);
                    return frame.Variables.All.Select(slot =>
                    {
                        var has = snap.TryGet(slot.Declaration.Name, out var v);
                        var display = !has || v is null ? "NULL" : v.ToString() ?? "NULL";
                        return new VariableInfo(slot.Declaration.Name, display, slot.Declaration.DataTypeSql);
                    }).ToList();

                case "system":
                    return BuildSystemScope();

                case "temp":
                    return await BuildTempScopeAsync(frameId, ct).ConfigureAwait(false);

                case "errorcontext":
                    var ec = S.ActiveErrorContext?.Values;
                    return ec is null
                        ? new List<VariableInfo>()
                        : new List<VariableInfo>
                        {
                            new("Number", ec.Number.ToString(), null),
                            new("Severity", ec.Severity.ToString(), null),
                            new("State", ec.State.ToString(), null),
                            new("Line", ec.Line?.ToString() ?? "NULL", null),
                            new("Procedure", ec.Procedure ?? "NULL", null),
                            new("Message", ec.Message, null),
                        };

                default:
                    throw new ArgumentException("scope must be 'locals', 'system', 'temp', or 'errorContext'.");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private List<VariableInfo> BuildSystemScope()
    {
        var mode = S.IsBroken ? "broken" : S.IsDoomed ? "doomed" : S.IsTransactionDetached ? "detached" : "healthy";
        var list = new List<VariableInfo>
        {
            new("@@TRANCOUNT", S.LastObservedTrancount.ToString(), null),
            new("XACT_STATE()", S.LastObservedXactState.ToString(), null),
            new("@@SPID", S.Spid.ToString(), null),
            new("Session mode", mode, null),
        };
        foreach (var (option, value) in S.RuntimeOptionsSnapshot)
        {
            list.Add(new VariableInfo(option, value, null));
        }

        return list;
    }

    // DESIGN §12.2: chain-resolved temp objects for the frame, rowcount/cursor status filled
    // lazily (healthy only; doomed/broken use the eager client-side certainty display).
    private async Task<List<VariableInfo>> BuildTempScopeAsync(int frameId, CancellationToken ct)
    {
        var allFrames = S.Frames;
        var frameIndex = -1;
        for (var i = 0; i < allFrames.Count; i++)
        {
            if (allFrames[i].Ordinal == frameId)
            {
                frameIndex = i;
                break;
            }
        }

        if (frameIndex < 0)
        {
            return new List<VariableInfo>();
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<VariableInfo>();
        for (var i = frameIndex; i >= 0; i--)
        {
            var entries = allFrames[i].TempObjects.All;
            for (var e = entries.Count - 1; e >= 0; e--)
            {
                var entry = entries[e];
                if (entry.IsDead || !seen.Add(entry.OriginalName))
                {
                    continue;
                }

                string display;
                if (S.IsBroken)
                {
                    display = "(session terminated)";
                }
                else if (S.IsDoomed)
                {
                    display = entry.Kind == TempObjectKind.TableVariable
                        ? "(0 rows — contents lost across rollback, C25)"
                        : entry.Kind == TempObjectKind.Cursor ? "(cursor)" : "(destroyed by doomed rollback, C23)";
                }
                else if (entry.Kind == TempObjectKind.Cursor)
                {
                    var (status, fault) = await S.GetCursorStatusAsync(entry.PhysicalName, ct).ConfigureAwait(false);
                    display = fault is not null ? $"(error: {fault})" : $"({status})";
                }
                else
                {
                    var (count, fault) = await S.GetTempObjectRowCountAsync(entry.PhysicalName, ct).ConfigureAwait(false);
                    display = fault is not null ? $"(error: {fault})" : $"({count} rows)";
                }

                result.Add(new VariableInfo(entry.OriginalName, display, entry.Kind.ToString()));
            }
        }

        return result;
    }

    public async Task<ResultSetInfo?> GetTempRowsAsync(string physicalName, int page, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Touch();
            var pageSize = S.TempTablePageSize;
            var (rows, fault) = await S.GetTempObjectPageAsync(physicalName, page * pageSize, pageSize, ct).ConfigureAwait(false);
            if (fault is not null || rows is null)
            {
                return new ResultSetInfo(new[] { "error" }, new[] { new[] { fault ?? "no rows" } }, false);
            }

            return ToResultSetInfo(rows);
        }
        finally
        {
            _gate.Release();
        }
    }

    // DESIGN §24.4/§12.3 evaluate: REPL eval. Reads always; a write is refused by Core unless
    // AllowConsoleWrites (§24.1(3)) — the host does not need its own gate here.
    public async Task<string> EvaluateAsync(int? frameId, string expression, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Touch();
            var frame = ResolveFrameOrTop(frameId);
            var result = await S.EvaluateReplAsync(frame, expression, ct).ConfigureAwait(false);
            if (result.Outcome == Session.ReplOutcome.Refused)
            {
                return result.RefusalMessage ?? "console statement refused.";
            }

            return result.Rendered ?? string.Empty;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string> SetVariableAsync(int frameId, string name, string value, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Touch();
            var frame = S.Frames.FirstOrDefault(f => f.Ordinal == frameId)
                ?? throw new ArgumentException($"No frame with id {frameId}.");
            var result = await S.SetVariableAsync(frame, name, value, ct).ConfigureAwait(false);
            if (result.Outcome == Session.SetVariableOutcome.Refused)
            {
                return result.RefusalReason ?? "setVariable refused.";
            }

            var display = result.AppliedValue is null or DBNull ? "NULL" : result.AppliedValue.ToString() ?? "NULL";
            return result.Note is not null ? $"{display} ({result.Note})" : display;
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---- teardown ---------------------------------------------------------------

    // DESIGN §24.1(2)/§16: unconditional rollback unless BOTH commitMode:"commit" AND the
    // target's allowWrites:true (TargetResolver.CommitAuthorized). No modal — the double gate
    // IS the authorization on this surface.
    public async Task<SessionSummary> EndAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            Touch();
            var commitAuthorized = TargetResolver.CommitAuthorized(_args.CommitModeRequested, _target);
            var returnCode = S.Frames.Count > 0 ? S.Frames[0].ReturnCode : 0;
            var finalState = S.IsBroken ? "faulted" : S.IsCompleted ? "completed" : "torn-down";

            // A70 (§24.4): same OUTPUT-param projection the trace summary carries — read
            // best-effort BEFORE teardown (never blocks it; null on any failure).
            var outputParams = await CaptureOutputParamsAsync(default).ConfigureAwait(false);

            Func<Task<bool>>? decision = commitAuthorized ? () => Task.FromResult(true) : null;
            await _teardownAsync(decision).ConfigureAwait(false);
            Committed = commitAuthorized;

            return new SessionSummary(
                SessionId,
                returnCode,
                outputParams ?? new Dictionary<string, string>(),
                Array.Empty<string>(),
                finalState,
                commitAuthorized,
                _stepCount);
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---- trace (Mode A, §24.3/§24.8) --------------------------------------------

    // DESIGN §24.3/§24.8: drive to completion with the auto-stepper, capturing a per-statement
    // record to a JSONL file. Returns the summary + file path (the record is on disk, never
    // inlined — token cost). Teardown is unconditional rollback unless the commit gate holds.
    // A70: variableCapture "changed" (default) records only the variables whose rendered value
    // differs from the SAME frame's previous stop (a frame's first stop is a full baseline);
    // "full" records the complete snapshot every step (the pre-A70 shape). The diff is computed
    // client-side from the same full read either way — the choice changes the file, not the
    // per-step query cost.
    public async Task<(SessionSummary Summary, string FilePath, int Steps)> RunTraceAsync(
        string traceDir, string stepMode, bool captureTempRowCounts, string? variableCapture, CancellationToken ct)
    {
        // Case-insensitive, like stepMode (review LOW-4).
        var fullCapture =
            string.IsNullOrEmpty(variableCapture) || string.Equals(variableCapture, "changed", StringComparison.OrdinalIgnoreCase)
                ? false
                : string.Equals(variableCapture, "full", StringComparison.OrdinalIgnoreCase)
                    ? true
                    : throw new ArgumentException($"variableCapture must be 'changed' or 'full', not '{variableCapture}'.");

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(traceDir);
            var filePath = Path.Combine(traceDir, $"trace-{SessionId}.jsonl");
            var kind = string.Equals(stepMode, "into", StringComparison.OrdinalIgnoreCase) ? StepKind.Into : StepKind.Over;

            await using var writer = new StreamWriter(filePath, append: false);
            await writer.WriteLineAsync(TraceJson.Header(
                SessionId, Server, Database, Mode, stepMode, fullCapture ? "full" : "changed")).ConfigureAwait(false);

            var seq = 0;
            // A70: launch warnings lead the summary messages — Mode A has no entry stop, so this
            // is the only place the agent can learn e.g. that an OUTPUT param was NULL-seeded.
            var messages = new List<string>(S.LaunchWarnings);
            // A70: per-frame baseline for the "changed" diff, keyed by frame identity (a re-entered
            // frame is a new object, so recursion gets a fresh full baseline). Popped frames are
            // pruned each iteration so a deep step-into trace cannot accumulate dead baselines.
            var previousVars = new Dictionary<Frame, Dictionary<string, string>>();
            while (!S.IsCompleted)
            {
                Touch();   // M11 gate (MAJOR): a long trace stays fresh so the idle sweep can't dispose it mid-run
                if (S.AtImplicitReturn)
                {
                    var (_, retMsgs) = await S.StepAsync(StepKind.Over, ct).ConfigureAwait(false);
                    messages.AddRange(retMsgs);
                    DrainNotes();
                    continue;
                }

                var frame = S.TopFrame;
                var current = S.Current;
                IReadOnlyList<ResultSet> sets;
                IReadOnlyList<string> msgs;
                try
                {
                    (sets, msgs) = await S.StepAsync(kind, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                seq++;
                messages.AddRange(msgs);
                var notes = DrainNotes();

                Dictionary<string, string>? vars = null;
                Dictionary<string, string>? changed = null;
                Dictionary<string, string>? tempCounts = null;
                // Capture the frame's post-statement state, but never let an inspection read
                // abort the whole trace: a step that popped/faulted the frame (FrameCompleted,
                // FrameFaulted) can leave its state table gone. Record "unavailable" instead
                // (A70: as an ABSENT variables field — nulls are omitted from the line).
                if (!S.IsBroken && frame is not null && S.Frames.Any(f => ReferenceEquals(f, frame)))
                {
                    try
                    {
                        vars = await CaptureVariablesAsync(frame, ct).ConfigureAwait(false);
                        if (!fullCapture)
                        {
                            changed = DiffVariables(previousVars.TryGetValue(frame, out var prev) ? prev : null, vars);
                            previousVars[frame] = vars;
                            vars = null;   // the line carries the delta, not the snapshot
                        }

                        if (captureTempRowCounts)
                        {
                            tempCounts = await CaptureTempCountsAsync(frame, ct).ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex)
                    {
                        // Do NOT update previousVars — the next successful read diffs against the
                        // last good baseline, so no change is silently swallowed by the error step.
                        var failure = new Dictionary<string, string> { ["__capture_error"] = ex.Message };
                        vars = fullCapture ? failure : null;
                        changed = fullCapture ? null : failure;
                    }
                }

                if (previousVars.Count > S.Frames.Count)
                {
                    foreach (var dead in previousVars.Keys.Where(f => !S.Frames.Any(live => ReferenceEquals(live, f))).ToList())
                    {
                        previousVars.Remove(dead);
                    }
                }

                var err = CurrentError();
                await writer.WriteLineAsync(TraceJson.Step(
                    seq, frame?.Ordinal, frame?.Module.Display, current?.Span.StartLine ?? 0, current?.Text,
                    vars, changed, tempCounts, msgs, sets.Select(ToResultSetInfo).ToList(), err, notes)).ConfigureAwait(false);
            }

            var returnCode = S.Frames.Count > 0 ? S.Frames[0].ReturnCode : 0;
            var finalState = S.IsBroken ? "faulted" : S.IsCompleted ? "completed" : "incomplete";
            // M11 re-review (N2): only a trace that ran to completion may commit — a cancelled or
            // faulted partial trace rolls back (rule-7 spirit: an involuntary exit never commits
            // partial work), even under commitMode:"commit" + allowWrites.
            var commitAuthorized = S.IsCompleted && TargetResolver.CommitAuthorized(_args.CommitModeRequested, _target);

            // A70: summary carries the frame-0 OUTPUT params' FINAL values (procedure mode,
            // best-effort — absent when the state is unreadable) and the messages deduplicated
            // ("… (occurred N×)"); the per-step lines keep every message verbatim in place.
            var outputParams = await CaptureOutputParamsAsync(ct).ConfigureAwait(false);
            var dedupedMessages = DedupeMessages(messages);

            await writer.WriteLineAsync(TraceJson.Summary(
                returnCode, outputParams, dedupedMessages, finalState, commitAuthorized, seq)).ConfigureAwait(false);
            await writer.FlushAsync().ConfigureAwait(false);

            Func<Task<bool>>? decision = commitAuthorized ? () => Task.FromResult(true) : null;
            await _teardownAsync(decision).ConfigureAwait(false);
            Committed = commitAuthorized;

            var summary = new SessionSummary(
                SessionId, returnCode, outputParams ?? new Dictionary<string, string>(),
                dedupedMessages, finalState, commitAuthorized, seq);
            return (summary, filePath, seq);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Dictionary<string, string>> CaptureVariablesAsync(Frame frame, CancellationToken ct)
    {
        var snap = await S.GetStateSnapshotAsync(frame, ct).ConfigureAwait(false);
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
    private static Dictionary<string, string> DiffVariables(
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
    internal static IReadOnlyList<string> DedupeMessages(List<string> messages)
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

    // A70 (§24.8/§24.4): the frame-0 OUTPUT parameters' final values for the summary —
    // procedure mode only, best-effort (null when frame 0 is gone or its state unreadable;
    // a summary field must never fail the trace or block teardown). Read BEFORE teardown.
    private async Task<Dictionary<string, string>?> CaptureOutputParamsAsync(CancellationToken ct)
    {
        if (Mode != "procedure" || S.IsBroken || S.Frames.Count == 0)
        {
            return null;
        }

        var frameZero = S.Frames[0];
        var outputSlots = frameZero.Variables.All.Where(s => s.Declaration.IsOutputParameter).ToList();
        if (outputSlots.Count == 0)
        {
            return null;
        }

        try
        {
            var snap = await S.GetStateSnapshotAsync(frameZero, ct).ConfigureAwait(false);
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

    private async Task<Dictionary<string, string>> CaptureTempCountsAsync(Frame frame, CancellationToken ct)
    {
        var map = new Dictionary<string, string>();
        if (S.IsDoomed || S.IsBroken)
        {
            return map;
        }

        foreach (var entry in frame.TempObjects.All)
        {
            if (entry.IsDead || entry.Kind == TempObjectKind.Cursor)
            {
                continue;
            }

            var (count, fault) = await S.GetTempObjectRowCountAsync(entry.PhysicalName, ct).ConfigureAwait(false);
            map[entry.OriginalName] = fault is not null ? $"error: {fault}" : (count?.ToString() ?? "?");
        }

        return map;
    }

    // ---- stop-state construction ------------------------------------------------

    private StopState BuildStopStateReason(string reason, IReadOnlyList<string> output, IReadOnlyList<ResultSet> sets)
    {
        _lastStopReason = reason;
        return BuildStopState(reason, output, sets);
    }

    private StopState BuildStopState(
        string reason, IReadOnlyList<string> output, IReadOnlyList<ResultSet> sets, IReadOnlyList<string>? warnings = null)
    {
        var frames = new List<FrameInfo>();
        var allFrames = S.Frames;
        for (var i = allFrames.Count - 1; i >= 0; i--)   // innermost first, like DAP stackTrace
        {
            var f = allFrames[i];
            var current = f.Cursor.Current;
            var startLine = current?.Span.StartLine ?? 0;
            var startCol = current?.Span.StartColumn ?? 0;
            var (endLine, endCol) = StatementEnd(current);
            var batch = f.Module.IsScript && (S.BatchCount > 1 || S.CurrentBatchRepeat > 1)
                ? new BatchInfo(S.CurrentBatchIndex, S.BatchCount, S.CurrentBatchIteration, S.CurrentBatchRepeat)
                : null;
            frames.Add(new FrameInfo(
                f.Ordinal, f.Module.Display, startLine,
                new[] { startLine, startCol, endLine, endCol }, current?.Text, batch));
        }

        var state = S.IsBroken ? "faulted" : S.IsCompleted ? "completed" : "stopped";
        var transaction = new TransactionInfo(
            S.LastObservedTrancount, S.LastObservedXactState, S.IsDoomed, S.IsTransactionDetached, S.IsBroken);

        return new StopState(
            SessionId, state, reason, frames, CurrentError(), transaction,
            output, sets.Select(ToResultSetInfo).ToList(), S.AtImplicitReturn,
            warnings ?? Array.Empty<string>());
    }

    private StopState BuildCompleted(
        IReadOnlyList<string>? output = null, IReadOnlyList<ResultSet>? sets = null, IReadOnlyList<string>? warnings = null)
    {
        _lastStopReason = "completed";
        return BuildStopState("completed", output ?? Array.Empty<string>(), sets ?? Array.Empty<ResultSet>(), warnings);
    }

    private ErrorInfo? CurrentError()
    {
        var active = S.ActiveErrorContext?.Values;
        var error = active ?? S.LastStep.Error;
        if (error is null)
        {
            return null;
        }

        var disp = S.LastStep.Disposition;
        var routedTo = active is not null ? "catch"
            : disp == StepDisposition.FrameFaulted ? "terminal"
            : disp is StepDisposition.FaultAtSite or StepDisposition.DoomedTempPreflight ? "faultSite"
            : "unhandled";

        return new ErrorInfo(error.Number, error.Severity, error.State, error.Line, error.Procedure, error.Message, routedTo);
    }

    private ResultSetInfo ToResultSetInfo(ResultSet set)
    {
        var maxRows = S.MaxConsoleRows;
        var displayChars = S.DisplayValueChars;
        var rows = new List<IReadOnlyList<string>>();
        var truncated = false;
        for (var i = 0; i < set.Rows.Count; i++)
        {
            if (i >= maxRows)
            {
                truncated = true;
                break;
            }

            var row = set.Rows[i];
            rows.Add(row.Select(cell =>
            {
                var text = cell is null or DBNull ? "NULL" : cell.ToString() ?? "NULL";
                return text.Length > displayChars ? text[..displayChars] : text;
            }).ToList());
        }

        return new ResultSetInfo(set.Columns.ToList(), rows, truncated);
    }

    private Frame ResolveFrameOrTop(int? frameId)
    {
        if (frameId is { } id)
        {
            return S.Frames.FirstOrDefault(f => f.Ordinal == id)
                ?? throw new ArgumentException($"No frame with id {id}.");
        }

        return S.TopFrame ?? throw new InvalidOperationException("No active frame.");
    }

    private IReadOnlyList<string> DrainNotes()
    {
        // A56: only surface at logLevel:verbose. The MCP surface has no per-session logLevel
        // knob yet (v1) — drain unconditionally (so notes never leak into a later step) and
        // return them as warnings; a future arg can gate this like the adapter's Verbose.
        return S.DrainDiagnosticNotes();
    }

    private void Touch() => LastActivityUtc = DateTime.UtcNow;

    private static bool IsExceptionDisposition(StepDisposition d) => d is
        StepDisposition.FaultAtSite or StepDisposition.RoutedToCatch or
        StepDisposition.UnhandledContinued or StepDisposition.FrameFaulted or
        StepDisposition.DoomedTempPreflight;

    private bool ShouldStopForExceptionFilters(StepDisposition d) => d switch
    {
        StepDisposition.FaultAtSite => true,
        StepDisposition.FrameFaulted => true,
        StepDisposition.RoutedToCatch => _stopOnCaught,
        StepDisposition.UnhandledContinued => _stopOnUnhandled,
        StepDisposition.DoomedTempPreflight => true,
        _ => false,
    };

    // A51 (§13): full-statement end span from the byte-exact source slice.
    private static (int EndLine, int EndColumn) StatementEnd(StatementUnit? unit)
    {
        if (unit is null)
        {
            return (0, 0);
        }

        var text = unit.Text;
        var lastNewline = text.LastIndexOf('\n');
        if (lastNewline < 0)
        {
            return (unit.Span.StartLine, unit.Span.StartColumn + text.Length);
        }

        var newlineCount = text.Count(ch => ch == '\n');
        return (unit.Span.StartLine + newlineCount, text.Length - lastNewline);
    }

    public async ValueTask DisposeAsync()
    {
        // DESIGN §24.1(2)/§24.2: involuntary teardown (host shutdown, idle sweep, eviction)
        // ALWAYS rolls back — no commit path here, regardless of commitMode (CLAUDE.md rule 7).
        //
        // M11 gate (MAJOR): acquire the session gate FIRST. The connection is non-MARS (C20), so
        // disposing it while a tool call's StepAsync is in flight on the same connection is a
        // TOCTOU that can corrupt the rollback or crash. A voluntary idle session has a free gate
        // (acquired instantly); a long in-flight op holds it, so we wait for it to finish before
        // rolling back. Bounded so host shutdown can never hang on a runaway op — if the wait
        // times out we force the rollback anyway (a hung batch is already lost).
        var acquired = await _gate.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        try
        {
            await _teardownAsync(null).ConfigureAwait(false);
        }
        catch
        {
            // best-effort — an already-dead connection must not mask shutdown.
        }
        finally
        {
            if (acquired)
            {
                _gate.Release();
            }
        }

        // M11 re-review (N6): deliberately NOT disposing the gate. We never touch its
        // AvailableWaitHandle, so the SemaphoreSlim holds no unmanaged resource to release, and
        // disposing it could throw ObjectDisposedException into a tool call still queued on it
        // during a concurrent sweep (masking the real "session gone" error). Letting it be GC'd
        // is safe and avoids that edge.
    }
}
