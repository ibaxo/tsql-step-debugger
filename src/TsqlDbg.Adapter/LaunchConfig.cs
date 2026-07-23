using Newtonsoft.Json.Linq;
using TsqlDbg.Core.Sessions;

namespace TsqlDbg.Adapter;

// DESIGN §17 launch.json schema, the subset relevant through M0. Parsed out of
// LaunchArguments.ConfigurationProperties (Dictionary<string, JToken> — confirmed
// against the installed 18.0.10427.1 package; see docs/package-versions.md).
public sealed record LaunchConfig(
    string Server,
    string Database,
    LaunchMode Mode,
    string? Procedure,
    IReadOnlyDictionary<string, string>? Args,
    string? ScriptPath,
    // DESIGN §17 (A60): inline script body for unsaved/untitled (or dirty) buffers. When
    // non-null it is debugged verbatim and the adapter never reads ScriptPath from disk;
    // ScriptPath then only feeds the stack-frame Source (which may be an untitled: URI).
    string? ScriptText,
    string? TargetsFile,
    string? WorkspaceFolder,
    int CompatLevel,
    int CommandTimeoutSeconds,
    bool StopOnEntry,
    WaitForMode WaitFor,
    int TempTablePageSize,
    int DisplayValueChars,
    bool AllowConsoleWrites,
    int ConsoleTimeoutSeconds,
    int MaxConsoleRows,
    int WatchBudgetMs,
    bool Boost,
    string? ExecuteAs,
    CommitMode CommitMode,
    IReadOnlyList<string> SourceMap,
    // DESIGN §16/§4 (A41): connection auth. Password is NEVER a field here — it reaches the
    // adapter only via the TSQLDBG_SQL_PASSWORD env var (set by the extension from SecretStorage).
    AuthType AuthType,
    string? SqlUser,
    bool Encrypt,
    string? ConnectionOptions,
    // DESIGN §12.3/§15 (A56): Debug Console verbosity. false (logLevel:"normal", default)
    // shows debuggee output/errors/results/logpoints/§16 notices only; true
    // (logLevel:"verbose") also surfaces the debugger's own diagnostic annotations
    // (NOCOUNT-forced, GO batch-entry, untracked SET options, DML-trigger heads-up).
    bool Verbose,
    // DESIGN §17 (A73): non-interactive trace-to-end mode. Null = normal interactive
    // launch; non-null = §24.3 Mode A on the DAP surface (no stops, console + §24.8 file).
    TraceRunConfig? TraceRun)
{
    public static LaunchConfig Parse(IDictionary<string, JToken> properties)
    {
        var server = RequireString(properties, "server");
        var database = RequireString(properties, "database");
        var modeText = GetString(properties, "mode") ?? "procedure";
        var mode = modeText.Equals("script", StringComparison.OrdinalIgnoreCase) ? LaunchMode.Script : LaunchMode.Procedure;

        IReadOnlyDictionary<string, string>? args = null;
        if (properties.TryGetValue("args", out var argsToken) && argsToken is JObject argsObject)
        {
            args = argsObject.Properties().ToDictionary(p => p.Name, p => p.Value.ToString());
        }

        // DESIGN §6/§22 M2: waitfor: "skip" | "honor", default skip.
        var waitForText = GetString(properties, "waitfor") ?? "skip";
        var waitFor = waitForText.Equals("honor", StringComparison.OrdinalIgnoreCase) ? WaitForMode.Honor : WaitForMode.Skip;

        // DESIGN §16/§17 (M7 commit-modal): default "rollback" — anything other than
        // an exact "commit" is the safe choice (CLAUDE.md safety rule 7), same
        // unrecognized-defaults-safe discipline as waitfor above.
        var commitModeText = GetString(properties, "commitMode") ?? "rollback";
        var commitMode = commitModeText.Equals("commit", StringComparison.OrdinalIgnoreCase)
            ? Core.Sessions.CommitMode.Commit
            : Core.Sessions.CommitMode.Rollback;

        // DESIGN §16/§4 (A41): "integrated" (default, SSPI) | "sql". Unrecognized -> integrated.
        var authType = (GetString(properties, "authType") ?? "integrated").Equals("sql", StringComparison.OrdinalIgnoreCase)
            ? Core.Sessions.AuthType.Sql
            : Core.Sessions.AuthType.Integrated;

        // DESIGN §12.3/§15 (A56): "normal" (default) | "verbose". Anything other than an
        // exact "verbose" is normal (quiet) — same unrecognized-defaults-quiet discipline
        // as waitfor/commitMode above.
        var verbose = (GetString(properties, "logLevel") ?? "normal").Equals("verbose", StringComparison.OrdinalIgnoreCase);

        return new LaunchConfig(
            server,
            database,
            mode,
            GetString(properties, "procedure"),
            args,
            GetString(properties, "script"),
            // DESIGN §17 (A60): inline body for unsaved buffers; null = read ScriptPath from disk.
            GetString(properties, "scriptText"),
            GetString(properties, "targetsFile"),
            GetString(properties, "workspaceFolder"),
            // DESIGN §2 (A57): product default 0 = auto (parser chosen from the server's product
            // version at connect). An explicit 150/160/170 pins the parser. The Core SessionOptions
            // library default stays 150 (safe, no probe) — same split-default idiom as
            // allowConsoleWrites (Core default false, product default true).
            GetInt(properties, "compatLevel") ?? 0,
            GetInt(properties, "commandTimeoutSec") ?? 300,
            GetBool(properties, "stopOnEntry") ?? true,
            waitFor,
            // DESIGN §15 defaults; M5 I4 is the first consumer.
            GetInt(properties, "tempTablePageSize") ?? 50,
            GetInt(properties, "displayValueChars") ?? 256,
            // DESIGN §12.3 / M5 I6 / §16. Product default TRUE (the interactive console is
            // writable out of the box, still inside the rolled-back session transaction).
            // Core's SessionOptions library default stays false (safe-by-default); the
            // product default is applied here, at the config boundary.
            GetBool(properties, "allowConsoleWrites") ?? true,
            GetInt(properties, "consoleTimeoutSec") ?? 30,
            GetInt(properties, "maxConsoleRows") ?? 200,
            // DESIGN §12.4 / M5 I7.
            GetInt(properties, "watchBudgetMs") ?? 2000,
            // DESIGN §14/§15: default false until fidelity pass 3 is green (M6 item 6).
            GetBool(properties, "boost") ?? false,
            // DESIGN §16/§21 C4 (M7): optional EXECUTE AS clause text.
            GetString(properties, "executeAs"),
            // DESIGN §16/§17 (M7 commit-modal).
            commitMode,
            // DESIGN §5.2/§17 (M7 sourceMap): already ${workspaceFolder}-resolved by
            // VS Code itself before this reaches the adapter (SourceMapResolver's
            // remarks) — only the filesystem-wildcard part is this adapter's job.
            GetStringArray(properties, "sourceMap"),
            // DESIGN §16/§4 (A41): connection auth (password NOT here — adapter env channel).
            authType,
            GetString(properties, "sqlUser"),
            GetBool(properties, "encrypt") ?? false,
            GetString(properties, "options"),
            // DESIGN §12.3/§15 (A56): console verbosity.
            verbose,
            // DESIGN §17 (A73): trace-run mode.
            ParseTraceRun(properties));
    }

    // DESIGN §17 (A73): `traceRun` is false (absent) | true (all defaults) | an options
    // object. stepMode follows the MCP arg's discipline (anything but an exact
    // case-insensitive "into" is Over — §24.4 default); variableCapture REFUSES an
    // unrecognized value (same as the MCP tool arg, §24.9 — a silently-wrong capture
    // shape is worse than a failed launch).
    private static TraceRunConfig? ParseTraceRun(IDictionary<string, JToken> properties)
    {
        if (!properties.TryGetValue("traceRun", out var token) || token.Type == JTokenType.Null)
        {
            return null;
        }

        if (token.Type == JTokenType.Boolean)
        {
            return token.Value<bool>() ? new TraceRunConfig(StepKind.Over, false, false, null) : null;
        }

        if (token is not JObject obj)
        {
            throw new ProtocolLaunchException("launch config 'traceRun' must be a boolean or an options object.");
        }

        var stepMode = obj.Value<string>("stepMode") ?? "over";
        var kind = stepMode.Equals("into", StringComparison.OrdinalIgnoreCase) ? StepKind.Into : StepKind.Over;

        var variableCapture = obj.Value<string>("variableCapture");
        var fullCapture =
            string.IsNullOrEmpty(variableCapture) || variableCapture.Equals("changed", StringComparison.OrdinalIgnoreCase)
                ? false
                : variableCapture.Equals("full", StringComparison.OrdinalIgnoreCase)
                    ? true
                    : throw new ProtocolLaunchException(
                        $"launch config 'traceRun.variableCapture' must be 'changed' or 'full', not '{variableCapture}'.");

        return new TraceRunConfig(
            kind,
            obj.Value<bool?>("captureTempRowCounts") ?? false,
            fullCapture,
            obj.Value<string>("file"));
    }

    private static string RequireString(IDictionary<string, JToken> properties, string key)
    {
        return GetString(properties, key)
            ?? throw new ProtocolLaunchException($"launch config is missing required property '{key}'.");
    }

    private static string? GetString(IDictionary<string, JToken> properties, string key)
    {
        return properties.TryGetValue(key, out var token) && token.Type != JTokenType.Null
            ? token.Value<string>()
            : null;
    }

    private static int? GetInt(IDictionary<string, JToken> properties, string key)
    {
        return properties.TryGetValue(key, out var token) && token.Type != JTokenType.Null
            ? token.Value<int>()
            : null;
    }

    private static bool? GetBool(IDictionary<string, JToken> properties, string key)
    {
        return properties.TryGetValue(key, out var token) && token.Type != JTokenType.Null
            ? token.Value<bool>()
            : null;
    }

    private static IReadOnlyList<string> GetStringArray(IDictionary<string, JToken> properties, string key)
    {
        if (!properties.TryGetValue(key, out var token) || token.Type != JTokenType.Array)
        {
            return Array.Empty<string>();
        }

        return ((JArray)token).Select(t => t.Value<string>()).Where(s => !string.IsNullOrEmpty(s)).ToList()!;
    }
}

// DESIGN §17 (A73): the parsed traceRun options — §24.4 trace_* knobs on the launch surface,
// plus an optional explicit trace-file path (else a timestamped file under the OS temp dir).
public sealed record TraceRunConfig(
    StepKind StepMode,
    bool CaptureTempRowCounts,
    bool FullVariableCapture,
    string? File);

// Thin marker so LaunchConfig.Parse doesn't need a dependency on the DAP library's
// ProtocolException; TsqlDbgDebugSession translates this to one at the DAP boundary.
public sealed class ProtocolLaunchException : Exception
{
    public ProtocolLaunchException(string message) : base(message)
    {
    }
}
