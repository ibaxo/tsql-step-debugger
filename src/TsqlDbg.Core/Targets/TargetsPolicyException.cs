namespace TsqlDbg.Core.Targets;

// Thrown for any targets.json policy refusal (missing file, malformed file, unknown
// server). DESIGN §16: "unknown server -> refuse to launch." Session must never open
// a connection once this is thrown.
public sealed class TargetsPolicyException : Exception
{
    public TargetsPolicyException(string message) : base(message)
    {
    }

    public TargetsPolicyException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
