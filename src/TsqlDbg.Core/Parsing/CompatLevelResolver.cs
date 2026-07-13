namespace TsqlDbg.Core.Parsing;

// DESIGN §2 (A57): compatLevel auto-detect. `compatLevel:0` (the product default, applied at
// the adapter's LaunchConfig boundary) means "auto" — pick the ScriptDom parser from the
// connected server's product MAJOR version (compat level = major × 10, e.g. 16 → 160),
// clamped to the band the bundled parsers cover, [150, 170], with a launch warning whenever
// the server sits outside it (older → 150, newer → 170). An explicit 150/160/170 is an
// override: returned unchanged, no probe, no warning.
//
// The major version is read from SqlConnection.ServerVersion (the login response — zero extra
// round trips, exactly like §12.1's @@SPID via SqlConnection.ServerProcessId; see
// LiveSession.OpenAsync). serverMajorVersion <= 0 means "unknown" (no live connection — e.g. a
// FakeStatementExecutor unit test — or an unparseable version string); auto then falls back to
// 150 with a note rather than guessing. Pure and side-effect-free so Session can call it with
// no round trip and it is directly unit-testable.
public static class CompatLevelResolver
{
    // The parsers ScriptParser.CreateParser can build (DESIGN §2) — the clamp band for auto.
    public const int MinCompatLevel = 150;
    public const int MaxCompatLevel = 170;

    public static int Resolve(int requestedCompatLevel, int serverMajorVersion, out string? warning)
    {
        warning = null;

        // An explicit pin (150/160/170, or anything non-zero) is honored verbatim.
        if (requestedCompatLevel != 0)
        {
            return requestedCompatLevel;
        }

        if (serverMajorVersion <= 0)
        {
            warning = "compatLevel is 'auto' (0) but the server version could not be read; "
                + $"using the {MinCompatLevel} (SQL Server 2019) parser.";
            return MinCompatLevel;
        }

        var desired = serverMajorVersion * 10;

        if (desired < MinCompatLevel)
        {
            warning = $"compatLevel 'auto': the server (major version {serverMajorVersion}, compat {desired}) "
                + $"is older than the oldest bundled parser; using {MinCompatLevel}. Some SQL Server 2019+ "
                + "syntax could parse here that the server would reject at run time.";
            return MinCompatLevel;
        }

        if (desired > MaxCompatLevel)
        {
            warning = $"compatLevel 'auto': the server (major version {serverMajorVersion}, compat {desired}) "
                + $"is newer than the newest bundled parser; using {MaxCompatLevel}. The very newest T-SQL "
                + "syntax may not parse.";
            return MaxCompatLevel;
        }

        // In band: majors 15/16/17 → exactly 150/160/170, all supported by CreateParser.
        return desired;
    }
}
