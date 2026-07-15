using System.Text;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;
using TsqlDbg.Adapter.Inspection;
using TsqlDbg.Core.Execution;
using TsqlDbg.Core.Sessions;
using TsqlDbg.Core.State;
using TsqlDbg.Core.Targets;
using TsqlDbg.Core.Tracing;

namespace TsqlDbg.Adapter;

// DESIGN §3 (DAP host) + §4 (session init sequence) + §6 (next/continue) + §13 (plain
// breakpoints) + §12.1 (Locals scope) + §18 (DAP conformance table), M1 slice per
// DESIGN.md §22: "stopOnEntry, next/continue, breakpoints (plain)".
//
// M1 restructure (gate amendment A2, docs/archive/reviews/m0-gate-review-fable.md): §4's full
// step order is restored — the connection opens and the session initializes (parse,
// state table, BEGIN TRAN) DURING `launch`, before `initialized` is sent, so
// `setBreakpoints` has a live parsed session (StatementIndex) to bind against. The
// `launch` response is sent only after init succeeds; init failure fails the launch
// request with the error message. The connection now stays open across DAP round
// trips (LiveSession) until `disconnect` or the run completes on its own.
//
// Threading (DESIGN §3, A15; M5 I1/I2 —
// docs/archive/reviews/m5-inspection-design-notes-fable.md §2): the InspectionExecutor now
// owns the shared stepping gate, the epoch, and the single FIFO inspection lane.
// stackTrace/scopes/cached-variables answer from the last published immutable
// StopSnapshot without ever touching the gate; a variables request that needs a
// fresh value fill enqueues onto the executor (HandleVariablesRequestAsync) instead
// of blocking the protocol thread. Every resume (next/continue/stepIn/stepOut/goto)
// bumps the epoch FIRST, synchronously, before its background step task ever
// acquires the gate — see InspectionExecutor.BeginResume.
//
// Also out of M1 scope, deliberately: `Source` on stack frames (the `tsqldbg:` virtual
// document provider is explicitly M6 scope, §22) — frames report line/column only.
public sealed class TsqlDbgDebugSession : DebugAdapterBase
{
    // M4 (§12.1): "scope(frame, kind) -> children", deterministic per frame ordinal
    // (FrameStack.MaxDepth = 32, so these ranges never collide). M5 I3/I4 (design
    // note §2 I1): "the existing 1000/2000 bases grow a Temp Tables base and a
    // System base." Table ROWS references (unbounded — one per live temp object) are
    // minted dynamically per snapshot instead (StopSnapshot.GetOrMintRowsReference),
    // starting well above every fixed base.
    private const int LocalsVariablesReferenceBase = 1000;
    private const int ErrorContextVariablesReferenceBase = 2000;
    private const int SystemVariablesReferenceBase = 3000;
    private const int TempTablesVariablesReferenceBase = 4000;
    private const int DynamicReferenceFloor = 500_000; // StopSnapshot's minted rows references start here
    private const int LargeTempTableWarnThreshold = 100_000; // §12.2 "warn-once for tables > 100k rows"

    private static int LocalsRef(int frameOrdinal) => LocalsVariablesReferenceBase + frameOrdinal;
    private static int ErrorContextRef(int frameOrdinal) => ErrorContextVariablesReferenceBase + frameOrdinal;
    private static int SystemRef(int frameOrdinal) => SystemVariablesReferenceBase + frameOrdinal;
    private static int TempTablesRef(int frameOrdinal) => TempTablesVariablesReferenceBase + frameOrdinal;

    private readonly ITraceSink _trace;
    private readonly TaskCompletionSource _completion = new();
    private readonly InspectionExecutor _executor = new();

    private LaunchConfig? _launchConfig;
    private TargetEntry? _target;
    private LiveSession? _liveSession;
    // M7 (§16 commit-modal): set while a tsqldbg_commitConfirm round trip is
    // outstanding; the custom tsqldbg_commitDecision request handler resolves it.
    // Never more than one in flight — teardown happens exactly once per session.
    private TaskCompletionSource<bool>? _pendingCommitDecision;
    // M6 S2 (design note §3, A22): breakpoints are stored PER MODULE IDENTITY (§13
    // "stored per module identity", now normative for the plumbing too) rather than
    // one line-keyed dictionary bound to whichever module was on top at setBreakpoints
    // time. RunUntilAsync's fast path looks up the TOP frame's module here — applies
    // to every frame currently executing that module (recursion, p19) but never a
    // coincidental line-number match in an unrelated module.
    private readonly Dictionary<Core.Interpreter.ModuleIdentity, Dictionary<int, BreakpointState>> _breakpointsByModule = new();
    // Requests that couldn't map to a line yet (module not live and its blueprint
    // didn't resolve) — re-verified the moment that module's first frame pushes
    // (StepOnceLockedAsync, StepKind.Into). Whole-set-replacement applies here too:
    // a fresh setBreakpoints for a module clears its own pending list.
    private readonly Dictionary<Core.Interpreter.ModuleIdentity, List<PendingBreakpoint>> _pendingBreakpointsByModule = new();
    // M7 (§5.1/§5.2 sourceMap): a real file with no resolved module match YET — all
    // a real (non-tsqldbg:) file path had to key a pending request on, since (unlike
    // a tsqldbg: URI) it carries no schema.name. Promoted the moment some module's
    // blueprint fetch matches this exact path (ReverifyPendingBreakpointsAsync).
    private readonly Dictionary<string, List<PendingBreakpoint>> _pendingBreakpointsByFile = new(StringComparer.OrdinalIgnoreCase);
    private int _nextBreakpointId = 1;
    // M5 I4 (§12.2): "warn-once for tables > 100k rows" — keyed by physical name so a
    // table stays warned across stops within the session (re-warning every stop would
    // be noise, not a fidelity concern).
    private readonly HashSet<string> _warnedLargeTempTables = new(StringComparer.Ordinal);
    private Core.Interpreter.StatementUnit? _lastBreakpointStopUnit;

    // §10.6 exception filters (setExceptionBreakpoints). "all" forwards straight to
    // Session.BreakOnAllErrors (the two-phase FaultAtSite stop); "caught"/"unhandled"
    // are adapter-side gates consulted by the continue loop (Session has no filter
    // concept for them — every RoutedToCatch/UnhandledContinued still happens, the
    // filter only decides whether `continue` stops on it or keeps going).
    private bool _stopOnCaughtErrors;
    private bool _stopOnUnhandledErrors = true;

    // §10.5 pause: HandlePauseRequest cancels whichever step is currently in flight.
    // Not disposed explicitly (see StepOnceLockedAsync/ContinueLockedAsync) — a Cancel()
    // racing a step that already finished is harmless (nothing observes the token
    // anymore), and disposing here would risk an ObjectDisposedException race against
    // a concurrent HandlePauseRequest call that doesn't hold the executor's gate.
    private CancellationTokenSource? _pauseCts;

    // DESIGN §13: one verified breakpoint line's condition/hit-count state. Hit-count
    // semantics ratified at the M2->M3 gate (docs/archive/reviews/m2-gate-review-fable.md §4,
    // Ivan 2026-07-05) — see HitCountFilter. M6 (A22): Id is stable across a pending ->
    // verified transition so the later `breakpoint` (changed) event correlates with
    // the one VS Code already has (DAP matches by id when present).
    private sealed class BreakpointState
    {
        public required int? Id { get; init; }
        public required int Line { get; init; }
        public string? Condition { get; init; }
        public HitCountFilter? HitCountFilter { get; init; }
        public int HitCount { get; set; }
        // M6 G1 (§13, A23): present iff this is a logpoint — a breakpoint with BOTH a
        // logMessage and a stop outcome does not exist in DAP (logMessage replaces
        // stopping).
        public string? LogMessage { get; init; }
    }

    // M6 S2: one breakpoint request that couldn't map to a line yet — kept so a later
    // module resolution (its first frame push) can re-map and re-verify it.
    private sealed record PendingBreakpoint(int? Id, int RequestedLine, string? Condition, string? HitCondition, string? LogMessage);

    public TsqlDbgDebugSession(ITraceSink trace)
    {
        _trace = trace;
    }

    public Task Completion => _completion.Task;

    public void InitializeStreams(Stream input, Stream output)
    {
        InitializeProtocolClient(input, output);
        // M6 S1 (design note §3, A22): a custom DAP command must be registered with
        // the protocol client before it can be dispatched at all — unlike the
        // standard commands (which DebugAdapterBase pre-registers in its own
        // InitializeProtocolClient), an unregistered command name never reaches
        // HandleProtocolRequest; the message is silently dropped.
        Protocol.RegisterRequestType<TsqldbgSourceRequest, TsqldbgSourceArguments, TsqldbgSourceResponseBody>(
            HandleTsqldbgSourceRequestAsync);
        // M7 (§16 commit-modal): the extension's reply to a tsqldbg_commitConfirm
        // event (also a custom request — same registration shape as tsqldbg_source).
        Protocol.RegisterRequestType<TsqldbgCommitDecisionRequest, TsqldbgCommitDecisionArguments, TsqldbgCommitDecisionResponseBody>(
            HandleTsqldbgCommitDecisionRequestAsync);
    }

    protected override InitializeResponse HandleInitializeRequest(InitializeArguments arguments)
    {
        _trace.Event("dap.initialize", $"clientID={arguments.ClientID}");

        // DESIGN §18: M5 I5 adds `evaluate context:"hover"` — REPL (I6)/watch (I7)
        // contexts remain documented gaps until their own M5 checklist items land.
        // M5 I8 adds `setVariable`.
        return new InitializeResponse
        {
            SupportsConfigurationDoneRequest = true,
            SupportsConditionalBreakpoints = true,
            SupportsHitConditionalBreakpoints = true,
            SupportsGotoTargetsRequest = true,
            SupportsEvaluateForHovers = true,
            SupportsSetVariable = true,
            // M6 G1 (§13, A23): logpoints ride the per-module breakpoint store (S2).
            SupportsLogPoints = true,
            // §10.6: "all" (stop at fault site before routing, off by default — the
            // two-phase FaultAtSite/deferred-route split, §10.6 D9), "caught" (off),
            // "unhandled" (on — native-continuation faults and terminal faults alike).
            ExceptionBreakpointFilters = new List<ExceptionBreakpointsFilter>
            {
                new("all", "All T-SQL errors") { Default = false },
                new("caught", "Caught T-SQL errors (routed to CATCH)") { Default = false },
                new("unhandled", "Unhandled T-SQL errors") { Default = true },
            },
            SupportsExceptionInfoRequest = true,
            // §18 (S4): terminate maps onto teardown (§16 commit-modal flow when
            // armed); declared here so HandleTerminateRequest is dispatched at all.
            SupportsTerminateRequest = true,
            // §18 (S4): cancel is scoped to inspection work only (REPL/watch/hover/
            // temp-table pages, the §3 FIFO lane) — the step path stays pause-only.
            SupportsCancelRequest = true,
        };
    }

    protected override LaunchResponse HandleLaunchRequest(LaunchArguments arguments)
    {
        try
        {
            _launchConfig = LaunchConfig.Parse(arguments.ConfigurationProperties);
            _trace.Event("dap.launch", $"server={_launchConfig.Server} database={_launchConfig.Database} mode={_launchConfig.Mode}");

            // DESIGN §16 (A38): this DAP adapter is the INTERACTIVE surface, where
            // targets.json is OPTIONAL metadata, not a gate. Resolve/load best-effort for
            // the env hint + connection options; a missing file or an unlisted server is
            // NOT a refusal — the human consents via the extension's launch warning (A40)
            // and the commit modal (A39). The programmatic surface (a future MCP server)
            // re-enforces the default-deny allowlist at its own entry point.
            TargetEntry target;
            try
            {
                var targetsPath = TargetsPolicy.ResolvePath(
                    _launchConfig.TargetsFile, _launchConfig.WorkspaceFolder, Environment.GetEnvironmentVariable);
                var targetsFile = TargetsFile.Load(targetsPath);
                if (targetsFile.TryGet(_launchConfig.Server, out var entry))
                {
                    target = entry;
                    _trace.Event("dap.launch.policy_ok", $"server={_launchConfig.Server} env={target.Env}");
                }
                else
                {
                    target = new TargetEntry(_launchConfig.Server, "unknown", AllowWrites: false, Options: null);
                    _trace.Event("dap.launch.no_target_entry", $"server={_launchConfig.Server} (interactive: proceeding under informed consent, no allowlist metadata)");
                }
            }
            catch (TargetsPolicyException ex)
            {
                target = new TargetEntry(_launchConfig.Server, "unknown", AllowWrites: false, Options: null);
                _trace.Event("dap.launch.no_targets_file", $"server={_launchConfig.Server} ({ex.Message})");
            }
            _target = target;

            // DESIGN §16 (A39, option A): on the interactive surface commit is authorized by
            // the terminate modal alone — no allowWrites pre-gate. Rollback stays the
            // unconditional default for every other exit (CLAUDE.md safety rule 7).

            var options = BuildSessionOptions(_launchConfig);

            // DESIGN §16/§4 (A41): a SQL-auth password arrives ONLY via the
            // TSQLDBG_SQL_PASSWORD child-process env var (set by the extension from
            // SecretStorage) — never a DAP launch arg, never traced. Read once, threaded
            // transiently into the connection build, never stored. Integrated auth = no password.
            string? sqlPassword = null;
            if (_launchConfig.AuthType == Core.Sessions.AuthType.Sql)
            {
                sqlPassword = Environment.GetEnvironmentVariable("TSQLDBG_SQL_PASSWORD");
                if (string.IsNullOrEmpty(sqlPassword))
                {
                    throw new InvalidOperationException(
                        "launch config authType='sql' requires a password via the " +
                        "TSQLDBG_SQL_PASSWORD environment variable (set by the extension from " +
                        "SecretStorage) — none was provided.");
                }
            }

            // Gate amendment A2: session init (open + parse + state table + BEGIN
            // TRAN) happens now, synchronously from the client's point of view — the
            // `launch` response only comes back once this succeeds or fails. Blocking
            // the dispatch thread here (rather than switching to the async responder
            // pattern) is a deliberate, documented simplification: launch is a
            // one-shot, bounded sequence of a handful of round trips, not the
            // long-running `continue`/`next` work §3's threading model is about.
            _liveSession = LiveSession.OpenAsync(options, target, _trace, password: sqlPassword).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _trace.Event("dap.launch.failed", ex.ToString());
            throw new ProtocolException(BuildLaunchErrorMessage(ex));
        }

        // §10.4: session-start policy notes (COMMIT statements found on a rollback-mode
        // session) surface to the Debug Console at launch, before any stepping starts.
        // These are user-facing warnings — always shown, never gated (A56).
        foreach (var warning in _liveSession.Session.LaunchWarnings)
        {
            Protocol.SendEvent(new OutputEvent($"{warning}\n") { Category = OutputEvent.CategoryValue.Console });
        }

        // A56: flush any launch-time diagnostic annotation (the init NOCOUNT-forced note),
        // gated on logLevel — draining unconditionally so it never trails into the first step.
        EmitDiagnosticNotes();

        // DESIGN §4 step 6: sent only after steps 1-5 (now including the real
        // connection/parse/state-table/BEGIN TRAN sequence) succeed.
        SendEvent(new InitializedEvent(), "initialized");

        return new LaunchResponse();
    }

