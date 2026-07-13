namespace TsqlDbg.Core.Tracing;

// DESIGN §19: "--trace <file>: full DAP JSON both directions + every composed batch
// text + control rows + timings. This is the primary debugging tool for the debugger
// itself; implement in M0." The DAP-JSON side is logged by the Adapter (it owns the
// protocol layer); Core logs everything it does independently through this sink so a
// Core-only caller (unit/integration tests, the fidelity harness) still gets a trace.
//
// "Never log data values unless --trace-data is also set" (§19): Event() takes a
// pre-formatted summary string, not raw row data, so the caller must already have
// decided what's safe to log. M0 never has real debuggee data to log (--trace-data
// gating is deferred until a milestone actually produces row/variable values worth
// hiding).
public interface ITraceSink
{
    void Event(string category, string message);
}
