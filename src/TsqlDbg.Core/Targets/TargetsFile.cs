using System.Text.Json;
using System.Text.Json.Serialization;

namespace TsqlDbg.Core.Targets;

// DESIGN §16: "targets.json, identical semantics to the mssql-proc-debug skill
// (env, allowWrites, options); unknown server -> refuse to launch. Never edit the
// file programmatically." This type only ever reads the file.
public sealed class TargetsFile
{
    private readonly IReadOnlyDictionary<string, TargetEntry> _byServer;

    private TargetsFile(IReadOnlyDictionary<string, TargetEntry> byServer)
    {
        _byServer = byServer;
    }

    public bool TryGet(string server, out TargetEntry entry)
    {
        return _byServer.TryGetValue(server, out entry!);
    }

    public static TargetsFile Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new TargetsPolicyException(
                $"targets.json not found at '{path}'. Refusing to launch: DESIGN.md §16 requires an " +
                "explicit allowlist entry for every server.");
        }

        var json = File.ReadAllText(path);
        return Parse(json);
    }

    public static TargetsFile Parse(string json)
    {
        TargetsFileDto dto;
        try
        {
            dto = JsonSerializer.Deserialize<TargetsFileDto>(json, JsonOptions)
                  ?? throw new TargetsPolicyException("targets.json is empty or 'null'.");
        }
        catch (JsonException ex)
        {
            throw new TargetsPolicyException($"targets.json is not valid JSON: {ex.Message}", ex);
        }

        var map = new Dictionary<string, TargetEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var (server, entry) in dto.Targets ?? new())
        {
            map[server] = new TargetEntry(server, entry.Env ?? "unknown", entry.AllowWrites, entry.Options);
        }

        return new TargetsFile(map);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private sealed class TargetsFileDto
    {
        [JsonPropertyName("targets")]
        public Dictionary<string, TargetEntryDto>? Targets { get; set; }
    }

    private sealed class TargetEntryDto
    {
        [JsonPropertyName("env")]
        public string? Env { get; set; }

        [JsonPropertyName("allowWrites")]
        public bool AllowWrites { get; set; }

        [JsonPropertyName("options")]
        public string? Options { get; set; }
    }
}