    // §5.4/§20.3 (A47): the Core refuses a script that doesn't parse
    // (Session.ResolveBatchBlueprintsAsync) as a clean InvalidOperationException whose
    // message is ALREADY the actionable diagnostic — it asks the live server under
    // SET PARSEONLY ON and leads with the real native error (e.g. Msg 111,
    // "'CREATE/ALTER PROCEDURE' must be the first statement in a query batch"), falling
    // back to ScriptDom's own parse error otherwise. VS Code shows this message verbatim
    // on the failed launch, so the adapter just forwards it. (The pre-A47 46010->"GO N"
    // headline was an over-broad match — 46010 is ScriptDom's GENERIC "Incorrect syntax
    // near X", raised for EVERY syntax error, not only a repeat count — so it mislabeled
    // e.g. a CREATE-PROC-not-first script as a GO problem; removed.)
    private static string BuildLaunchErrorMessage(Exception ex) => ex.Message;

    protected override SetExceptionBreakpointsResponse HandleSetExceptionBreakpointsRequest(SetExceptionBreakpointsArguments arguments)
    {
        _trace.Event("dap.request", $"setExceptionBreakpoints filters=[{string.Join(",", arguments.Filters ?? new List<string>())}]");
        var filters = arguments.Filters ?? new List<string>();
        if (_liveSession is not null)
        {
            _liveSession.Session.BreakOnAllErrors = filters.Contains("all");
        }

        _stopOnCaughtErrors = filters.Contains("caught");
        _stopOnUnhandledErrors = filters.Contains("unhandled");
        return new SetExceptionBreakpointsResponse();
    }

    protected override ExceptionInfoResponse HandleExceptionInfoRequest(ExceptionInfoArguments arguments)
    {
        _trace.Event("dap.request", "exceptionInfo");
        _executor.Gate.Wait();
        try
        {
            // §10.6: exceptionInfo reads whichever error is "active" right now — inside
            // a CATCH that's the ErrorContextStack top (§10.2); otherwise (a FaultAtSite
            // stop, or a terminal FrameFaulted with no CATCH involved) it's the fault
            // LastStep just carried.
            var error = _liveSession?.Session.ActiveErrorContext?.Values ?? _liveSession?.Session.LastStep.Error;
            if (error is null)
            {
                return new ExceptionInfoResponse("0", ExceptionBreakMode.Never);
            }

            var description = $"Msg {error.Number}, Level {error.Severity}, State {error.State}" +
                               (error.Procedure is { } proc ? $", Procedure {proc}" : string.Empty) +
                               (error.Line is { } line ? $", Line {line}" : string.Empty);
            return new ExceptionInfoResponse(error.Number.ToString(), ExceptionBreakMode.Always)
            {
                Description = description,
                Details = new ExceptionDetails { Message = error.Message, FullTypeName = description },
            };
        }
        finally
        {
            _executor.Gate.Release();
        }
    }

    protected override PauseResponse HandlePauseRequest(PauseArguments arguments)
    {
        _trace.Event("dap.request", "pause");
        // §10.5: cancels whichever StepAsync is currently in flight (next/continue run
        // on a background Task holding the executor's gate — this handler must NOT
        // wait on it, or a pause request could never be delivered while a step is
        // running). The
        // resulting OperationCanceledException is caught in StepOnceLockedAsync/
        // ContinueLockedAsync, which publish `stopped reason:pause` themselves.
        //
        // Cancel() runs OFF the protocol thread (A15): CancellationTokenSource.Cancel
        // executes registered callbacks synchronously on the caller, and SqlClient's
        // registration (SqlCommand.Cancel) blocks until the attention lands — on the
        // async driver path that is up to the in-flight batch's REMAINING RUNTIME
        // (fact 30). Inline, that froze the protocol thread for the duration of a
        // long boosted batch: no pause response, no further requests
        // (m6-boosted-attention triage, anomaly 2).
        var cts = _pauseCts;
        _ = Task.Run(() => cts?.Cancel());
        return new PauseResponse();
    }

    // §18/§5.4 (S4): scope = INSPECTION work ONLY (REPL/watch/hover/temp-table page
    // fetches -- the §3 FIFO lane owned by InspectionExecutor); the step path is
    // `pause`'s territory, never this request's. See InspectionExecutor.CancelPending's
    // remarks for why per-requestId selectivity isn't achievable against this SDK
    // (best-effort cancels everything currently pending in the lane instead -- DAP
    // itself documents `cancel` as best-effort). Always returns success: an unknown,
    // already-completed, or not-cancellable id is conformant success-no-op per DAP.
    protected override CancelResponse HandleCancelRequest(CancelArguments arguments)
    {
        _trace.Event("dap.request", $"cancel requestId={arguments.RequestId} progressId={arguments.ProgressId}");
        _executor.CancelPending();
        return new CancelResponse();
    }

    // M6 S2 (design note §3, A22): the *Async responder variant, because resolving a
    // module not yet on the call stack may need a real OBJECT_DEFINITION round trip
    // (TryGetModuleIndexAsync) — the plain synchronous override can't await that.
    protected override void HandleSetBreakpointsRequestAsync(
        IRequestResponder<SetBreakpointsArguments, SetBreakpointsResponse> responder)
    {
        var requested = responder.Arguments.Breakpoints ?? new List<SourceBreakpoint>();
        _trace.Event("dap.request", $"setBreakpoints count={requested.Count}");
        if (_liveSession is null)
        {
            // Can't happen with a spec-compliant client (setBreakpoints always
            // follows a successful launch+initialized), but fail honestly rather
            // than NullReferenceException if it does.
            responder.SetResponse(new SetBreakpointsResponse(
                requested.Select(_ => new Breakpoint { Verified = false, Message = "No active session." }).ToList()));
            return;
        }

        _ = SetBreakpointsLockedAsync(responder, responder.Arguments.Source, requested);
    }

