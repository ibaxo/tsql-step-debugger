using System.Text.Json.Serialization;
using TsqlDbg.Core.Sessions;

namespace TsqlDbg.Mcp;

// DESIGN §24.9: the per-session option subset an agent supplies on start_session / trace_*,
// mirroring the §17 launch schema that Core's SessionOptions consumes. Two surface-specific
// defaults differ from the adapter (§24.1): allowConsoleWrites defaults FALSE (safe-by-default,
// the Core library default) and commitMode defaults "rollback". compatLevel defaults 0 (auto,
// A57). Credentials never appear here — a SQL-auth password arrives only via the
// TSQLDBG_SQL_PASSWORD process env var (C27, §24.1(4)).
public sealed record SessionArgs(
    string Server,
    string Database,
    string Mode = "script",                     // "script" | "procedure"
    string? Procedure = null,
    IReadOnlyDictionary<string, string>? Args = null,
    string? Script = null,                       // inline script text (mode=script)
    string? ScriptPath = null,                   // OR a path to read (mode=script)
    string AuthType = "integrated",              // "integrated" | "sql"
    string? SqlUser = null,
    int CompatLevel = 0,                         // 0 = auto (A57)
    string CommitMode = "rollback",              // "rollback" | "commit"
    bool AllowConsoleWrites = false,             // §24.1(3): off by default on this surface
    bool Boost = false,
    string WaitFor = "skip",                     // "skip" | "honor"
    int CommandTimeoutSec = 300,
    string? ExecuteAs = null,
    IReadOnlyList<string>? SourceMap = null)
{
    // DESIGN §24.1(1)/§24.9 (M11 gate, CRITICAL): the agent MUST NOT be able to supply a raw
    // connection-string fragment or connection-level flags. SqlConnectionStringFactory appends
    // such fragments LAST, so an agent value like "Server=EVILSQL" would override the typed,
    // allowlisted DataSource — defeating the default-deny gate AND redirecting the
    // TSQLDBG_SQL_PASSWORD to an attacker-chosen server. These are therefore NOT tool arguments
    // (they are absent from §24.9's per-session list by design). Encryption/trust/options for an
    // allowlisted server belong in that server's targets.json entry (operator-controlled,
    // trusted), which the factory appends from TargetEntry.Options — never from agent input.
    public LaunchMode ParseMode() => string.Equals(Mode, "procedure", StringComparison.OrdinalIgnoreCase)
        ? LaunchMode.Procedure
        : LaunchMode.Script;

    private Core.Sessions.AuthType ParseAuthType() => string.Equals(AuthType, "sql", StringComparison.OrdinalIgnoreCase)
        ? Core.Sessions.AuthType.Sql
        : Core.Sessions.AuthType.Integrated;

    private CommitMode ParseCommitMode() => string.Equals(CommitMode, "commit", StringComparison.OrdinalIgnoreCase)
        ? Core.Sessions.CommitMode.Commit
        : Core.Sessions.CommitMode.Rollback;

    private WaitForMode ParseWaitFor() => string.Equals(WaitFor, "honor", StringComparison.OrdinalIgnoreCase)
        ? WaitForMode.Honor
        : WaitForMode.Skip;

    // [JsonIgnore]: computed helpers, not tool inputs — kept out of the generated tool schema so
    // an agent never tries to set them (M11 re-review N4).
    [JsonIgnore]
    public bool CommitModeRequested => ParseCommitMode() == Core.Sessions.CommitMode.Commit;

    [JsonIgnore]
    public bool IsSqlAuth => ParseAuthType() == Core.Sessions.AuthType.Sql;

    // DESIGN §24.9: build the Core SessionOptions. Script text is resolved from ScriptPath
    // when only a path was given (mirrors the adapter's BuildSessionOptions File.ReadAllText).
    public SessionOptions ToSessionOptions()
    {
        var mode = ParseMode();
        string? scriptText = Script;
        if (mode == LaunchMode.Script)
        {
            if (scriptText is null && !string.IsNullOrWhiteSpace(ScriptPath))
            {
                scriptText = File.ReadAllText(ScriptPath);
            }

            if (string.IsNullOrWhiteSpace(scriptText))
            {
                throw new ArgumentException("mode=script requires either 'script' (inline text) or 'scriptPath'.");
            }
        }
        else if (string.IsNullOrWhiteSpace(Procedure))
        {
            throw new ArgumentException("mode=procedure requires 'procedure' (e.g. 'dbo.uspProcessOrder').");
        }

        return new SessionOptions(
            Server,
            Database,
            mode,
            Procedure,
            Args,
            scriptText,
            CompatLevel,
            CommandTimeoutSec,
            ParseWaitFor(),
            AllowConsoleWrites: AllowConsoleWrites,
            Boost: Boost,
            ExecuteAs: ExecuteAs,
            CommitMode: ParseCommitMode(),
            SourceMapGlobs: SourceMap,
            AuthType: ParseAuthType(),
            SqlUser: SqlUser,
            // Encrypt / ConnectionOptions are deliberately NOT agent-supplied (see the record
            // header): the Core defaults hold, and a target's own targets.json `options` fragment
            // (operator-controlled) supplies encryption/trust for that allowlisted server.
            Encrypt: false,
            ConnectionOptions: null);
    }
}
