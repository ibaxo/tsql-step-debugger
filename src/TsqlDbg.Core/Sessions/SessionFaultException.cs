namespace TsqlDbg.Core.Sessions;

// DESIGN §22 M1 decision on record (phase0-integration-notes.md): "any ok=0 control
// row or SqlException is session-fatal (no §10 routing yet) -> teardown." §10 error
// routing (catch/propagate) is M3 scope; this exception is what M1 raises instead.
public sealed class SessionFaultException : Exception
{
    public SessionFaultException(string message) : base(message)
    {
    }
}
