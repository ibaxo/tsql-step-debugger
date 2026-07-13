using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace TsqlDbg.Integration;

// M6 item 6 (organizer-review follow-up, docs/archive/reviews/
// m6-item6-adapter-boost-wiring-sonnet-escalation.md): a minimal, hand-rolled DAP
// client -- Content-Length-framed JSON over a spawned adapter process's stdin/stdout,
// the same wire shape TsqlDbg.Adapter's Microsoft.VisualStudio.Shared.
// VSCodeDebugProtocol host speaks server-side. No DAP SDK on this side; just enough
// to drive the adapter-level boost-refusal live tests (and any future real-DAP
// Integration test) deterministically. Mirrors the M1-M5 smoke-trace precedent, now
// wired into the automated suite instead of only a one-off manual capture.
internal sealed class DapStdioHarness : IAsyncDisposable
{
    private readonly Process _proc;
    private readonly Stream _stdin;
    private readonly Stream _stdout;
    private int _seq;
    private readonly Dictionary<int, TaskCompletionSource<JsonNode>> _pending = new();
    private readonly Channel<JsonNode> _events = Channel.CreateUnbounded<JsonNode>();
    private readonly object _lock = new();
    private readonly Task _readerTask;

    private DapStdioHarness(Process proc)
    {
        _proc = proc;
        _stdin = proc.StandardInput.BaseStream;
        _stdout = proc.StandardOutput.BaseStream;
        _readerTask = Task.Run(ReadLoopAsync);
    }

    // M7 packaging (D7 item 2/3): every existing call site (`Launch(tracePath)`) keeps
    // launching the dev-path build output via `dotnet <dll>`, byte-identical to
    // before. The two new optional parameters let a packaging smoke launch a
    // DIFFERENT executable directly (the packaged self-contained adapter) with a
    // masked child-process environment (PATH/DOTNET_ROOT), without touching any
    // existing test's behavior.
    public static DapStdioHarness Launch(
        string tracePath,
        string? executablePath = null,
        IReadOnlyDictionary<string, string?>? environmentOverrides = null)
    {
        ProcessStartInfo psi;
        if (executablePath is null)
        {
            var dllPath = Path.Combine(AppContext.BaseDirectory, "TsqlDbg.Adapter.dll");
            psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                ArgumentList = { dllPath, "--trace", tracePath },
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
        }
        else
        {
            // The packaged adapter is a self-contained apphost exe -- run it directly,
            // no "dotnet" muxer prefix (that's the whole point of this leg).
            psi = new ProcessStartInfo
            {
                FileName = executablePath,
                ArgumentList = { "--trace", tracePath },
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
        }

        if (environmentOverrides is not null)
        {
            foreach (var (key, value) in environmentOverrides)
            {
                if (value is null)
                {
                    psi.Environment.Remove(key);
                }
                else
                {
                    psi.Environment[key] = value;
                }
            }
        }

        var proc = Process.Start(psi) ?? throw new InvalidOperationException("failed to start TsqlDbg.Adapter process");
        proc.BeginErrorReadLine();
        return new DapStdioHarness(proc);
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (true)
            {
                var msg = await ReadMessageAsync(_stdout).ConfigureAwait(false);
                if (msg is null)
                {
                    break;
                }

                var type = msg["type"]?.GetValue<string>();
                if (type == "response")
                {
                    var reqSeq = msg["request_seq"]!.GetValue<int>();
                    TaskCompletionSource<JsonNode>? tcs;
                    lock (_lock)
                    {
                        _pending.Remove(reqSeq, out tcs);
                    }

                    tcs?.TrySetResult(msg);
                }
                else if (type == "event")
                {
                    await _events.Writer.WriteAsync(msg).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _events.Writer.TryComplete();
        }
    }

    private static async Task<JsonNode?> ReadMessageAsync(Stream stream)
    {
        int contentLength = -1;
        while (true)
        {
            var line = await ReadLineAsync(stream).ConfigureAwait(false);
            if (line is null)
            {
                return null;
            }

            if (line.Length == 0)
            {
                break;
            }

            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            {
                contentLength = int.Parse(line.Substring("Content-Length:".Length).Trim());
            }
        }

        if (contentLength < 0)
        {
            return null;
        }

        var buffer = new byte[contentLength];
        var read = 0;
        while (read < contentLength)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read, contentLength - read)).ConfigureAwait(false);
            if (n == 0)
            {
                return null;
            }

            read += n;
        }