    // DESIGN §13: "map each line to the first SU whose OriginalStartLine >= line ...
    // Respond verified:true with the actual line ... unmappable -> verified:false +
    // message." Conditional/hit-count state resets on every setBreakpoints call — DAP
    // replaces the full breakpoint set for a source each time it's sent (this is also
    // how the ratified hit-count ruling's "counters reset on setBreakpoints
    // replacement" clause is satisfied). M6 S2/A22: the set now replaces exactly one
    // MODULE's entries (_breakpointsByModule[identity]), not a single global
    // dictionary — a module that can't resolve yet (not live, blueprint didn't fetch)
    // stores its requests as pending instead, re-verified the moment that module's
    // first frame pushes (ReverifyPendingBreakpointsAsync).
    //
    // M2-gate follow-up §5.2: two requested lines can map FORWARD to the same SU (e.g.
    // both landing on a blank/comment line's next statement) — the dictionary is keyed
    // by mapped line, so the later request's condition/hitCondition silently wins
    // while both still report verified:true. Documented as intentional last-wins
    // rather than merged: rare in practice, and VS Code's own UI never asks a user to
    // set two breakpoints on the same source line in the first place.
    private async Task SetBreakpointsLockedAsync(
        IRequestResponder<SetBreakpointsArguments, SetBreakpointsResponse> responder,
        Source? source, List<SourceBreakpoint> requested)
    {
        await _executor.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var (identity, sourceError, pendingFilePath) = ResolveModuleIdentity(source);
            if (identity is null && pendingFilePath is null)
            {
                responder.SetResponse(new SetBreakpointsResponse(
                    requested.Select(_ => new Breakpoint { Verified = false, Message = sourceError }).ToList()));
                return;
            }

            if (identity is null)
            {
                // M7 (§5.1/§5.2): a real file with no resolved module match YET —
                // track pending-by-file; ReverifyPendingBreakpointsAsync promotes it
                // the moment some module's blueprint fetch matches this exact path.
                var pendingList = new List<PendingBreakpoint>();
                var pendingResults = new List<Breakpoint>();
                foreach (var r in requested)
                {
                    var id = _nextBreakpointId++;
                    pendingResults.Add(new Breakpoint
                    {
                        Id = id,
                        Verified = false,
                        Message = "Not yet matched to a resolved module (sourceMap) — will bind once a stepped-into module's source matches this file.",
                        Source = source,
                    });
                    pendingList.Add(new PendingBreakpoint(id, r.Line, r.Condition, r.HitCondition, r.LogMessage));
                }

                _pendingBreakpointsByFile[pendingFilePath!] = pendingList;
                responder.SetResponse(new SetBreakpointsResponse(pendingResults));
                return;
            }

            var (index, message) = await _liveSession!.Session.TryGetModuleIndexAsync(identity).ConfigureAwait(false);
            var results = new List<Breakpoint>();
            if (index is not null)
            {
                var mapped = new Dictionary<int, BreakpointState>();
                foreach (var r in requested)
                {
                    var id = _nextBreakpointId++;
                    // M8 (§5.4/A36): in script mode a requested line may belong to ANY
                    // batch, not just the one currently live — TryGetModuleIndexAsync
                    // above only ever hands back the ACTIVE batch's own StatementIndex
                    // (all batch frames share ModuleIdentity.Script(), so it can only
                    // resolve to whichever one is currently on the stack). Session.
                    // TryMapScriptBreakpointLine scans every batch's blueprint (all
                    // resolved at launch) instead, so a breakpoint in a not-yet-active
                    // batch verifies immediately — no pending-until-load path. Every
                    // other module (procedure frames, tsqldbg: virtual docs) keeps the
                    // single-index mapping unchanged.
                    var isMapped = identity.IsScript
                        ? _liveSession.Session.TryMapScriptBreakpointLine(r.Line, out _, out var unit)
                        : index.TryMapBreakpointLine(r.Line, out unit);
                    if (isMapped)
                    {
                        results.Add(new Breakpoint { Id = id, Verified = true, Line = unit.Span.StartLine, Source = source });
                        var hitCountFilter = HitCountFilter.Parse(r.HitCondition);
                        if (hitCountFilter is { InvalidText: { } invalidText })
                        {
                            Protocol.SendEvent(new OutputEvent(
                                $"Breakpoint at line {unit.Span.StartLine}: could not parse hitCondition '{invalidText}' — " +
                                "breaking on every hit instead (never silently past a breakpoint).\n")
                            {
                                Category = OutputEvent.CategoryValue.Stderr,
                            });
                        }

                        mapped[unit.Span.StartLine] = new BreakpointState
                        {
                            Id = id,
                            Line = unit.Span.StartLine,
                            Condition = string.IsNullOrWhiteSpace(r.Condition) ? null : r.Condition,
                            HitCountFilter = hitCountFilter,
                            LogMessage = string.IsNullOrEmpty(r.LogMessage) ? null : r.LogMessage,
                        };
                    }
                    else
                    {
                        results.Add(new Breakpoint { Id = id, Verified = false, Message = "Past the last statement in this frame.", Source = source });
                    }
                }

                _breakpointsByModule[identity] = mapped;
                _pendingBreakpointsByModule.Remove(identity);
            }
            else
            {
                var pending = new List<PendingBreakpoint>();
                foreach (var r in requested)
                {
                    var id = _nextBreakpointId++;
                    results.Add(new Breakpoint { Id = id, Verified = false, Message = message, Source = source });
                    pending.Add(new PendingBreakpoint(id, r.Line, r.Condition, r.HitCondition, r.LogMessage));
                }

                _breakpointsByModule.Remove(identity);
                _pendingBreakpointsByModule[identity] = pending;
            }

            responder.SetResponse(new SetBreakpointsResponse(results));
        }
        finally
        {
            _executor.Gate.Release();
        }
    }

    // M6 S2 (design note §3): source.path with the tsqldbg: scheme parses to
    // {schema}.{name}, server/database sanity-checked against this session (a
    // breakpoint left over from a DIFFERENT target's virtual doc is a permanent
    // mismatch, not a pending one).
    // M7 (§5.1/§5.2 sourceMap hash-compare): a real file first checks the reverse
    // lookup — has SOME module's blueprint fetch already matched THIS exact file
    // (a prior step-into, or a proactive TryGetModuleIndexAsync resolution)?
    // Script mode's own frame-0 file is never ambiguous (sourceMap comparison
    // never applies to it — it IS the ground truth already). A procedure-mode
    // real file that hasn't matched anything YET has no name to key a proactive
    // fetch on (unlike a tsqldbg: URI) — it comes back as PendingFilePath, tracked
    // like S2's per-module pending list and promoted the moment some module's
    // fetch resolves a match for that path (ReverifyPendingBreakpointsAsync).
    private (Core.Interpreter.ModuleIdentity? Identity, string? Error, string? PendingFilePath) ResolveModuleIdentity(Source? source)
    {
        if (_liveSession is null)
        {
            return (null, "No active session.", null);
        }

        var path = source?.Path;
        if (!string.IsNullOrEmpty(path) && path.StartsWith(VirtualDocScheme, StringComparison.OrdinalIgnoreCase))
        {
            if (!TryParseVirtualDocPath(path, out var server, out var database, out var schema, out var name))
            {
                return (null, $"Malformed {VirtualDocScheme} source path.", null);
            }

            if (!string.Equals(server, _launchConfig!.Server, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(database, _launchConfig.Database, StringComparison.OrdinalIgnoreCase))
            {
                return (null, $"'{schema}.{name}' belongs to a different session ({server}/{database}).", null);
            }

            return (ModuleIdentityFromVirtualDoc(database, schema, name), null, null);
        }

        if (!string.IsNullOrEmpty(path) && _liveSession.Session.TryResolveModuleBySourceFile(path, out var matchedModule))
        {
            return (matchedModule, null, null);
        }

        var root = _liveSession.Session.Frames.Count > 0 ? _liveSession.Session.Frames[0] : null;
        if (root is null)
        {
            return (null, "No active session.", null);
        }

        if (root.Module.IsScript || string.IsNullOrEmpty(path))
        {
            return (root.Module, null, null);
        }

        return (null, null, path);
    }

    // M6 S1/S2 (design note §3, A22): the one URI contract shared by three call
    // sites — the extension's TextDocumentContentProvider (which just round-trips
    // whatever Source.Path it was given), setBreakpoints source resolution, and the
    // tsqldbg_source custom request. Shape: tsqldbg:/{server}/{database}/{schema}.{name}.sql
    private const string VirtualDocScheme = "tsqldbg:";

    private static bool TryParseVirtualDocPath(string path, out string server, out string database, out string schema, out string name)
    {
        server = database = schema = name = "";
        var rest = path.Substring(VirtualDocScheme.Length).TrimStart('/');
        var segments = rest.Split('/');
        if (segments.Length != 3)
        {
            return false;
        }

        var fileName = Uri.UnescapeDataString(segments[2]);
        if (!fileName.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var stem = fileName[..^4];
        var dot = stem.IndexOf('.');
        if (dot < 0)
        {
            return false;
        }

        server = Uri.UnescapeDataString(segments[0]);
        database = Uri.UnescapeDataString(segments[1]);
        schema = stem[..dot];
        name = stem[(dot + 1)..];
        return true;
    }

    // A58 (§11.6): the reserved `__dyn` schema token marks a dynamic-SQL document. The identity
    // must be reconstructed WITH its IsDynamic flag — ModuleIdentity equality includes it, and both
    // the live-frame lookup and the session's dynamic-text retention map key on it. A plain
    // 4-arg construction here would silently miss both and serve an empty document.
    private static Core.Interpreter.ModuleIdentity ModuleIdentityFromVirtualDoc(
        string database, string schema, string name)
        => string.Equals(schema, Core.Interpreter.ModuleIdentity.DynamicSchema, StringComparison.Ordinal)
            ? Core.Interpreter.ModuleIdentity.Dynamic(database, name)
            : new Core.Interpreter.ModuleIdentity(database, schema, name, IsScript: false);

    private string BuildVirtualDocPath(Core.Interpreter.ModuleIdentity module)
        => $"{VirtualDocScheme}/{Uri.EscapeDataString(_launchConfig!.Server)}/{Uri.EscapeDataString(_launchConfig.Database)}/" +
           $"{Uri.EscapeDataString(module.Schema ?? "dbo")}.{Uri.EscapeDataString(module.Name)}.sql";

    // M6 S1 (design note §3, A22): the extension's TextDocumentContentProvider's only
    // data source — byte-exact server text (StatementIndex.FullScript, never
    // regenerated: §5.3/§5.2's line-1-ground-truth contract), refused with a message
    // on an unresolvable module or broken session. The async responder variant (like
    // HandleVariablesRequestAsync) lets the fetch genuinely await the OBJECT_DEFINITION
    // round trip instead of blocking a thread.
    private void HandleTsqldbgSourceRequestAsync(IRequestResponder<TsqldbgSourceArguments, TsqldbgSourceResponseBody> responder)
    {
        _ = ResolveTsqldbgSourceAsync(responder);
    }

    private async Task ResolveTsqldbgSourceAsync(IRequestResponder<TsqldbgSourceArguments, TsqldbgSourceResponseBody> responder)
    {
        var path = responder.Arguments.Path;
        if (string.IsNullOrEmpty(path) || !TryParseVirtualDocPath(path, out var server, out var database, out var schema, out var name))
        {
            responder.SetResponse(new TsqldbgSourceResponseBody { Content = $"-- tsqldbg: could not parse source path '{path}'." });
            return;
        }

        if (_liveSession is null)
        {
            responder.SetResponse(new TsqldbgSourceResponseBody { Content = "-- tsqldbg: no active session." });
            return;
        }

        if (!string.Equals(server, _launchConfig!.Server, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(database, _launchConfig.Database, StringComparison.OrdinalIgnoreCase))
        {
            responder.SetResponse(new TsqldbgSourceResponseBody { Content = $"-- tsqldbg: '{schema}.{name}' belongs to a different session ({server}/{database})." });
            return;
        }

        var identity = ModuleIdentityFromVirtualDoc(database, schema, name);
        await _executor.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var (index, message) = await _liveSession.Session.TryGetModuleIndexAsync(identity).ConfigureAwait(false);
            responder.SetResponse(new TsqldbgSourceResponseBody { Content = index?.FullScript ?? $"-- tsqldbg: {message}" });
        }
        finally
        {
            _executor.Gate.Release();
        }
    }

    protected override GotoTargetsResponse HandleGotoTargetsRequest(GotoTargetsArguments arguments)
    {
        _trace.Event("dap.request", $"gotoTargets line={arguments.Line}");
        _executor.Gate.Wait();
        try
        {
            if (_liveSession is null || !_liveSession.Session.Index!.TryMapBreakpointLine(arguments.Line, out var unit))
            {
                return new GotoTargetsResponse(new List<GotoTarget>());
            }

            // DESIGN §13: one target per line, id = the unit's stable ordinal.
            return new GotoTargetsResponse(new List<GotoTarget>
            {
                new(unit.Ordinal, $"{unit.Kind}/{unit.SubKind}", unit.Span.StartLine) { Column = unit.Span.StartColumn },
            });
        }
        finally
        {
            _executor.Gate.Release();
        }
    }

    // M5 I1/I2 (A15): goto is a resume — the epoch bumps synchronously, on the
    // protocol thread, BEFORE the gate is acquired, exactly like next/continue.
    protected override GotoResponse HandleGotoRequest(GotoArguments arguments)
    {
        _trace.Event("dap.request", $"goto targetId={arguments.TargetId}");
        var epoch = _executor.BeginResume();
        _executor.Gate.Wait();
        try
        {
            if (_liveSession is null)
            {
                throw new ProtocolException("No active session.");
            }

            var target = _liveSession.Session.Index!.All.FirstOrDefault(u => u.Ordinal == arguments.TargetId)
                ?? throw new ProtocolException($"No such goto target id {arguments.TargetId}.");

            // DESIGN §13: "it moves the cursor without executing the skipped code
            // (document loudly: state does not change)."
            _liveSession.Session.JumpTo(target);
            Protocol.SendEvent(new OutputEvent(
                $"Jumped to line {target.Span.StartLine} — state does not change; any skipped statements did not execute.\n")
            {
                Category = OutputEvent.CategoryValue.Console,
            });
            PublishSnapshotAndStop(epoch, StoppedEvent.ReasonValue.Goto);
            return new GotoResponse();
        }
        finally
        {
            _executor.Gate.Release();
        }
    }

    protected override ConfigurationDoneResponse HandleConfigurationDoneRequest(ConfigurationDoneArguments arguments)
    {
        _trace.Event("dap.configurationDone", string.Empty);

        var stopOnEntry = _launchConfig?.StopOnEntry ?? true;
        // M5 I1/I2 (A15): epoch bumps synchronously, right here, before the
        // background task ever waits on the gate — a `continue` from
        // configurationDone is a resume like any other. Entry is not a resume (no
        // step runs), so it publishes at whatever the current epoch already is.
        var epoch = stopOnEntry ? _executor.Epoch : _executor.BeginResume();
        _ = Task.Run(async () =>
        {
            await _executor.Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_liveSession is null)
                {
                    return;
                }

                if (_liveSession.Session.IsCompleted)
                {
                    await EndSessionLockedAsync().ConfigureAwait(false);
                }
                else if (stopOnEntry)
                {
                    // DESIGN §6: "Current before any Advance is the first statement" —
                    // InitializeAsync already positioned the cursor there; entry is a
                    // pure publish, no step.
                    PublishSnapshotAndStop(epoch, StoppedEvent.ReasonValue.Entry);
                }
                else
                {
                    await ContinueLockedAsync(epoch).ConfigureAwait(false);
                }
            }
            finally
            {
                _executor.Gate.Release();
            }
        });

        return new ConfigurationDoneResponse();
    }

    protected override NextResponse HandleNextRequest(NextArguments arguments)
    {
        _trace.Event("dap.request", "next");
        var epoch = _executor.BeginResume();
        _ = Task.Run(async () =>
        {
            await _executor.Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await StepOnceLockedAsync(epoch, StoppedEvent.ReasonValue.Step).ConfigureAwait(false);
            }
            finally
            {
                _executor.Gate.Release();
            }
        });

        return new NextResponse();
    }

    protected override ContinueResponse HandleContinueRequest(ContinueArguments arguments)
    {
        _trace.Event("dap.request", "continue");
        var epoch = _executor.BeginResume();
        _ = Task.Run(async () =>
        {
            await _executor.Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await ContinueLockedAsync(epoch).ConfigureAwait(false);
            }
            finally
            {
                _executor.Gate.Release();
            }
        });

        return new ContinueResponse { AllThreadsContinued = true };
    }

    protected override StepInResponse HandleStepInRequest(StepInArguments arguments)
    {
        _trace.Event("dap.request", "stepIn");
        var epoch = _executor.BeginResume();
        _ = Task.Run(async () =>
        {
            await _executor.Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                // M4 (§11.1/§6): Session.StepAsync(Into) itself falls back to plain
                // stepping when Current isn't an eligible EXEC (C8/C9/C10 shapes) — the
                // adapter never needs its own eligibility pre-check.
                await StepOnceLockedAsync(epoch, StoppedEvent.ReasonValue.Step, StepKind.Into).ConfigureAwait(false);
            }
            finally
            {
                _executor.Gate.Release();
            }
        });

        return new StepInResponse();
    }

    protected override StepOutResponse HandleStepOutRequest(StepOutArguments arguments)
    {
        _trace.Event("dap.request", "stepOut");
        var epoch = _executor.BeginResume();
        _ = Task.Run(async () =>
        {
            await _executor.Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await StepOutLockedAsync(epoch).ConfigureAwait(false);
            }
            finally
            {
                _executor.Gate.Release();
            }
        });

        return new StepOutResponse();
    }

    protected override ThreadsResponse HandleThreadsRequest(ThreadsArguments arguments)
    {
        // DESIGN §18: "threads | single thread, id 1, name "T-SQL session"".
        return new ThreadsResponse(new List<Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages.Thread>
        {
            new(1, "T-SQL session"),
        });
    }

    protected override StackTraceResponse HandleStackTraceRequest(StackTraceArguments arguments)
    {
        _trace.Event("dap.request", "stackTrace");
        // M5 I1/I2 (A15): answered from the last published StopSnapshot — no gate,
        // no round trip. A request racing an in-flight resume gets the DAP
        // not-stopped error rather than a stale or mid-step read.
        if (_executor.IsRunning)
        {
            throw new ProtocolException("Not stopped.");
        }

        var snapshot = _executor.CurrentSnapshot;
        if (snapshot is null || snapshot.Frames.Count == 0)
        {
            return new StackTraceResponse(new List<StackFrame>()) { TotalFrames = 0 };
        }

        // DESIGN §18: "stackTrace | virtual frames: name `dbo.Proc (line N)` ...".
        // M4 (§11): Session.Frames is bottom (root) -> top; DAP wants innermost
        // (currently executing) first, id = the frame's own stable ordinal (unique
        // for the session's lifetime, §11.4). M6 (§13/A22): every frame now carries a
        // Source (BuildFrameSource) — real file for script-mode frame 0, else the
        // tsqldbg: virtual document.
        var frames = snapshot.Frames
            .Reverse()
            .Select(f => new StackFrame(f.Ordinal, BuildFrameName(f), f.Line, f.Column)
            {
                Source = BuildFrameSource(f),
                // A51 (§13): report the full statement span so VS Code boxes the whole
                // statement about to execute, not just its first line. Guarded: a
                // null-cursor frame (EndLine 0) leaves the range unset (line-only).
                EndLine = f.EndLine > 0 ? f.EndLine : (int?)null,
                EndColumn = f.EndLine > 0 ? f.EndColumn : (int?)null,
            })
            .ToList();
        return new StackTraceResponse(frames) { TotalFrames = frames.Count };
    }

    // DESIGN §18 / M8 (§5.4): a multi-batch script's bottom batch frame is named
    // "<script> [batch k/N] (line L)" for orientation across GO boundaries; §5.4/A43 adds a
    // "×i/M" suffix while a `GO N` batch iterates. Single-batch, non-repeated scripts,
    // procedure frames, and every stepped-into callee keep "<display> (line L)" (callees are
    // never IsScript; BatchCount is 1 and BatchRepeat is 1 outside a multi-batch/repeated script).
    private static string BuildFrameName(SnapshotFrame f)
    {
        if (!f.Module.IsScript || (f.BatchCount <= 1 && f.BatchRepeat <= 1))
        {
            return $"{f.ModuleDisplay} (line {f.Line})";
        }

        var iteration = f.BatchRepeat > 1 ? $" ×{f.BatchIteration}/{f.BatchRepeat}" : string.Empty;
        return $"{f.ModuleDisplay} [batch {f.BatchIndex + 1}/{f.BatchCount}{iteration}] (line {f.Line})";
    }

    // M6 S3 (design note §3, A22): script-mode frame 0 is the one real workspace
    // file (the debugged .sql itself); every other frame — procedure-mode frame 0,
    // and any stepped-into callee — is the read-only tsqldbg: virtual document,
    // path-based with NO sourceReference so VS Code's persisted breakpoints re-bind
    // across sessions (S1). M7 (§5.1/§5.2): UNLESS that module's server definition
    // byte-matched a real sourceMap file — then that file's real Source rides
    // instead, so breakpoints in it (ResolveModuleIdentity) line up with what the
    // user is actually looking at.
    private Source? BuildFrameSource(SnapshotFrame frame)
    {
        if (_launchConfig is null)
        {
            return null;
        }

        // M8 (§5.4): every script batch frame (not just ordinal 0) is the one real
        // workspace .sql file — batches ≥ 1 carry a fresh non-zero ordinal but share
        // ModuleIdentity.Script(), so gate on IsScript, not Ordinal == 0.
        if (frame.Module.IsScript && _launchConfig.Mode == LaunchMode.Script)
        {
            var scriptPath = _launchConfig.ScriptPath;
            return scriptPath is null ? null : new Source { Name = FrameSourceName(scriptPath), Path = scriptPath };
        }

        if (_liveSession is not null && _liveSession.Session.TryGetSourceMapFile(frame.Module, out var file) && file is not null)
        {
            return new Source { Name = Path.GetFileName(file), Path = file };
        }

        return new Source { Name = frame.Module.Display, Path = BuildVirtualDocPath(frame.Module) };
    }

    // A60: the script frame's Source.Path may be a real filesystem path OR an unsaved buffer's
    // URI (e.g. "untitled:Untitled-1"). Path.GetFileName leaves a scheme-prefixed URI intact
    // (no directory separator to split on), so derive a readable tab name from the URI's last
    // segment instead; a plain path stays exactly as before.
    private static string FrameSourceName(string scriptPath)
    {
        if (Uri.TryCreate(scriptPath, UriKind.Absolute, out var uri) && !uri.IsFile && uri.Scheme != "tsqldbg")
        {
            var tail = scriptPath.Split('/', '\\').Last();
            return tail.Length > 0 ? tail : scriptPath;
        }

        return Path.GetFileName(scriptPath);
    }

    protected override ScopesResponse HandleScopesRequest(ScopesArguments arguments)
    {
        _trace.Event("dap.request", $"scopes frameId={arguments.FrameId}");
        if (_executor.IsRunning)
        {
            throw new ProtocolException("Not stopped.");
        }

        var snapshot = _executor.CurrentSnapshot;
        var frame = snapshot?.Frames.FirstOrDefault(f => f.Ordinal == arguments.FrameId);
        if (snapshot is null || frame is null)
        {
            return new ScopesResponse(new List<Scope>());
        }

        var locals = new Scope("Locals", LocalsRef(frame.Ordinal), expensive: false)
        {
            NamedVariables = frame.Variables.Count,
        };
        var scopes = new List<Scope> { locals };

        // §12.1/§10.6: Error Context scope, only on the TOP (currently executing)
        // frame while inside an active CATCH — the ErrorContextStack's top entry
        // reflects wherever execution is right now (dynamic extent, §10.2), not
        // necessarily the frame that raised it. DAP invalidates variablesReferences
        // on every resume, so there's no stale-scope risk from always re-deriving.
        var topOrdinal = snapshot.Frames[^1].Ordinal;
        if (frame.Ordinal == topOrdinal && snapshot.ActiveErrorContext is not null)
        {
            scopes.Add(new Scope("Error Context", ErrorContextRef(frame.Ordinal), expensive: false)
            {
                NamedVariables = 6,
            });
        }

        // M5 I4 (§12.2): Temp Tables — always shown (like Locals), even empty;
        // "expensive" per DAP convention (values are lazy, first-expand round trips).
        scopes.Add(new Scope("Temp Tables", TempTablesRef(frame.Ordinal), expensive: true)
        {
            NamedVariables = frame.TempObjects.Count,
        });

        // M5 I3 (§12.1): System — always shown, zero round trips ever.
        scopes.Add(new Scope("System", SystemRef(frame.Ordinal), expensive: false));

        return new ScopesResponse(scopes);
    }

    // M5 I1/I2 (A15): the ONE request that may need a real round trip (a Locals
    // value fill). Overriding the *Async responder variant — rather than the plain
    // HandleVariablesRequest — is what lets the protocol thread return immediately:
    // the fill enqueues onto the inspection executor and responder.SetResponse is
    // called later, from whichever thread the executor's FIFO lane runs on.
    protected override void HandleVariablesRequestAsync(IRequestResponder<VariablesArguments, VariablesResponse> responder)
    {
        var reference = responder.Arguments.VariablesReference;
        _trace.Event("dap.request", $"variables ref={reference}");

        var snapshot = _executor.CurrentSnapshot;
        if (_liveSession is null || snapshot is null)
        {
            responder.SetResponse(new VariablesResponse(new List<Variable>()));
            return;
        }

        if (_executor.IsRunning)
        {
            responder.SetError(new ProtocolException("Not stopped."));
            return;
        }

        if (reference is >= ErrorContextVariablesReferenceBase and < ErrorContextVariablesReferenceBase + Core.Interpreter.FrameStack.MaxDepth)
        {
            var values = snapshot.ActiveErrorContext;
            var errorContextVariables = values is null
                ? new List<Variable>()
                : new List<Variable>
                {
                    new("Number", values.Number.ToString(), 0),
                    new("Severity", values.Severity.ToString(), 0),
                    new("State", values.State.ToString(), 0),
                    new("Line", values.Line?.ToString() ?? "NULL", 0),
                    new("Procedure", values.Procedure ?? "NULL", 0),
                    new("Message", values.Message, 0),
                };
            responder.SetResponse(new VariablesResponse(errorContextVariables));
            return;
        }

        if (reference is >= SystemVariablesReferenceBase and < SystemVariablesReferenceBase + Core.Interpreter.FrameStack.MaxDepth)
        {
            var frame = snapshot.Frames.FirstOrDefault(f => f.Ordinal == reference - SystemVariablesReferenceBase);
            responder.SetResponse(new VariablesResponse(frame is null ? new List<Variable>() : RenderSystemScope(snapshot.GetSystemScope(frame))));
            return;
        }

        if (reference is >= TempTablesVariablesReferenceBase and < TempTablesVariablesReferenceBase + Core.Interpreter.FrameStack.MaxDepth)
        {
            var frame = snapshot.Frames.FirstOrDefault(f => f.Ordinal == reference - TempTablesVariablesReferenceBase);
            if (frame is null)
            {
                responder.SetResponse(new VariablesResponse(new List<Variable>()));
                return;
            }

            _ = FillTempTablesAndRespondAsync(responder, snapshot, frame);
            return;
        }

        if (reference >= DynamicReferenceFloor)
        {
            if (snapshot.TryResolveRowsReference(reference, out var physicalName))
            {
                _ = FillTempTableRowsAndRespondAsync(responder, physicalName, snapshot);
                return;
            }

            if (snapshot.TryResolveRowDetail(reference, out var detail))
            {
                var displayChars = _liveSession.Session.DisplayValueChars;
                var cells = detail.Columns
                    .Select((col, i) => new Variable(col, Truncate(CellText(detail.Values[i]), displayChars), 0))
                    .ToList();
                responder.SetResponse(new VariablesResponse(cells));
                return;
            }

            responder.SetResponse(new VariablesResponse(new List<Variable>()));
            return;
        }

        if (reference is < LocalsVariablesReferenceBase or >= LocalsVariablesReferenceBase + Core.Interpreter.FrameStack.MaxDepth)
        {
            responder.SetResponse(new VariablesResponse(new List<Variable>()));
            return;
        }

        var localsFrame = snapshot.Frames.FirstOrDefault(f => f.Ordinal == reference - LocalsVariablesReferenceBase);
        if (localsFrame is null)
        {
            responder.SetResponse(new VariablesResponse(new List<Variable>()));
            return;
        }

        // I1: repeat request for (epoch, frame) in the same stop — a cache hit,
        // answered without touching the executor at all.
        if (snapshot.TryGetCachedValues(localsFrame.Ordinal, out var cachedValues))
        {
            responder.SetResponse(new VariablesResponse(RenderLocals(localsFrame, cachedValues)));
            return;
        }

        var sessionFrame = _liveSession.Session.Frames.FirstOrDefault(f => f.Ordinal == localsFrame.Ordinal);
        if (sessionFrame is null)
        {
            responder.SetResponse(new VariablesResponse(new List<Variable>()));
            return;
        }

        _ = FillLocalsAndRespondAsync(responder, snapshot, localsFrame, sessionFrame);
    }

    // M5 I3 (§12.1): zero round trips — every value already lives in the snapshot.
    private static List<Variable> RenderSystemScope(SystemScopeValues values)
    {
        var variables = new List<Variable>
        {
            new("@@TRANCOUNT", values.Trancount.ToString(), 0),
            new("XACT_STATE()", values.XactState.ToString(), 0),
            new("@@SPID", values.Spid.ToString(), 0),
            new("XACT_ABORT", values.XactAbortOn ? "ON" : "OFF", 0),
            new("QUOTED_IDENTIFIER", values.QuotedIdentifier ? "ON" : "OFF", 0),  // A52 (§12.1)
            new("ANSI_NULLS", values.AnsiNulls ? "ON" : "OFF", 0),                // A52 (§12.1)
            new("Session mode", values.ModeAnnotation, 0),
        };
        foreach (var (option, value) in values.RuntimeOptions)
        {
            variables.Add(new Variable(option, value, 0));
        }

        return variables;
    }

    // I1/I2: first request for (epoch, frame) — enqueues the fill onto the single
    // FIFO inspection lane and awaits it; THIS call never touches the connection or
    // the gate directly (EnqueueAsync does, on the executor's pump).
    private async Task FillLocalsAndRespondAsync(
        IRequestResponder<VariablesArguments, VariablesResponse> responder,
        StopSnapshot snapshot, SnapshotFrame frame, Core.Interpreter.Frame sessionFrame)
    {
        try
        {
            var result = await _executor.EnqueueAsync(
                snapshot.Epoch, ct => _liveSession!.Session.GetStateSnapshotAsync(sessionFrame, ct)).ConfigureAwait(false);

            if (TryReportNonCompletion(result, responder))
            {
                return;
            }

            snapshot.CacheValues(frame.Ordinal, result.Value!);
            responder.SetResponse(new VariablesResponse(RenderLocals(frame, result.Value!)));
        }
        catch (Exception ex)
        {
            responder.SetError(new ProtocolException(ex.Message));
        }
    }

    // DESIGN §8.1/§12.1 display projection, unchanged from the pre-M5 shape — only
    // its caller (above) moved from a gated live read to an executor-enqueued fill.
    private static List<Variable> RenderLocals(SnapshotFrame frame, StateSnapshot values)
    {
        return frame.Variables
            .Select(slot =>
            {
                var hasValue = values.TryGet(slot.Declaration.Name, out var value);
                var display = !hasValue || value is null ? "NULL" : value.ToString() ?? "NULL";
                return new Variable(slot.Declaration.Name, display, 0) { Type = slot.Declaration.DataTypeSql };
            })
            .ToList();
    }

    // M5 I4: first request for the Temp Tables SCOPE in this epoch — fills every
    // not-yet-cached entry's value (rowcount / cursor status) in ONE executor item,
    // then renders the whole list. Repeat requests are answered from the per-entry
    // cache without touching the executor at all. Doomed/broken entries never reach
    // here with a pending fill — EagerDisplay is already set for those (I4).
    private async Task FillTempTablesAndRespondAsync(
        IRequestResponder<VariablesArguments, VariablesResponse> responder, StopSnapshot snapshot, SnapshotFrame frame)
    {
        var pending = frame.TempObjects
            .Where(o => o.EagerDisplay is null && !snapshot.TryGetCachedTempValue(o.PhysicalName, out _))
            .ToList();
        if (pending.Count == 0)
        {
            responder.SetResponse(new VariablesResponse(RenderTempTables(snapshot, frame)));
            return;
        }

        try
        {
            var result = await _executor.EnqueueAsync(snapshot.Epoch, async ct =>
            {
                foreach (var entry in pending)
                {
                    var display = entry.Kind == Core.Interpreter.TempObjectKind.Cursor
                        ? await RenderCursorStatusAsync(entry.PhysicalName, ct).ConfigureAwait(false)
                        : await RenderRowCountAsync(entry.PhysicalName, ct).ConfigureAwait(false);
                    snapshot.CacheTempValue(entry.PhysicalName, display);
                }

                return true;
            }).ConfigureAwait(false);

            if (TryReportNonCompletion(result, responder))
            {
                return;
            }

            responder.SetResponse(new VariablesResponse(RenderTempTables(snapshot, frame)));
        }
        catch (Exception ex)
        {
            responder.SetError(new ProtocolException(ex.Message));
        }
    }

    // M5 I4 (§12.2): SELECT COUNT(*) lazily, warn-once above the 100k threshold.
    private async Task<string> RenderRowCountAsync(string physicalName, CancellationToken ct)
    {
        var (count, fault) = await _liveSession!.Session.GetTempObjectRowCountAsync(physicalName, ct).ConfigureAwait(false);
        if (fault is not null)
        {
            return $"(error: {fault})";
        }

        if (count is { } n && n > LargeTempTableWarnThreshold && _warnedLargeTempTables.Add(physicalName))
        {
            Protocol.SendEvent(new OutputEvent(
                $"Temp Tables: '{physicalName}' has {n} rows (> {LargeTempTableWarnThreshold:N0}) — paging may be slow (§12.2).\n")
            { Category = OutputEvent.CategoryValue.Console });
        }

        return $"({count} rows)";
    }

    private async Task<string> RenderCursorStatusAsync(string physicalName, CancellationToken ct)
    {
        var (status, fault) = await _liveSession!.Session.GetCursorStatusAsync(physicalName, ct).ConfigureAwait(false);
        return fault is not null ? $"(error: {fault})" : $"({status})";
    }

    // M5 I4: original name displayed, physical name in evaluateName (§12.2). A
    // resolved rowcount > 0 (table/table-variable, not cursor) mints this snapshot's
    // stable "rows" children reference for OFFSET/FETCH paging.
    private static List<Variable> RenderTempTables(StopSnapshot snapshot, SnapshotFrame frame)
    {
        var variables = new List<Variable>();
        foreach (var entry in frame.TempObjects)
        {
            var display = entry.EagerDisplay
                ?? (snapshot.TryGetCachedTempValue(entry.PhysicalName, out var cached) ? cached : "(pending)");
            var variable = new Variable(entry.OriginalName, display, 0) { EvaluateName = entry.PhysicalName };
            if (entry.Kind != Core.Interpreter.TempObjectKind.Cursor && TryParseRowCount(display, out var rowCount) && rowCount > 0)
            {
                variable.VariablesReference = snapshot.GetOrMintRowsReference(entry.PhysicalName);
                variable.IndexedVariables = rowCount;
            }

            variables.Add(variable);
        }

        return variables;
    }

    private static bool TryParseRowCount(string display, out int rowCount)
    {
        rowCount = 0;
        const string suffix = " rows)";
        if (!display.StartsWith('(') || !display.EndsWith(suffix))
        {
            return false;
        }

        return int.TryParse(display.AsSpan(1, display.Length - 1 - suffix.Length), out rowCount);
    }

    // M5 I4 (§12.2 paging): a table's "rows" children — OFFSET/FETCH via the DAP
    // Start/Count paging args (supportsVariablePaging). Each row is itself an indexed
    // Variable (name = absolute row index) whose OWN children (minted eagerly from
    // this same already-fetched page — no second round trip) are the row's columns,
    // cells truncated client-side to Session.DisplayValueChars (§7.5-style, but these
    // values never went through the state table's CONVERT/LEFT projection — they're
    // raw ADO.NET typed values here).
    private async Task FillTempTableRowsAndRespondAsync(
        IRequestResponder<VariablesArguments, VariablesResponse> responder, string physicalName, StopSnapshot snapshot)
    {
        var args = responder.Arguments;
        var start = args.Start ?? 0;
        var count = args.Count ?? _liveSession!.Session.TempTablePageSize;

        try
        {
            var result = await _executor.EnqueueAsync(
                snapshot.Epoch, ct => _liveSession!.Session.GetTempObjectPageAsync(physicalName, start, count, ct)).ConfigureAwait(false);

            if (TryReportNonCompletion(result, responder))
            {
                return;
            }

            var (page, fault) = result.Value;
            if (fault is not null)
            {
                responder.SetResponse(new VariablesResponse(new List<Variable> { new("(error)", fault, 0) }));
                return;
            }

            var displayChars = _liveSession!.Session.DisplayValueChars;
            var variables = new List<Variable>();
            for (var r = 0; r < page!.Rows.Count; r++)
            {
                var row = page.Rows[r];
                var preview = Truncate(
                    string.Join(", ", page.Columns.Select((col, c) => $"{col}={CellText(row[c])}")), displayChars);
                var rowReference = snapshot.MintRowDetailReference(page.Columns, row);
                variables.Add(new Variable((start + r).ToString(), preview, rowReference)
                {
                    NamedVariables = page.Columns.Count,
                });
            }

            responder.SetResponse(new VariablesResponse(variables));
        }
        catch (Exception ex)
        {
            responder.SetError(new ProtocolException(ex.Message));
        }
    }

    // M7 (§5.4, S4 cancel): the shared non-completion reporter for every executor-
    // enqueued inspection fill (Locals/Temp Tables/rows/REPL/watch/setVariable) —
    // Invalidated (a resume preempted it, §10.5-adjacent) reads "Not stopped."
    // exactly as before; Cancelled (a DAP `cancel` request preempted it, §5.4) is
    // now distinguishable, wire-visible as its own error text rather than being
    // folded into the same message. IRequestResponder (non-generic) is the base
    // every IRequestResponder&lt;TArgs,TResponse&gt; implements, so one helper covers
    // all eight call sites regardless of their response type.
    private static bool TryReportNonCompletion<T>(InspectionExecutor.InspectionResult<T> result, IRequestResponder responder)
    {
        switch (result.Outcome)
        {
            case InspectionExecutor.InspectionOutcome.Invalidated:
                responder.SetError(new ProtocolException("Not stopped."));
                return true;
            case InspectionExecutor.InspectionOutcome.Cancelled:
                responder.SetError(new ProtocolException("Cancelled."));
                return true;
            default:
                return false;
        }
    }

    private static string CellText(object? value) => value is null ? "NULL" : value.ToString() ?? "NULL";

    private static string Truncate(string text, int max) => text.Length > max ? text[..max] : text;

    // M5 I5 (§12.4 hover — design note §2): "token classification client-side ...
    // no new machinery: hover is a thin router over I1/I4." Only context:"hover" is
    // implemented here; REPL (I6) and watch (I7) land in their own checklist items.
    protected override void HandleEvaluateRequestAsync(IRequestResponder<EvaluateArguments, EvaluateResponse> responder)
    {
        var args = responder.Arguments;
        _trace.Event("dap.request", $"evaluate context={args.Context} expr={args.Expression}");

        if (args.Context is not (EvaluateArguments.ContextValue.Hover or EvaluateArguments.ContextValue.Repl or EvaluateArguments.ContextValue.Watch))
        {
            responder.SetError(new ProtocolException($"evaluate context '{args.Context}' is not yet implemented (M5)."));
            return;
        }

        var snapshot = _executor.CurrentSnapshot;
        if (_liveSession is null || snapshot is null)
        {
            responder.SetError(new ProtocolException("Not stopped."));
            return;
        }

        if (_executor.IsRunning)
        {
            responder.SetError(new ProtocolException("Not stopped."));
            return;
        }

        var frameOrdinal = args.FrameId ?? snapshot.Frames[^1].Ordinal;
        var frame = snapshot.Frames.FirstOrDefault(f => f.Ordinal == frameOrdinal);
        if (frame is null)
        {
            responder.SetError(new ProtocolException("no such frame"));
            return;
        }

        if (args.Context == EvaluateArguments.ContextValue.Repl)
        {
            var sessionFrame = _liveSession.Session.Frames.FirstOrDefault(f => f.Ordinal == frameOrdinal);
            if (sessionFrame is null)
            {
                responder.SetError(new ProtocolException("no such frame"));
                return;
            }

            _ = EvaluateReplAndRespondAsync(responder, snapshot, sessionFrame, args.Expression ?? string.Empty);
            return;
        }

        if (args.Context == EvaluateArguments.ContextValue.Watch)
        {
            var sessionFrame = _liveSession.Session.Frames.FirstOrDefault(f => f.Ordinal == frameOrdinal);
            if (sessionFrame is null)
            {
                responder.SetError(new ProtocolException("no such frame"));
                return;
            }

            EvaluateWatchAndRespond(responder, snapshot, sessionFrame, args.Expression ?? string.Empty);
            return;
        }

        var token = (args.Expression ?? string.Empty).Trim();

        // Variable → the I1 value cache. Locals are per-FRAME (T-SQL scoping), never
        // chain-resolved — a callee cannot see a caller's @variable.
        var variableSlot = frame.Variables.FirstOrDefault(v => string.Equals(v.Declaration.Name, token, StringComparison.OrdinalIgnoreCase));
        if (variableSlot is not null)
        {
            _ = HoverVariableAsync(responder, snapshot, frame, variableSlot);
            return;
        }

        // Temp/table-var name resolvable in the hovered frame's CHAIN → the I4 value
        // string (frame.TempObjects is already the chain-resolved, deduped list).
        var tempEntry = frame.TempObjects.FirstOrDefault(o => string.Equals(o.OriginalName, token, StringComparison.OrdinalIgnoreCase));
        if (tempEntry is not null)
        {
            _ = HoverTempObjectAsync(responder, snapshot, tempEntry);
            return;
        }

        // Anything else → no result (§12.4).
        responder.SetError(new ProtocolException("no hover result"));
    }

    private async Task HoverVariableAsync(
        IRequestResponder<EvaluateArguments, EvaluateResponse> responder,
        StopSnapshot snapshot, SnapshotFrame frame, Core.Interpreter.VariableSlot slot)
    {
        if (snapshot.TryGetCachedValues(frame.Ordinal, out var cachedValues))
        {
            responder.SetResponse(BuildHoverVariableResponse(slot, cachedValues));
            return;
        }

        var sessionFrame = _liveSession!.Session.Frames.FirstOrDefault(f => f.Ordinal == frame.Ordinal);
        if (sessionFrame is null)
        {
            responder.SetError(new ProtocolException("no such frame"));
            return;
        }

        try
        {
            // I5: "enqueue a fill if the frame's values were never fetched this stop" —
            // the SAME fill-once slot the Locals scope uses; a hover that lands after
            // the scope was already expanded is a cache hit, above.
            var result = await _executor.EnqueueAsync(
                snapshot.Epoch, ct => _liveSession!.Session.GetStateSnapshotAsync(sessionFrame, ct)).ConfigureAwait(false);
            if (TryReportNonCompletion(result, responder))
            {
                return;
            }

            snapshot.CacheValues(frame.Ordinal, result.Value!);
            responder.SetResponse(BuildHoverVariableResponse(slot, result.Value!));
        }
        catch (Exception ex)
        {
            responder.SetError(new ProtocolException(ex.Message));
        }
    }

    private static EvaluateResponse BuildHoverVariableResponse(Core.Interpreter.VariableSlot slot, StateSnapshot values)
    {
        var hasValue = values.TryGet(slot.Declaration.Name, out var value);
        var display = !hasValue || value is null ? "NULL" : value.ToString() ?? "NULL";
        return new EvaluateResponse(display, 0) { Type = slot.Declaration.DataTypeSql };
    }

    private async Task HoverTempObjectAsync(
        IRequestResponder<EvaluateArguments, EvaluateResponse> responder, StopSnapshot snapshot, TempObjectProjection entry)
    {
        if (entry.EagerDisplay is not null)
        {
            responder.SetResponse(new EvaluateResponse(entry.EagerDisplay, 0));
            return;
        }

        if (snapshot.TryGetCachedTempValue(entry.PhysicalName, out var cachedValue))
        {
            responder.SetResponse(new EvaluateResponse(cachedValue, 0));
            return;
        }

        try
        {
            var result = await _executor.EnqueueAsync(snapshot.Epoch, ct => entry.Kind == Core.Interpreter.TempObjectKind.Cursor
                ? RenderCursorStatusAsync(entry.PhysicalName, ct)
                : RenderRowCountAsync(entry.PhysicalName, ct)).ConfigureAwait(false);

            if (TryReportNonCompletion(result, responder))
            {
                return;
            }

            snapshot.CacheTempValue(entry.PhysicalName, result.Value!);
            responder.SetResponse(new EvaluateResponse(result.Value!, 0));
        }
        catch (Exception ex)
        {
            responder.SetError(new ProtocolException(ex.Message));
        }
    }

    // M5 I6 (§12.3 REPL): enqueues onto the I2 executor with its OWN consoleTimeoutSec
    // (§15) — a linked CancellationTokenSource layered UNDER the executor's own token,
    // so a genuine timeout is caught and returned as a normal ReplResult (never seen
    // by EnqueueAsync as an OperationCanceledException, which would otherwise
    // misclassify it as "Invalidated by resume" instead of "the console statement
    // itself timed out"). A real resume still preempts normally (the `when` filter
    // lets that exception propagate to EnqueueAsync's own classification).
    private async Task EvaluateReplAndRespondAsync(
        IRequestResponder<EvaluateArguments, EvaluateResponse> responder,
        StopSnapshot snapshot, Core.Interpreter.Frame sessionFrame, string statementText)
    {
        try
        {
            var result = await _executor.EnqueueAsync(snapshot.Epoch, async ct =>
            {
                var consoleTimeoutSec = _liveSession!.Session.ConsoleTimeoutSeconds;
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(consoleTimeoutSec));
                try
                {
                    return await _liveSession.Session.EvaluateReplAsync(sessionFrame, statementText, timeoutCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    return new Session.ReplResult(Session.ReplOutcome.Refused, null,
                        $"console statement timed out after {consoleTimeoutSec}s (consoleTimeoutSec).");
                }
            }).ConfigureAwait(false);

            if (TryReportNonCompletion(result, responder))
            {
                return;
            }

            var replResult = result.Value!;
            if (replResult.Outcome == Session.ReplOutcome.Refused)
            {
                responder.SetError(new ProtocolException(replResult.RefusalMessage ?? "console statement refused."));
                return;
            }

            // M6 R2 (design note §5-R2, A25): a write-mode REPL's trailing probe may
            // have moved trancount/xact_state/session-mode — republish before
            // responding so a client that reads System scope right after this
            // evaluate call already sees the current values.
            RepublishSystemScopeIfChanged(snapshot);

            // A46: a write-mode console statement may have changed variable values (SET
            // @x = …, EXEC … @x OUTPUT, …) — drop the frame's cached Locals so the next
            // `variables` request re-fills fresh, and emit `invalidated` so the client
            // refetches now. Mirrors setVariable's InvalidateValues, but for the whole
            // frame (one statement can change several variables at once).
            if (replResult.VariablesChanged)
            {
                snapshot.InvalidateValues(sessionFrame.Ordinal);
                Protocol.SendEvent(new InvalidatedEvent
                {
                    Areas = new List<InvalidatedAreas> { InvalidatedAreas.Variables },
                    ThreadId = 1,
                });
            }

            responder.SetResponse(new EvaluateResponse(replResult.Rendered ?? string.Empty, 0));
        }
        catch (Exception ex)
        {
            responder.SetError(new ProtocolException(ex.Message));
        }
    }

    // M6 R2 (design note §5-R2, A25): compares the CURRENTLY tracked
    // trancount/xact_state/mode against what every frame's System scope currently
    // shows (the eager baseline, or an earlier republish) and, on any difference,
    // rebuilds every frame's scope in place and emits ONE `invalidated` event. Same
    // epoch, same frames, same variablesReference space (I1 untouched) — additive
    // only, so a client that ignores `invalidated` loses nothing it had before.
    private void RepublishSystemScopeIfChanged(StopSnapshot snapshot)
    {
        if (_liveSession is null)
        {
            return;
        }

        var session = _liveSession.Session;
        var modeAnnotation = session.IsBroken ? "broken"
            : session.IsDoomed ? "doomed"
            : session.IsTransactionDetached ? "detached"
            : "healthy";
        var runtimeOptions = session.RuntimeOptionsSnapshot;
        var changed = false;
        foreach (var frame in snapshot.Frames)
        {
            var current = snapshot.GetSystemScope(frame);
            if (current.Trancount == session.LastObservedTrancount
                && current.XactState == session.LastObservedXactState
                && current.ModeAnnotation == modeAnnotation)
            {
                continue;
            }

            changed = true;
            snapshot.RepublishSystemScope(frame.Ordinal, new SystemScopeValues(
                session.LastObservedTrancount, session.LastObservedXactState, session.Spid,
                current.XactAbortOn, runtimeOptions, modeAnnotation,
                current.QuotedIdentifier, current.AnsiNulls));  // A52: parse-time options don't move on a republish
        }

        if (changed)
        {
            Protocol.SendEvent(new InvalidatedEvent
            {
                Areas = new List<InvalidatedAreas> { InvalidatedAreas.Variables },
                ThreadId = 1,
            });
        }
    }

    // M5 I7 (§12.4 watch): the budget check is SYNCHRONOUS and must happen before
    // anything touches the executor — "any watch whose turn comes after watchBudgetMs
    // has elapsed returns ⏱ WITHOUT touching the connection." StopSnapshot's stopwatch
    // starts on the first watch of this epoch; sequential DAP dispatch (one request at
    // a time from the client) is what makes this race-free without an explicit lock
    // here (StopSnapshot's own lock covers concurrent callers regardless).
    private void EvaluateWatchAndRespond(
        IRequestResponder<EvaluateArguments, EvaluateResponse> responder,
        StopSnapshot snapshot, Core.Interpreter.Frame sessionFrame, string expressionText)
    {
        var expressionKey = expressionText.Trim();
        var isClickToEvaluate = snapshot.HasWatchOverflowed(expressionKey);

        if (!isClickToEvaluate && !snapshot.TryBeginWatchTurn(expressionKey, _liveSession!.Session.WatchBudgetMs))
        {
            responder.SetResponse(new EvaluateResponse("⏱ (click to evaluate)", 0));
            return;
        }

        _ = EvaluateWatchAndRespondAsync(responder, sessionFrame, expressionText, isClickToEvaluate);
    }

    private async Task EvaluateWatchAndRespondAsync(
        IRequestResponder<EvaluateArguments, EvaluateResponse> responder,
        Core.Interpreter.Frame sessionFrame, string expressionText, bool isClickToEvaluate)
    {
        try
        {
            var epoch = _executor.CurrentSnapshot!.Epoch;
            var result = await _executor.EnqueueAsync(epoch, async ct =>
            {
                if (!isClickToEvaluate)
                {
                    return await _liveSession!.Session.EvaluateWatchAsync(sessionFrame, expressionText, ct).ConfigureAwait(false);
                }

                // §12.4: "runs outside the budget under its own consoleTimeoutSec,
                // cancellable" — same timeout-vs-resume distinction as REPL: a genuine
                // timeout is caught here and returned as a normal value, never seen by
                // EnqueueAsync as an OperationCanceledException (which would
                // misclassify it as a resume-preemption).
                var consoleTimeoutSec = _liveSession!.Session.ConsoleTimeoutSeconds;
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(consoleTimeoutSec));
                try
                {
                    return await _liveSession.Session.EvaluateWatchAsync(sessionFrame, expressionText, timeoutCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    return $"timed out after {consoleTimeoutSec}s (consoleTimeoutSec).";
                }
            }).ConfigureAwait(false);

            if (TryReportNonCompletion(result, responder))
            {
                return;
            }

            responder.SetResponse(new EvaluateResponse(result.Value!, 0));
        }
        catch (Exception ex)
        {
            responder.SetError(new ProtocolException(ex.Message));
        }
    }

    // M5 I8 (§8.3 setVariable, A19): only the Locals scope is writable — System/Temp
    // Tables/Error Context entries are all display-only, refused here rather than
    // silently accepted. The healthy/detached/doomed/broken arms all live in
    // Session.SetVariableAsync; this handler just enqueues and renders the outcome.
    protected override void HandleSetVariableRequestAsync(IRequestResponder<SetVariableArguments, SetVariableResponse> responder)
    {
        var args = responder.Arguments;
        _trace.Event("dap.request", $"setVariable ref={args.VariablesReference} name={args.Name} value={args.Value}");

        if (args.VariablesReference is < LocalsVariablesReferenceBase or >= LocalsVariablesReferenceBase + Core.Interpreter.FrameStack.MaxDepth)
        {
            responder.SetError(new ProtocolException("setVariable is only supported on the Locals scope (§8.3)."));
            return;
        }

        var snapshot = _executor.CurrentSnapshot;
        if (_liveSession is null || snapshot is null)
        {
            responder.SetError(new ProtocolException("Not stopped."));
            return;
        }

        if (_executor.IsRunning)
        {
            responder.SetError(new ProtocolException("Not stopped."));
            return;
        }

        var frameOrdinal = args.VariablesReference - LocalsVariablesReferenceBase;
        var sessionFrame = _liveSession.Session.Frames.FirstOrDefault(f => f.Ordinal == frameOrdinal);
        if (sessionFrame is null)
        {
            responder.SetError(new ProtocolException("no such frame"));
            return;
        }

        _ = SetVariableAndRespondAsync(responder, snapshot, sessionFrame, args.Name, args.Value);
    }

    private async Task SetVariableAndRespondAsync(
        IRequestResponder<SetVariableArguments, SetVariableResponse> responder,
        StopSnapshot snapshot, Core.Interpreter.Frame sessionFrame, string variableName, string literalText)
    {
        try
        {
            // I2: even the doomed arm (no server round trip at all) goes through the
            // SAME executor lane — it still mutates Frame.Snapshot, which the stepping
            // task also touches, so it needs the same gate serialization as everything
            // else, not just the arms that hit the connection.
            var result = await _executor.EnqueueAsync(
                snapshot.Epoch, ct => _liveSession!.Session.SetVariableAsync(sessionFrame, variableName, literalText, ct)).ConfigureAwait(false);

            if (TryReportNonCompletion(result, responder))
            {
                return;
            }

            var setResult = result.Value!;
            if (setResult.Outcome == Session.SetVariableOutcome.Refused)
            {
                responder.SetError(new ProtocolException(setResult.RefusalReason ?? "setVariable refused."));
                return;
            }

            if (setResult.Note is not null)
            {
                Protocol.SendEvent(new OutputEvent($"{setResult.Note}\n") { Category = OutputEvent.CategoryValue.Console });
            }

            // The frame's cached Locals values are stale the instant this succeeds —
            // drop them so the next `variables` request re-fills fresh.
            snapshot.InvalidateValues(sessionFrame.Ordinal);
            var display = setResult.AppliedValue is null or DBNull ? "NULL" : setResult.AppliedValue.ToString() ?? "NULL";
            responder.SetResponse(new SetVariableResponse(display));
        }
        catch (Exception ex)
        {
            responder.SetError(new ProtocolException(ex.Message));
        }
    }

    // §18 (S4): the EXPLICIT terminate path — the only one Session.TeardownAsync
    // ever receives a commit-decision callback on (§16 commit-modal; disconnect,
    // errors, and a lost adapter process all roll back unconditionally, CLAUDE.md
    // safety rule 7). §5.3's pre-cancel (rider (ii), shared verbatim with
    // HandleDisconnectRequest below) runs FIRST, off the protocol thread (A15) —
    // without it a long-running in-flight step would delay this teardown (and any
    // commit-modal prompt) by its remaining runtime.
    protected override TerminateResponse HandleTerminateRequest(TerminateArguments arguments)
    {
        _trace.Event("dap.request", "terminate");
        var cts = _pauseCts;
        _ = Task.Run(() => cts?.Cancel());

        _ = Task.Run(async () =>
        {
            await _executor.Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_liveSession is not null)
                {
                    await TerminateLiveSessionLockedAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                _executor.Gate.Release();
            }
        });

        return new TerminateResponse();
    }

    private async Task TerminateLiveSessionLockedAsync()
    {
        // Mirrors EndSessionLockedAsync's shape (dispose LiveSession + terminated
        // event, leave _executor/_completion for whichever disconnect follows —
        // DAP clients conventionally send one after terminate; HandleDisconnectRequest
        // below already guards on _liveSession is null, so that is idempotent here).
        _executor.MarkIdle();
        var armed = _launchConfig?.CommitMode == CommitMode.Commit;
        Func<Task<bool>>? commitDecision = armed ? RequestCommitConfirmationAsync : null;
        await _liveSession!.DisposeAsync(commitDecision).ConfigureAwait(false);
        _liveSession = null;
        SendEvent(new TerminatedEvent(), "terminated");
    }

    // §16 commit-modal: the custom-event/custom-request round trip with the
    // extension. A 60s timeout (§16's own number) or a reply the extension never
    // sends both resolve to "no" — Session.TeardownAsync then rolls back, exactly
    // as if commitMode were never set at all.
    private async Task<bool> RequestCommitConfirmationAsync()
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingCommitDecision = tcs;
        try
        {
            Protocol.SendEvent(new TsqldbgCommitConfirmEvent(
                _launchConfig!.Server, _launchConfig.Database, _target?.Env ?? "unknown"));

            var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(60))).ConfigureAwait(false);
            if (completed != tcs.Task)
            {
                Protocol.SendEvent(new OutputEvent(
                    "Commit confirmation timed out after 60s with no reply from the extension — rolling back instead (§16).\n")
                { Category = OutputEvent.CategoryValue.Console });
                return false;
            }

            var confirmed = await tcs.Task.ConfigureAwait(false);
            if (!confirmed)
            {
                Protocol.SendEvent(new OutputEvent("Commit declined — rolling back instead (§16).\n")
                {
                    Category = OutputEvent.CategoryValue.Console,
                });
            }

            return confirmed;
        }
        finally
        {
            _pendingCommitDecision = null;
        }
    }

    private void HandleTsqldbgCommitDecisionRequestAsync(
        IRequestResponder<TsqldbgCommitDecisionArguments, TsqldbgCommitDecisionResponseBody> responder)
    {
        _trace.Event("dap.request", $"tsqldbg_commitDecision commit={responder.Arguments.Commit}");
        _pendingCommitDecision?.TrySetResult(responder.Arguments.Commit);
        responder.SetResponse(new TsqldbgCommitDecisionResponseBody());
    }

    // §5.3 rider (ii): HandleDisconnectRequest used to queue teardown behind
    // _executor.Gate with NOTHING preempting an in-flight debuggee batch first — a
    // long-running WAITFOR/loop batch delayed rollback+dispose by its full
    // remaining runtime. The pre-cancel below (same A15-disciplined mechanism as
    // HandlePauseRequest/HandleTerminateRequest: capture the CTS, Cancel() off the
    // protocol thread) makes the in-flight batch die via the attention first, so
    // the gate wait that follows is short. The resulting pause-stop publication
    // (StepOnceLockedAsync/RunUntilAsync's normal OperationCanceledException path)
    // is harmless noise — the client is already gone.
    protected override DisconnectResponse HandleDisconnectRequest(DisconnectArguments arguments)
    {
        _trace.Event("dap.disconnect", string.Empty);
        var cts = _pauseCts;
        _ = Task.Run(() => cts?.Cancel());

        _ = Task.Run(async () =>
        {
            await _executor.Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_liveSession is not null)
                {
                    await _liveSession.DisposeAsync().ConfigureAwait(false);
                    _liveSession = null;
                }
            }
            finally
            {
                _executor.Gate.Release();
            }

            await _executor.DisposeAsync().ConfigureAwait(false);
            _completion.TrySetResult();
        });

        return new DisconnectResponse();
    }

    // --- locked helpers (caller must hold the executor's gate) -------------------

    // M7 FYI(a) (orchestrator ruling 2026-07-08, docs/archive/reviews/m7-hardening-sonnet.md):
    // Session.StepAsync/TryStepBoostedAsync return a per-step Messages channel —
    // console-note strings (C2's DML-trigger note, C5's step-time SET NOCOUNT note,
    // the M4 D6 untracked-SET-option note, WAITFOR-skip notes) AND native server
    // messages (PRINT/absorbed error text). Both step-driver sites (StepOnceLockedAsync
    // and RunUntilAsync's non-boosted + boosted step calls) previously DISCARDED this
    // list entirely, so those notes were generated but never reached the live Debug
    // Console (C2/C5 were a hollow feature in the real adapter — invisible). Emit each
    // as a Console OutputEvent, BEFORE that step's stop/pause publish, so the note
    // precedes the stop in the event stream (the §18 category the ruling pins — matching
    // LaunchWarnings/the COMMIT-policy note/logpoints, all Console). Fidelity is
    // unaffected — the fidelity harness drives SessionHost/RunToEndAsync directly and
    // reads SessionResult.Execution.Messages, never the DAP OutputEvent stream.
    private void EmitStepMessages(IReadOnlyList<string> messages)
    {
        foreach (var message in messages)
        {
            Protocol.SendEvent(new OutputEvent($"{message}\n") { Category = OutputEvent.CategoryValue.Console });
        }

        // A56 (§12.3/§15): after this step's debuggee output, surface the Core's
        // diagnostic annotations — gated on logLevel. Always DRAINED here (even at
        // normal verbosity) so the buffer never leaks a note into a later step's output.
        EmitDiagnosticNotes();
    }

    // A56 (§12.3/§15): logLevel:"verbose" launch flag. false (default "normal") suppresses
    // the debugger's own cosmetic/navigational annotations; debuggee output, errors,
    // logpoints, halt-explanations, and §16 notices are unaffected.
    private bool Verbose => _launchConfig?.Verbose ?? false;

    // A56: drain the Core's diagnostic-note channel and surface it to the Debug Console
    // only under logLevel:"verbose". The drain runs unconditionally (clearing the buffer);
    // the logLevel decides whether the drained notes are shown. The A54 implicit-return
    // note is NOT on this channel — it explains the halt the user is parked on, like the
    // goto/pause/commit notices, so it is always shown (EmitImplicitReturnNote).
    private void EmitDiagnosticNotes()
    {
        var notes = _liveSession?.Session.DrainDiagnosticNotes();
        if (notes is null || notes.Count == 0 || !Verbose)
        {
            return;
        }

        foreach (var note in notes)
        {
            Protocol.SendEvent(new OutputEvent($"{note}\n") { Category = OutputEvent.CategoryValue.Console });
        }
    }

    // A50 (§12.3): a stepped/continued statement's OWN result sets — a bare SELECT, a
    // proc's SELECT, etc. — were previously dropped ("deliberately out of scope"): a
    // stepped SELECT that returns rows showed nothing, only the REPL rendered result
    // sets. Now they render to the Debug Console through the SAME §12.3 projection the
    // REPL uses (aligned text tables capped at maxConsoleRows), emitted after this step's
    // messages and before its stop/pause publish. Nothing to show (no sets, or only
    // column-less sets — e.g. a control-row-only step, INSERT, SET) emits nothing.
    // Fidelity is unaffected for the same reason EmitStepMessages is (DAP-stream only).
    // A54 (§6/§11.5): the console note for a parked implicit-return stop. The stop rests at
    // the end of the module body — Locals + OUTPUT params hold their final in-proc values,
    // the pending return code is 0 — and the next step performs the §11.5 pop. For the
    // top-level proc in procedure mode there is no caller, so the next step finishes.
    private void EmitImplicitReturnNote()
    {
        var session = _liveSession?.Session;
        if (session is null)
        {
            return;
        }

        var name = session.TopFrame?.Module.Display ?? "procedure";
        var next = session.Frames.Count > 1 ? "return to the caller" : "finish";
        Protocol.SendEvent(new OutputEvent(
            $"-- end of {name} — implicit RETURN (return code 0); step again to {next}.\n")
        { Category = OutputEvent.CategoryValue.Console });
    }

    private void EmitStepResultSets(IReadOnlyList<ResultSet> resultSets)
    {
        if (resultSets.Count == 0 || _liveSession is null)
        {
            return;
        }

        var rendered = Session.RenderResultSetsAsText(resultSets, _liveSession.Session.MaxConsoleRows);
        if (rendered.Length > 0)
        {
            Protocol.SendEvent(new OutputEvent($"{rendered}\n") { Category = OutputEvent.CategoryValue.Stdout });
        }
    }

    private async Task StepOnceLockedAsync(int epoch, StoppedEvent.ReasonValue reasonOnStop, StepKind kind = StepKind.Over)
    {
        if (_liveSession is null || _liveSession.Session.IsCompleted)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        _pauseCts = cts;
        IReadOnlyList<string> stepMessages;
        IReadOnlyList<ResultSet> stepResultSets;
        try
        {
            (stepResultSets, stepMessages) = await _liveSession.Session.StepAsync(kind, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // §10.5: the in-flight statement was cancelled via a pause request. The
            // token cancellation short-circuits before any control row comes back, so
            // the cursor/shadows/snapshot are exactly as they were before this step —
            // no different from never having stepped at all.
            PublishSnapshotAndPause(epoch);
            return;
        }
        catch (Exception ex)
        {
            await ReportFaultAndEndLockedAsync(ex).ConfigureAwait(false);
            return;
        }
        finally
        {
            _pauseCts = null;
        }

        // M7 FYI(a): forward this step's console notes BEFORE any stop/pause publish
        // below (including the completion path — a note on the final SU still shows
        // before `terminated`). A50: the statement's own result sets follow the notes.
        EmitStepMessages(stepMessages);
        EmitStepResultSets(stepResultSets);

        // A54 (§6/§11.5): a MODULE frame just parked at its implicit return — the step ran
        // off the last statement of the body (no explicit RETURN) and rests at the proc's
        // end for inspection. Explain it; the NEXT step performs the pop.
        if (_liveSession.Session.AtImplicitReturn)
        {
            EmitImplicitReturnNote();
        }

        if (_liveSession.Session.IsCompleted)
        {
            await EndSessionLockedAsync().ConfigureAwait(false);
            return;
        }

        // M6 S2 (design note §3): "the re-verify path is REQUIRED" — a step-into may
        // have just pushed the FIRST frame for a module that had pending (unresolved)
        // breakpoints; re-map them now instead of waiting for a fresh setBreakpoints.
        if (kind == StepKind.Into)
        {
            await ReverifyPendingBreakpointsAsync().ConfigureAwait(false);
        }

        // `next` always stops after exactly one step regardless of exception filters
        // (filters only gate whether `continue` keeps going) — but the STOP REASON
        // should still say "exception"/"pause" when that's what actually happened, so
        // the client highlights it correctly.
        if (_liveSession.Session.LastStep.Disposition == StepDisposition.EngineAttention)
        {
            PublishSnapshotAndPause(epoch);
        }
        else if (IsExceptionDisposition(_liveSession.Session.LastStep.Disposition))
        {
            PublishSnapshotAndStopException(epoch);
        }
        else
        {
            PublishSnapshotAndStop(epoch, reasonOnStop);
        }
    }

    // M6 S2 (design note §3): the "re-verify path is REQUIRED" clause — a breakpoint
    // set in a callee's virtual doc BEFORE any step-into resolves the moment that
    // callee's first frame pushes, without waiting for a fresh setBreakpoints call
    // (VS Code persists virtual-doc breakpoints across sessions, so this is the ONLY
    // re-verify trigger for them within a single running session). M7 (§5.1/§5.2):
    // a real-file breakpoint set before this module was EVER touched had nothing to
    // key on but the file path (unlike a tsqldbg: URI, which names schema.name
    // directly) — now that this push resolved (or already-cached-resolved) module
    // -> file via its blueprint fetch, promote it the same way, through the same
    // shared helper.
    private async Task ReverifyPendingBreakpointsAsync()
    {
        var module = _liveSession?.Session.TopFrame?.Module;
        if (module is null)
        {
            return;
        }

        if (_pendingBreakpointsByModule.TryGetValue(module, out var pendingByModule) && pendingByModule.Count > 0)
        {
            await PromotePendingBreakpointsAsync(
                module, pendingByModule, new Source { Name = module.Display, Path = BuildVirtualDocPath(module) }).ConfigureAwait(false);
            _pendingBreakpointsByModule.Remove(module);
        }

        if (_liveSession!.Session.TryGetSourceMapFile(module, out var file) && file is not null
            && _pendingBreakpointsByFile.TryGetValue(file, out var pendingByFile) && pendingByFile.Count > 0)
        {
            await PromotePendingBreakpointsAsync(
                module, pendingByFile, new Source { Name = Path.GetFileName(file), Path = file }).ConfigureAwait(false);
            _pendingBreakpointsByFile.Remove(file);
        }
    }

    // Shared by both pending flavors above — the only difference between a
    // tsqldbg: URI's pending-by-module entry and a real file's pending-by-file one
    // is what Source to publish the `breakpoint changed` event with. Merges into
    // any ALREADY-mapped entries for this module (never clobbers — the two pending
    // flavors can both resolve to the SAME module across two separate
    // setBreakpoints calls, e.g. a tsqldbg: doc and its sourceMap-matched real file
    // both open with breakpoints).
    private async Task PromotePendingBreakpointsAsync(
        Core.Interpreter.ModuleIdentity module, List<PendingBreakpoint> pending, Source source)
    {
        var (index, _) = await _liveSession!.Session.TryGetModuleIndexAsync(module).ConfigureAwait(false);
        if (index is null)
        {
            return;
        }

        var mapped = _breakpointsByModule.TryGetValue(module, out var existing)
            ? new Dictionary<int, BreakpointState>(existing)
            : new Dictionary<int, BreakpointState>();
        foreach (var p in pending)
        {
            if (!index.TryMapBreakpointLine(p.RequestedLine, out var unit))
            {
                continue;
            }

            mapped[unit.Span.StartLine] = new BreakpointState
            {
                Id = p.Id,
                Line = unit.Span.StartLine,
                Condition = string.IsNullOrWhiteSpace(p.Condition) ? null : p.Condition,
                HitCountFilter = HitCountFilter.Parse(p.HitCondition),
                LogMessage = string.IsNullOrEmpty(p.LogMessage) ? null : p.LogMessage,
            };
            Protocol.SendEvent(new BreakpointEvent(BreakpointEvent.ReasonValue.Changed, new Breakpoint
            {
                Id = p.Id,
                Verified = true,
                Line = unit.Span.StartLine,
                Source = source,
            }));
        }

        if (mapped.Count > 0)
        {
            _breakpointsByModule[module] = mapped;
        }
    }

    private Task ContinueLockedAsync(int epoch) => RunUntilAsync(epoch, () => true, StoppedEvent.ReasonValue.Step);

    // M4 (§6/§11, design notes D9): "stepOut is the adapter's continue-until-depth-
    // shrinks loop, no session verb needed." Plain StepKind.Over throughout (stepping
    // OUT never wants to step further IN); a pop that lands back at or above the entry
    // depth is the boundary. Breakpoints/exceptions/pause hit along the way still stop,
    // same as continue.
    private Task StepOutLockedAsync(int epoch)
    {
        if (_liveSession is null)
        {
            return Task.CompletedTask;
        }

        var entryDepth = _liveSession.Session.Frames.Count;
        return RunUntilAsync(epoch, () => _liveSession.Session.Frames.Count >= entryDepth, StoppedEvent.ReasonValue.Step);
    }

    // Shared by continue and stepOut: steps (StepKind.Over) while `keepGoing()` holds,
    // honoring breakpoints, the stop-before-COMMIT confirmation, exception filters, and
    // pause identically either way. `boundaryReason` publishes only when the loop exits
    // because `keepGoing()` went false (continue's predicate never does).
    private async Task RunUntilAsync(int epoch, Func<bool> keepGoing, StoppedEvent.ReasonValue boundaryReason)
    {
        if (_liveSession is null)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        _pauseCts = cts;
        try
        {
            // DESIGN §6 continue: "loop next internally without publishing stops, until:
            // a breakpoint SU is reached (check before executing it) ... or frame-0 end."
            // Bug caught live (docs/archive/reviews/m1-breakpoint-smoketest-trace.jsonl): checking
            // Current against the breakpoint set on *every* iteration means a `continue`
            // that resumes from a just-published breakpoint stop re-matches the same SU
            // immediately and never advances — the check must be skipped exactly once for
            // the unit that was just stopped on (it was already "reached and shown" by the
            // previous continue; the user asking to continue past it means execute it,
            // then resume checking from the *next* unit). The same skip-once gate also
            // covers §10.4's stop-before-COMMIT confirmation below.
            while (!_liveSession.Session.IsCompleted && keepGoing())
            {
                // A54 (§6/§11.5): a MODULE frame parked at its implicit return (its last body
                // SU ran off the end, no explicit RETURN). Under continue/stepOut this is NOT
                // a stop (§6 — like the A44 GO boundary): consume it (the deferred §11.5 pop)
                // and keep going. `Session.Current` is null while parked, so this MUST run
                // before the breakpoint check below reads it. stepOut's depth predicate then
                // exits one pop later, in the caller; continue runs on. next/stepIn DO stop
                // here — but that is StepOnceLockedAsync, not this run-until loop.
                if (_liveSession.Session.AtImplicitReturn)
                {
                    try
                    {
                        var (_, returnMessages) = await _liveSession.Session.StepAsync(StepKind.Over, cts.Token).ConfigureAwait(false);
                        EmitStepMessages(returnMessages);
                    }
                    catch (OperationCanceledException)
                    {
                        PublishSnapshotAndPause(epoch);
                        return;
                    }
                    catch (Exception ex)
                    {
                        await ReportFaultAndEndLockedAsync(ex).ConfigureAwait(false);
                        return;
                    }

                    continue;
                }

                var current = _liveSession.Session.Current!;
                var skipCheck = ReferenceEquals(current, _lastBreakpointStopUnit);
                _lastBreakpointStopUnit = null;

                // M6 item 6 (B9/B1): boost dispatch happens BEFORE the per-SU breakpoint/
                // COMMIT checks below. BoostPlanner's isBlocked walk (IsSuBlocked, root
                // included) already refuses whenever any member SU — or the IF/WHILE root
                // itself — carries a breakpoint or logpoint, so a refusal (null) always
                // falls through to the per-SU path unchanged; a fired boost skips both the
                // breakpoint check and the plain StepAsync call for this iteration.
                bool boosted;
                try
                {
                    var boostResult = await _liveSession.Session.TryStepBoostedAsync(IsSuBlocked, cts.Token).ConfigureAwait(false);
                    boosted = boostResult is not null;
                    if (boostResult is { } br)
                    {
                        // M7 FYI(a): a boosted subtree's own console notes, forwarded
                        // before the disposition-based publishes at the loop's tail.
                        // A50: and its own result sets.
                        EmitStepMessages(br.Messages);
                        EmitStepResultSets(br.ResultSets);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Organizer review (docs/archive/reviews/m6-item6-adapter-boost-wiring-sonnet-
                    // escalation.md): catch-symmetry with the plain StepAsync path below.
                    // B7 (HandleBoostedBatchDeathAsync) classifies a cancelled in-flight
                    // boosted batch as EngineAttention internally when it surfaces as a
                    // StatementExecutionException, but SqlClient can ALSO surface token
                    // cancellation as a raw OperationCanceledException instead -- without
                    // this catch, that transport variant would fall through to the
                    // catch(Exception) below and END the session via
                    // ReportFaultAndEndLockedAsync instead of publishing a pause.
                    PublishSnapshotAndPause(epoch);
                    return;
                }
                catch (Exception ex)
                {
                    await ReportFaultAndEndLockedAsync(ex).ConfigureAwait(false);
                    return;
                }

                if (!boosted && !skipCheck)
                {
                    // M4/M6 (§13, per-module store, A22): only consult the TOP frame's
                    // OWN module's breakpoints — a stepped-into different procedure's
                    // coincidental line numbers must never spuriously match. A recursive
                    // call into that SAME module still matches (ModuleIdentity equality).
                    var topModule = _liveSession.Session.TopFrame?.Module;
                    if (topModule is not null
                        && _breakpointsByModule.TryGetValue(topModule, out var moduleBreakpoints)
                        && moduleBreakpoints.TryGetValue(current.Span.StartLine, out var breakpoint))
                    {
                        if (breakpoint.LogMessage is { } logMessage)
                        {
                            // M6 G1 (§13, A23): qualification is IDENTICAL to a plain
                            // breakpoint's (condition-false neither logs nor counts) —
                            // logpoints never stop and never touch the skip-once gate.
                            if (await ShouldBreakAsync(breakpoint).ConfigureAwait(false))
                            {
                                await EmitLogpointAsync(logMessage).ConfigureAwait(false);
                            }
                        }
                        else if (await ShouldBreakAsync(breakpoint).ConfigureAwait(false))
                        {
                            _lastBreakpointStopUnit = current;
                            PublishSnapshotAndStop(epoch, StoppedEvent.ReasonValue.Breakpoint);
                            return;
                        }
                    }

                    if (current.SubKind == Core.Interpreter.SuSubKind.Commit)
                    {
                        // §10.4: "detect COMMIT SUs ... stop-before with a confirmation
                        // prompt at runtime." DAP has no native confirm dialog — stop
                        // exactly once (skip-once gate, same as a breakpoint) with a
                        // console explanation; continuing again executes it for real.
                        // M4-gate carry-over: IsTransactionDetached sharpens the wording
                        // — while detached there is no live safety transaction to commit
                        // at all, so the COMMIT will faithfully fault with native 3902
                        // rather than commit anything.
                        _lastBreakpointStopUnit = current;
                        var message = _liveSession.Session.IsTransactionDetached
                            ? $"About to execute COMMIT at line {current.Span.StartLine} — no safety transaction is " +
                              "currently open (detached, §10.4); this will natively fail with error 3902, faithfully " +
                              "— nothing commits."
                            : $"About to execute COMMIT at line {current.Span.StartLine} (§10.4 policy violation on a " +
                              "rollback-mode session) — this commits the DEBUGGER'S OWN safety transaction, durably: " +
                              "all writes so far become permanent. Continue again to confirm, or Jump to Cursor to " +
                              "skip it.";
                        Protocol.SendEvent(new OutputEvent($"{message}\n") { Category = OutputEvent.CategoryValue.Console });
                        PublishSnapshotAndStop(epoch, StoppedEvent.ReasonValue.Breakpoint);
                        return;
                    }
                }

                if (!boosted)
                {
                    try
                    {
                        var (stepSets, stepMessages) = await _liveSession.Session.StepAsync(StepKind.Over, cts.Token).ConfigureAwait(false);
                        // M7 FYI(a): forward this step's console notes before the
                        // disposition-based publishes at the loop's tail. A50: then the
                        // statement's own result sets.
                        EmitStepMessages(stepMessages);
                        EmitStepResultSets(stepSets);
                    }
                    catch (OperationCanceledException)
                    {
                        PublishSnapshotAndPause(epoch);
                        return;
                    }
                    catch (Exception ex)
                    {
                        await ReportFaultAndEndLockedAsync(ex).ConfigureAwait(false);
                        return;
                    }
                }

                if (_liveSession.Session.LastStep.Disposition == StepDisposition.EngineAttention)
                {
                    PublishSnapshotAndPause(epoch);
                    return;
                }

                // M8/A44 (§5.4/§6): a GO boundary just crossed — either a NORMAL batch
                // completion (folded into the step that executed the batch's last SU,
                // Session.AdvanceAndSettleAsync) or a batch-terminal fault's
                // continuation (Session.PendingBatchAdvance, consumed by the step
                // immediately after the fault-site stop). Under `continue`/`stepOut` this
                // is deliberately NOT a stop: §6 defines `continue` as running until a
                // breakpoint / exception-filter stop / frame-0 end / pause, and a GO
                // boundary is none of those (the M8 design note §8.2 — "or continues if
                // in continue/boost mode"; RunToEndAsync at Session.cs:3003 already treats
                // BatchCompleted as keep-going). `BatchCompleted` therefore falls through
                // as a keep-going disposition — the loop advances into the next batch and
                // only halts at a real breakpoint (a Run-to-Cursor temp breakpoint is one),
                // an exception, or session end. The boundary stays observable without
                // halting: AdvanceToNextBatchAsync's "-- GO: entering batch k of N ..."
                // console note was already forwarded by EmitStepMessages above. `next`/
                // `stepIn` DO stop at a batch entry — but that is StepOnceLockedAsync's
                // single-step "publish and stop", not this run-until loop.
                // (A44, 2026-07-12 — corrects an M8 over-stop that intercepted Run to
                // Cursor / continue-to-a-later-batch breakpoint at the next batch's first
                // line; docs/archive/reviews/multibatch-continue-boundary-opus.md.)

                // §10.6: FaultAtSite (the 'all' filter's first phase) and FrameFaulted
                // (terminal) always stop; RoutedToCatch/UnhandledContinued stop only if
                // their filter is on — otherwise the loop keeps going exactly like
                // native execution would.
                if (TryShouldStopForExceptionFilters(_liveSession.Session.LastStep.Disposition))
                {
                    PublishSnapshotAndStopException(epoch);
                    return;
                }
            }

            if (_liveSession.Session.IsCompleted)
            {
                await EndSessionLockedAsync().ConfigureAwait(false);
            }
            else
            {
                // keepGoing() went false — e.g. stepOut's depth boundary was crossed.
                PublishSnapshotAndStop(epoch, boundaryReason);
            }
        }
        finally
        {
            _pauseCts = null;
        }
    }

    // M6 item 6 (B1's isBlocked parameter): the adapter's per-module breakpoint store,
    // exposed to BoostPlanner as the "does a breakpoint/logpoint bind to this line"
    // predicate over member SUs — root included, so a bound IF/WHILE itself refuses
    // boost via the SAME whitelist walk, never a separate adapter-side check.
    private bool IsSuBlocked(Core.Interpreter.StatementUnit unit)
    {
        var topModule = _liveSession?.Session.TopFrame?.Module;
        return topModule is not null
            && _breakpointsByModule.TryGetValue(topModule, out var moduleBreakpoints)
            && moduleBreakpoints.ContainsKey(unit.Span.StartLine);
    }

    private static bool IsExceptionDisposition(StepDisposition disposition) => disposition is
        StepDisposition.FaultAtSite or StepDisposition.RoutedToCatch or
        StepDisposition.UnhandledContinued or StepDisposition.FrameFaulted or
        StepDisposition.DoomedTempPreflight;   // A14: pre-flight C23 diagnostic at the SU (§10.4)

    private bool TryShouldStopForExceptionFilters(StepDisposition disposition) => disposition switch
    {
        StepDisposition.FaultAtSite => true,                     // 'all' filter caused this — always stop
        StepDisposition.FrameFaulted => true,                    // terminal — always stop
        StepDisposition.RoutedToCatch => _stopOnCaughtErrors,
        StepDisposition.UnhandledContinued => _stopOnUnhandledErrors,
        StepDisposition.DoomedTempPreflight => true,             // A14: not filter-gated — the ratified
                                                                 // text says interactive runs stop here;
                                                                 // continue after the stop executes anyway
        _ => false,
    };

    private void PublishExceptionStopped()
    {
        var error = _liveSession?.Session.LastStep.Error;
        SendEvent(new StoppedEvent(StoppedEvent.ReasonValue.Exception)
        {
            ThreadId = 1,
            Description = error is not null ? $"Msg {error.Number}, Level {error.Severity}: {error.Message}" : null,
            Text = error?.Message,
        }, $"stopped reason=exception number={error?.Number} severity={error?.Severity}");
    }

    private void PublishPaused()
    {
        Protocol.SendEvent(new OutputEvent(
            "Paused (§10.5) — the in-flight statement was cancelled; cursor unchanged, retry (next/continue) or Jump to Cursor.\n")
        { Category = OutputEvent.CategoryValue.Console });
        SendEvent(new StoppedEvent(StoppedEvent.ReasonValue.Pause) { ThreadId = 1 }, "stopped reason=pause");
    }

    // DESIGN §13: condition first (debugger-initiated eval via Session.EvaluateConditionAsync
    // — touches no shadow state, §6 M2 D3), then hit count. "A faulting condition =
    // console warning + break anyway."
    private async Task<bool> ShouldBreakAsync(BreakpointState breakpoint)
    {
        if (breakpoint.Condition is not null)
        {
            var (value, faultMessage) = await _liveSession!.Session.EvaluateConditionAsync(breakpoint.Condition).ConfigureAwait(false);
            if (faultMessage is not null)
            {
                Protocol.SendEvent(new OutputEvent($"Breakpoint condition warning: {faultMessage}\n")
                {
                    Category = OutputEvent.CategoryValue.Stderr,
                });
                return true;
            }

            if (value != true)
            {
                return false;
            }
        }

        if (breakpoint.HitCountFilter is { } filter)
        {
            breakpoint.HitCount++;
            return filter.ShouldStop(breakpoint.HitCount);
        }

        return true;
    }

    // M6 G1/G2 (design note §4, A23): interpolate {expr} segments (non-nested braces,
    // v1 — no escape syntax, mirroring VS Code's own debuggers) and emit as console
    // output. Session.EvaluateLogExpressionsAsync owns the one-round-trip-with-
    // per-expr-fallback contract — this method is pure template stitching.
    private async Task EmitLogpointAsync(string logMessage)
    {
        var segments = ParseLogMessage(logMessage);
        var expressionTexts = segments.Where(s => s.IsExpression).Select(s => s.Text).ToList();
        if (expressionTexts.Count == 0)
        {
            Protocol.SendEvent(new OutputEvent($"{logMessage}\n") { Category = OutputEvent.CategoryValue.Console });
            return;
        }

        var values = await _liveSession!.Session.EvaluateLogExpressionsAsync(expressionTexts).ConfigureAwait(false);
        var sb = new StringBuilder();
        var valueIndex = 0;
        foreach (var segment in segments)
        {
            sb.Append(segment.IsExpression ? values[valueIndex++] : segment.Text);
        }

        sb.Append('\n');
        Protocol.SendEvent(new OutputEvent(sb.ToString()) { Category = OutputEvent.CategoryValue.Console });
    }

    private readonly record struct LogMessageSegment(bool IsExpression, string Text);

    // DESIGN §13/A23: "{expr} segments ... non-nested braces, v1; no escape syntax".
    // An unterminated '{' is treated as literal text for the rest of the message —
    // never a parse error (a logpoint template is user-typed free text, not T-SQL).
    private static List<LogMessageSegment> ParseLogMessage(string logMessage)
    {
        var segments = new List<LogMessageSegment>();
        var i = 0;
        while (i < logMessage.Length)
        {
            var open = logMessage.IndexOf('{', i);
            if (open < 0)
            {
                segments.Add(new LogMessageSegment(false, logMessage[i..]));
                break;
            }

            if (open > i)
            {
                segments.Add(new LogMessageSegment(false, logMessage[i..open]));
            }

            var close = logMessage.IndexOf('}', open + 1);
            if (close < 0)
            {
                segments.Add(new LogMessageSegment(false, logMessage[open..]));
                break;
            }

            segments.Add(new LogMessageSegment(true, logMessage[(open + 1)..close]));
            i = close + 1;
        }

        return segments;
    }

    private async Task ReportFaultAndEndLockedAsync(Exception ex)
    {
        _trace.Event("session.fault", ex.ToString());
        Protocol.SendEvent(new OutputEvent($"{ex.Message}\n") { Category = OutputEvent.CategoryValue.Stderr });
        await EndSessionLockedAsync().ConfigureAwait(false);
    }

    private async Task EndSessionLockedAsync()
    {
        // M5 I1/I2: the session is ending without a normal stop to publish through —
        // nothing is left to preempt, so just clear the running flag directly.
        _executor.MarkIdle();
        if (_liveSession is not null)
        {
            await _liveSession.DisposeAsync().ConfigureAwait(false);
            _liveSession = null;
        }

        SendEvent(new TerminatedEvent(), "terminated");
    }

    // M5 I1 (design note §2, A15): builds the eager, client-side-only StopSnapshot
    // for `epoch` from the live Session — frame list, per-frame variable catalogs,
    // and the active error context display. Called once per stop, right before the
    // corresponding `stopped` event, from whichever thread settled that stop.
    private StopSnapshot BuildSnapshot(int epoch)
    {
        if (_liveSession is null)
        {
            return StopSnapshot.Empty(epoch);
        }

        var session = _liveSession.Session;
        // M5 I3 (§12.1): every value here is already-tracked session state — no round
        // trip. Session-wide (not per-frame) except XactAbortOn (§7.2, frame env).
        var modeAnnotation = session.IsBroken ? "broken"
            : session.IsDoomed ? "doomed"
            : session.IsTransactionDetached ? "detached"
            : "healthy";
        var runtimeOptions = session.RuntimeOptionsSnapshot;
        var allFrames = session.Frames;

        var frames = new List<SnapshotFrame>(allFrames.Count);
        for (var i = 0; i < allFrames.Count; i++)
        {
            var f = allFrames[i];
            var systemScope = new SystemScopeValues(
                session.LastObservedTrancount, session.LastObservedXactState, session.Spid,
                f.XactAbortOn, runtimeOptions, modeAnnotation,
                f.SetEnv.QuotedIdentifier, f.SetEnv.AnsiNulls);  // A52: per-frame parse-time options
            var tempObjects = ResolveTempObjects(allFrames, i, session.IsDoomed, session.IsBroken);

            // A54 (§6/§11.5): while the TOP frame is parked at its implicit return its cursor
            // is completed (Current is null) — anchor the stop at the END of the module body
            // (the last body SU's end line, line-only) so VS Code marks "end of procedure"
            // instead of collapsing to line 0. Every other frame, and the top frame at a
            // normal stop, uses the current SU's span (A51 full-statement highlight).
            int line, column, endLine, endColumn;
            if (i == allFrames.Count - 1 && session.AtImplicitReturn)
            {
                var body = f.Cursor.Index.All;
                var (bodyEndLine, bodyEndColumn) = body.Count > 0 ? StatementEnd(body[^1]) : (0, 0);
                line = bodyEndLine;
                column = bodyEndColumn > 0 ? bodyEndColumn : 1;
                endLine = 0;   // line-only marker — StackFrame leaves EndLine/EndColumn unset
                endColumn = 0;
            }
            else
            {
                (endLine, endColumn) = StatementEnd(f.Cursor.Current);  // A51: full-statement highlight span
                line = f.Cursor.Current?.Span.StartLine ?? 0;
                column = f.Cursor.Current?.Span.StartColumn ?? 0;
            }

            frames.Add(new SnapshotFrame(
                f.Ordinal,
                f.Module,
                f.Module.Display,
                line,
                column,
                endLine,
                endColumn,
                f.Variables.All,
                systemScope,
                tempObjects,
                session.CurrentBatchIndex,     // M8 (§5.4): session-level; only the script batch frame renders it
                session.BatchCount,
                session.CurrentBatchIteration, // §5.4/A43: `GO N` iteration/repeat for the `×i/M` suffix
                session.CurrentBatchRepeat));
        }

        var activeError = session.ActiveErrorContext?.Values;
        var errorDisplay = activeError is null
            ? null
            : new ErrorContextDisplay(
                activeError.Number, activeError.Severity, activeError.State,
                activeError.Line, activeError.Procedure, activeError.Message);

        return new StopSnapshot(epoch, frames, errorDisplay);
    }

    // A51 (§13): the (endLine,endColumn) of a unit's FULL statement span, derived from the
    // byte-exact original source slice (unit.Text) — StartColumn plus the slice's own line
    // breaks. VS Code highlights (line,column)→(endLine,endColumn) on the top frame, so
    // reporting the whole span boxes the entire statement about to execute (this debugger
    // steps by STATEMENT, not by line), instead of just its first line. Columns are 1-based
    // like ScriptDom/DAP; a source line after a newline restarts at column 1. Null cursor
    // (completed/edge) collapses to (0,0), matching the (Line,Column) fallback above.
    private static (int EndLine, int EndColumn) StatementEnd(Core.Interpreter.StatementUnit? unit)
    {
        if (unit is null)
        {
            return (0, 0);
        }

        var text = unit.Text;
        var startLine = unit.Span.StartLine;
        var lastNewline = text.LastIndexOf('\n');
        if (lastNewline < 0)
        {
            return (startLine, unit.Span.StartColumn + text.Length);
        }

        var newlineCount = 0;
        foreach (var ch in text)
        {
            if (ch == '\n')
            {
                newlineCount++;
            }
        }

        // The last source line begins at column 1; its length is the chars after the final
        // '\n'. endColumn is one past the last char (any trailing '\r' lives on earlier lines).
        return (startLine + newlineCount, text.Length - lastNewline);
    }

    // M5 I4 (§12.2/§9): for DAP frame at list-index `frameIndex`, walk the frame chain
    // INNERMOST-first (frameIndex, frameIndex-1, ..., 0) over live (!IsDead) registry
    // entries, deduping by original name — the innermost live entry wins, exactly what
    // R2 resolution would let that frame's code touch. Within one frame, the MOST
    // RECENT entry for a name wins too (a dropped-and-recreated #temp), mirroring
    // TempObjectRegistry.TryResolve's own reverse scan.
    private static IReadOnlyList<TempObjectProjection> ResolveTempObjects(
        IReadOnlyList<Core.Interpreter.Frame> allFrames, int frameIndex, bool isDoomed, bool isBroken)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<TempObjectProjection>();
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

                result.Add(new TempObjectProjection(
                    entry.OriginalName, entry.PhysicalName, entry.Kind, entry.CreatedAtTrancount,
                    EagerTempDisplay(entry, isDoomed, isBroken)));
            }
        }

        return result;
    }

    // M5 I4: the "A14 certainty argument, reused client-side" — doomed/broken values
    // are known with certainty from registry facts alone (CreatedAtTrancount, kind),
    // NEVER from probing (a probe would 208 or touch doomed re-materialization).
    // Null means healthy/detached: the caller must fall back to the lazy fill.
    private static string? EagerTempDisplay(Core.Interpreter.TempObjectEntry entry, bool isDoomed, bool isBroken)
    {
        if (isBroken)
        {
            return "(session terminated)";
        }

        if (isDoomed)
        {
            return entry.Kind switch
            {
                Core.Interpreter.TempObjectKind.TempTable when entry.CreatedAtTrancount >= 1 =>
                    "(destroyed by the doomed transaction's forced rollback — C23)",
                Core.Interpreter.TempObjectKind.TableVariable =>
                    "(0 rows — contents lost across rollback, C25)",
                Core.Interpreter.TempObjectKind.Cursor => "(cursor)",
                _ => null,
            };
        }

        return null;
    }

    private void PublishSnapshotAndStop(int epoch, StoppedEvent.ReasonValue reason)
    {
        _executor.PublishSnapshot(BuildSnapshot(epoch));
        PublishStopped(reason);
    }

    private void PublishSnapshotAndStopException(int epoch)
    {
        _executor.PublishSnapshot(BuildSnapshot(epoch));
        PublishExceptionStopped();
    }

    private void PublishSnapshotAndPause(int epoch)
    {
        _executor.PublishSnapshot(BuildSnapshot(epoch));
        PublishPaused();
    }

    private void PublishStopped(StoppedEvent.ReasonValue reason)
    {
        SendEvent(new StoppedEvent(reason) { ThreadId = 1 }, $"stopped reason={reason}");
    }

    // M2-gate follow-up (docs/archive/reviews/m2-gate-review-fable.md §5.1): "DAP requests
    // aren't traced ... add dap.request/dap.event categories" — every Handle*Request
    // override logs "dap.request" at entry (this file); every protocol-level DAP event
    // (stopped/terminated/initialized) funnels through here for "dap.event", now that
    // M3's exception stops make the gap actively painful to reconstruct from a trace.
    // OutputEvent console/stderr notes are not wrapped here — their own text already
    // carries the same information plainly.
    private void SendEvent(DebugEvent evt, string summary)
    {
        _trace.Event("dap.event", summary);
        Protocol.SendEvent(evt);
    }

    private static SessionOptions BuildSessionOptions(LaunchConfig config)
    {
        string? scriptText = null;
        if (config.Mode == LaunchMode.Script)
        {
            // A60: an unsaved/untitled (or dirty) buffer arrives as inline `scriptText` — debug it
            // verbatim, never touching the disk (ScriptPath is then only a Source hint, e.g. an
            // untitled: URI). Otherwise fall back to reading the named file.
            if (config.ScriptText is not null)
            {
                scriptText = config.ScriptText;
            }
            else if (!string.IsNullOrWhiteSpace(config.ScriptPath))
            {
                scriptText = File.ReadAllText(config.ScriptPath);
            }
            else
            {
                throw new ProtocolException("launch config 'script' (or 'scriptText') is required when mode = script.");
            }
        }

        return new SessionOptions(
            config.Server,
            config.Database,
            config.Mode,
            config.Procedure,
            config.Args,
            scriptText,
            config.CompatLevel,
            config.CommandTimeoutSeconds,
            config.WaitFor,
            config.TempTablePageSize,
            config.DisplayValueChars,
            config.AllowConsoleWrites,
            config.ConsoleTimeoutSeconds,
            config.MaxConsoleRows,
            config.WatchBudgetMs,
            config.Boost,
            config.ExecuteAs,
            config.CommitMode,
            config.SourceMap,
            config.AuthType,
            config.SqlUser,
            config.Encrypt,
            config.ConnectionOptions);
    }
}

