using TsqlDbg.Core.Tracing;

namespace TsqlDbg.Core.Tests.Fakes;

// M6 (B10): the boost trace surface is part of the contract (--trace is the
// debugger's own debugger) — tests record events through the same ITraceSink API the
// file sink uses, so an event that reaches here is by construction parseable there.
public sealed class RecordingTraceSink : ITraceSink
{
    public List<(string Category, string Message)> Events { get; } = new();

    public void Event(string category, string message) => Events.Add((category, message));
}