        return JsonNode.Parse(Encoding.UTF8.GetString(buffer));
    }

    private static async Task<string?> ReadLineAsync(Stream stream)
    {
        var sb = new StringBuilder();
        var buf = new byte[1];
        while (true)
        {
            var n = await stream.ReadAsync(buf.AsMemory(0, 1)).ConfigureAwait(false);
            if (n == 0)
            {
                return sb.Length > 0 ? sb.ToString() : null;
            }

            if (buf[0] == '\n')
            {
                if (sb.Length > 0 && sb[^1] == '\r')
                {
                    sb.Length--;
                }

                return sb.ToString();
            }

            sb.Append((char)buf[0]);
        }
    }

    public async Task<JsonNode> SendRequestAsync(string command, JsonObject? arguments = null, TimeSpan? timeout = null)
    {
        int seq;
        var tcs = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
        {
            seq = ++_seq;
            _pending[seq] = tcs;
        }

        var req = new JsonObject { ["seq"] = seq, ["type"] = "request", ["command"] = command };
        if (arguments is not null)
        {
            req["arguments"] = arguments;
        }

        var json = req.ToJsonString();
        var bytes = Encoding.UTF8.GetBytes(json);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {bytes.Length}\r\n\r\n");
        await _stdin.WriteAsync(header).ConfigureAwait(false);
        await _stdin.WriteAsync(bytes).ConfigureAwait(false);
        await _stdin.FlushAsync().ConfigureAwait(false);

        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(30);
        var completed = await Task.WhenAny(tcs.Task, Task.Delay(effectiveTimeout)).ConfigureAwait(false);
        if (completed != tcs.Task)
        {
            throw new TimeoutException($"request '{command}' (seq={seq}) timed out after {effectiveTimeout}");
        }

        var response = await tcs.Task.ConfigureAwait(false);
        if (!(response["success"]?.GetValue<bool>() ?? false))
        {
            throw new InvalidOperationException(
                $"request '{command}' failed: {response["message"]?.GetValue<string>() ?? "(no message)"}");
        }

        return response;
    }

    // Drains the shared event channel in FIFO order until (name, predicate) matches.
    // Every event is delivered exactly once -- callers must wait for events in the
    // order the protocol actually emits them; non-matching events in between are
    // discarded (harmless for these tests, which drive a single linear scenario).
    public async Task<JsonNode> WaitForEventAsync(string eventName, Func<JsonNode, bool>? predicate = null, TimeSpan? timeout = null)
    {
        predicate ??= _ => true;
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        while (DateTime.UtcNow < deadline)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            using var cts = new CancellationTokenSource(remaining);
            JsonNode msg;
            try
            {
                msg = await _events.Reader.ReadAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (msg["event"]?.GetValue<string>() == eventName && predicate(msg))
            {
                return msg;
            }
        }

        throw new TimeoutException($"never saw event '{eventName}' within the timeout");
    }

    // A56: collect EVERY event up to and including the first that matches
    // `terminalEvent` (+predicate), in arrival order. WaitForEventAsync discards
    // non-matching events, so it cannot assert what did NOT arrive before a
    // synchronization point; this can (e.g. "no gated diagnostic note reached the
    // console before the step-stop").
    public async Task<IReadOnlyList<JsonNode>> CollectEventsUntilAsync(
        string terminalEvent, Func<JsonNode, bool>? predicate = null, TimeSpan? timeout = null)
    {
        predicate ??= _ => true;
        var collected = new List<JsonNode>();
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        while (DateTime.UtcNow < deadline)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            using var cts = new CancellationTokenSource(remaining);
            JsonNode msg;
            try
            {
                msg = await _events.Reader.ReadAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            collected.Add(msg);
            if (msg["event"]?.GetValue<string>() == terminalEvent && predicate(msg))
            {
                return collected;
            }
        }

        throw new TimeoutException($"never saw terminal event '{terminalEvent}' within the timeout");
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_proc.HasExited)
            {
                await SendRequestAsync("disconnect", new JsonObject(), TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            }
        }
        catch
        {
            // best-effort teardown
        }

        try
        {
            _stdin.Close();
        }
        catch
        {
            // ignore
        }

        if (!_proc.WaitForExit(5000))
        {
            _proc.Kill(true);
        }

        await _readerTask.ConfigureAwait(false);
    }
}
