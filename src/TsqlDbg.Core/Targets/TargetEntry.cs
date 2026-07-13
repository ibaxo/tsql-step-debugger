namespace TsqlDbg.Core.Targets;

// DESIGN §16: allowlist entry. Keyed by server name in targets.json.
public sealed record TargetEntry(
    string Server,
    string Env,
    bool AllowWrites,
    string? Options);
