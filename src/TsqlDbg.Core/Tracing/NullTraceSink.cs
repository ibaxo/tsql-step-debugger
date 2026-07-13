namespace TsqlDbg.Core.Tracing;

public sealed class NullTraceSink : ITraceSink
{
    public static readonly NullTraceSink Instance = new();

    private NullTraceSink()
    {
    }

    public void Event(string category, string message)
    {
    }
}
