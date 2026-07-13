namespace TsqlDbg.Core.Sessions;

// DESIGN §6/§22 M2: "WAITFOR DELAY/TIME: intercepted; Debug Console note + configurable
// behavior (waitfor: "skip" | "honor", default skip)."
public enum WaitForMode { Skip, Honor }

// DESIGN §16/§17 (A39, option A): "commitMode:"rollback" (default; teardown always rolls
// back) or "commit". On the interactive surface commit is authorized by the terminate
// modal alone (no allowWrites pre-gate — A38/A39); the programmatic surface additionally
// requires target allowWrites:true. Rollback stays the unconditional default everywhere
// except that one ratified path (CLAUDE.md safety rule 7) — see Session.TeardownAsync.
public enum CommitMode { Rollback, Commit }

// DESIGN §4/§16 (A41): connection auth for the Connection Manager (M10). Integrated =
// Windows/SSPI (default, no password); Sql = SQL login (UserID + Password, the password
// supplied out-of-band via the adapter's TSQLDBG_SQL_PASSWORD env — never in SessionOptions).
public enum AuthType { Integrated, Sql }

// DESIGN §17 launch.json subset needed through M2+.
public sealed record SessionOptions(
    string Server,
    string Database,
    LaunchMode Mode,
    string? Procedure,
    IReadOnlyDictionary<string, string>? Args,
    string? ScriptText,
    int CompatLevel = 150,
    int CommandTimeoutSeconds = 300,
    WaitForMode WaitFor = WaitForMode.Skip,
    // DESIGN §15 / M5 I4 (§12.2 Temp Tables scope paging): rows per page for the
    // OFFSET/FETCH shape.
    int TempTablePageSize = 50,
    // DESIGN §15 / M5 I4 (§7.5-style truncation for temp-table row cells, client-side
    // since these values come from a raw SELECT, not the state-table CONVERT path).
    int DisplayValueChars = 256,
    // DESIGN §12.3 / M5 I6: REPL write-mode gate — "unless launch allowConsoleWrites:
    // true (then any DML/DDL, still inside the session transaction)." This Core library
    // default stays false (safe-by-default); the PRODUCT default is true, applied at the
    // adapter config boundary (LaunchConfig) — DESIGN §16, flipped 2026-07-13.
    bool AllowConsoleWrites = false,
    // DESIGN §15 / M5 I6: the console's own CommandTimeout (distinct from the
    // per-step commandTimeoutSec) — enforced by the caller (the DAP layer) via a
    // linked CancellationTokenSource, not inside Session itself.
    int ConsoleTimeoutSeconds = 30,
    // DESIGN §15 / M5 I6: REPL result-set row cap ("200 of N — refine your query").
    int MaxConsoleRows = 200,
    // DESIGN §15 / M5 I7 (§12.4 watch): the shared per-stop budget before remaining
    // watches show "⏱ (click to evaluate)" — enforced by the DAP layer (StopSnapshot's
    // per-epoch stopwatch), not inside Session itself.
    int WatchBudgetMs = 2000,
    // DESIGN §14/A21: boost — off by default until the fidelity corpus passes with it
    // enabled (§15/§22). The Fable boost core merged (M6 item 6): Session.
    // TryStepBoostedAsync consults this flag directly (BoostSessionGate/BoostPlanner),
    // and the adapter's continue loop dispatches through it per SU via the B1
    // IsSuBlocked predicate (TsqlDbgDebugSession.RunUntilAsync) before falling back to
    // interpreted stepping.
    bool Boost = false,
    // DESIGN §16/§21 C4 (M7): ownership-chaining / EXECUTE AS identity caveat —
    // the clause text placed after "EXECUTE AS" (e.g. "USER = 'AppUser'"), emitted
    // once at session init (right after §4 step 2) and REVERTed in teardown before
    // the rollback/commit decision. Null (default) = no impersonation, unchanged
    // behavior. Previously honestly-omitted (D4 register audit); now consumed.
    string? ExecuteAs = null,
    // DESIGN §16/§17 (A39, option A): default Rollback — teardown is unconditional
    // rollback everywhere except the ONE ratified path: an explicit DAP `terminate`
    // with commitMode=Commit plus an adapter-supplied, extension-confirmed decision
    // callback passed to TeardownAsync. On the interactive surface the terminate modal
    // is the sole authorization (no allowWrites pre-gate, A38/A39); the programmatic
    // surface additionally requires the target's allowWrites:true.
    CommitMode CommitMode = CommitMode.Rollback,
    // DESIGN §5.2/§17 (M7 sourceMap hash-compare): glob patterns (already
    // ${workspaceFolder}-resolved by VS Code before the adapter ever sees them —
    // see SourceMapResolver) checked at each module's first blueprint fetch; a
    // CRLF-normalized byte-exact match binds breakpoints/stack-frame Source to
    // that real file instead of the tsqldbg: virtual document. Empty (default) =
    // unchanged behavior, no file I/O attempted at all.
    IReadOnlyList<string>? SourceMapGlobs = null,
    // DESIGN §4/§16 (A41, M10 Connection Manager): connection auth + options. AuthType
    // default Integrated (SSPI) = today's behavior; SqlUser is the login NAME only (never the
    // password — that arrives via the adapter's env channel). Encrypt:true -> Mandatory,
    // false (default) -> Optional. ConnectionOptions is an extra raw connection-string
    // fragment (profile encrypt/trust/options), appended like the target's options.
    AuthType AuthType = AuthType.Integrated,
    string? SqlUser = null,
    bool Encrypt = false,
    string? ConnectionOptions = null)
{
    /// <summary>Never null — callers iterate without a null-check.</summary>
    public IReadOnlyList<string> SourceMapGlobsOrEmpty => SourceMapGlobs ?? Array.Empty<string>();
}