// M6 S1 (design note §3, A22): a custom DAP command needs its own Request/Args/
// Response triple registered via Protocol.RegisterRequestType — this SDK dispatches
// ONLY pre-registered command names (confirmed by decompiling DebugProtocolClient:
// every standard command is wired the same way in DebugAdapterBase's own
// InitializeProtocolClient); an unregistered command is silently dropped, never
// reaching HandleProtocolRequest at all.
public sealed class TsqldbgSourceArguments
{
    [Newtonsoft.Json.JsonProperty("path")]
    public string? Path { get; set; }
}

public sealed class TsqldbgSourceRequest : DebugRequestWithResponse<TsqldbgSourceArguments, TsqldbgSourceResponseBody>
{
    public const string RequestType = "tsqldbg_source";

    public TsqldbgSourceRequest() : base(RequestType)
    {
    }
}

// The extension's TextDocumentContentProvider reads `body.content` (see
// extension/src/extension.ts). ResponseBody subclasses need explicit [JsonProperty]:
// this SDK's ProtocolObject base does not camelCase C# property names automatically.
public sealed class TsqldbgSourceResponseBody : ResponseBody
{
    [Newtonsoft.Json.JsonProperty("content")]
    public string Content { get; set; } = string.Empty;
}

// M7 (§5.2/§16, commit-modal): the adapter-initiated half of the confirmation round
// trip — a custom EVENT (adapter -> client), since DAP has no built-in "ask the user
// something" mechanism ("a DAP runInTerminal-free mechanism", §16). DebugEvent's one
// constructor takes the event's wire name; Type itself is [JsonIgnore]'d by the base
// (becomes the envelope's "event" field) so only these three properties serialize
// into "body" — same shape OutputEvent/StoppedEvent use.
public sealed class TsqldbgCommitConfirmEvent : DebugEvent
{
    public const string EventType = "tsqldbg_commitConfirm";

    public TsqldbgCommitConfirmEvent(string server, string database, string env) : base(EventType)
    {
        Server = server;
        Database = database;
        Env = env;
    }

    [Newtonsoft.Json.JsonProperty("server")]
    public string Server { get; }

    [Newtonsoft.Json.JsonProperty("database")]
    public string Database { get; }

    [Newtonsoft.Json.JsonProperty("env")]
    public string Env { get; }
}

// The extension's reply — a custom REQUEST (client -> adapter), same registration
// shape as tsqldbg_source's existing custom request.
public sealed class TsqldbgCommitDecisionArguments
{
    [Newtonsoft.Json.JsonProperty("commit")]
    public bool Commit { get; set; }
}

public sealed class TsqldbgCommitDecisionRequest : DebugRequestWithResponse<TsqldbgCommitDecisionArguments, TsqldbgCommitDecisionResponseBody>
{
    public const string RequestType = "tsqldbg_commitDecision";

    public TsqldbgCommitDecisionRequest() : base(RequestType)
    {
    }
}

public sealed class TsqldbgCommitDecisionResponseBody : ResponseBody
{
}
