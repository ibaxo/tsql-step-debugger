using TsqlDbg.Core.Execution;
using TsqlDbg.Core.Interpreter;

namespace TsqlDbg.Core.Sessions;

// DESIGN §20.3.1: "returnCode, outputParams{} (name -> value, exact typed comparison)."
// ReturnCode defaults to 0 (§11.5: bare RETURN, and running off the end of the frame
// body with no RETURN at all, both return 0 natively) and is overwritten only by a
// RETURN <value> whose expression evaluates successfully (§6 M2 D9).
public sealed record SessionResult(
    IReadOnlyList<StatementUnit> StatementUnits,
    BatchResult Execution,
    int ReturnCode = 0);
