using System.Text.Json;

namespace TsqlDbg.Core.Tracing;

// DESIGN §19. One JSON object per line (easy to tail -f / grep, and parseable per
// CLAUDE.md's "Definition of done": "--trace output remains parseable").
public sealed class FileTraceSink : ITraceSink, IDisposable
{
    private readonly StreamWriter _writer;
    private readonly object _gate = new();

    public FileTraceSink(string path)
    {
        var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        _writer = new StreamWriter(stream) { AutoFlush = true };
    }

    public void Event(string category, string message)
    {
        var line = JsonSerializer.Serialize(new TraceLine(DateTimeOffset.UtcNow, category, message));
        lock (_gate)
        {
            _writer.WriteLine(line);
        }
    }

    public void Dispose()
    {
        _writer.Dispose();
    }

    private sealed record TraceLine(DateTimeOffset Ts, string Category, string Message);
}
