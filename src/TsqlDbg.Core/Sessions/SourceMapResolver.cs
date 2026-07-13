namespace TsqlDbg.Core.Sessions;

// DESIGN §5.2 (M7 sourceMap hash-compare): expands one launch `sourceMap` glob
// entry into candidate file paths on disk. VS Code resolves `${workspaceFolder}`-
// style variables in the launch config BEFORE the adapter ever sees it (the same
// built-in substitution "script": "${file}" already relies on), so every pattern
// arriving here is already an absolute path — the only thing left to interpret is
// filesystem wildcards. Deliberately the smallest matcher that covers DESIGN.md's
// own example shape (a literal base directory, optionally "**" for any depth, a
// trailing filename pattern with plain `*`/`?`) rather than a full glob engine or a
// new NuGet dependency — Core has no file-I/O restriction (TargetsFile already
// reads targets.json from disk this way).
public static class SourceMapResolver
{
    public static IReadOnlyList<string> ExpandGlob(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return Array.Empty<string>();
        }

        // No wildcard at all — an exact file reference.
        if (pattern.IndexOfAny(WildcardChars) < 0)
        {
            return File.Exists(pattern) ? new[] { pattern } : Array.Empty<string>();
        }

        var recursive = pattern.Contains("**");
        var wildcardIndex = pattern.IndexOfAny(WildcardChars);
        var prefixEnd = pattern.LastIndexOfAny(PathSeparators, wildcardIndex);
        var baseDir = prefixEnd >= 0 ? pattern[..prefixEnd] : ".";
        var rest = prefixEnd >= 0 ? pattern[(prefixEnd + 1)..] : pattern;

        // `rest` is now e.g. "**/*.sql" or "*.sql" — the actual filename pattern is
        // whatever follows the LAST separator (a "**" segment, when present, only
        // ever means "any depth" here; DESIGN's own example never nests a literal
        // directory name after it).
        var lastSeparator = rest.LastIndexOfAny(PathSeparators);
        var filePattern = lastSeparator >= 0 ? rest[(lastSeparator + 1)..] : rest;

        if (string.IsNullOrEmpty(filePattern) || !Directory.Exists(baseDir))
        {
            return Array.Empty<string>();
        }

        return Directory.EnumerateFiles(
                baseDir, filePattern, recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
            .ToList();
    }

    private static readonly char[] WildcardChars = { '*', '?' };
    private static readonly char[] PathSeparators = { '/', '\\' };
}
