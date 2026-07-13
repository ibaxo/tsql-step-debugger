using TsqlDbg.Core.Execution;

namespace TsqlDbg.Core.Batches;

// DESIGN §7.1 notes: "the user statement's own result sets stream first; the adapter
// identifies the control row by the __dbg_ctl column and forwards every non-control
// result set to the Debug Console as query output." M3: a second marked set,
// __dbg_state, carries the raw (untruncated, typed) variable values — the session's
// binary snapshot (§8.1) and the §10.4 doomed-mode/resurrection value source. Neither
// marked set is ever forwarded to the console.
public static class ControlRowParser
{
    public static (ControlRow Control, IReadOnlyList<ResultSet> UserResultSets, IReadOnlyList<object?>? StateValues)
        Parse(BatchResult result, int variableCount)
    {
        if (!TryParse(result, variableCount, out var control, out var userSets, out var stateValues))
        {
            throw new InvalidOperationException(
                "Composed batch did not return a __dbg_ctl control row (execution or builder bug — §7.3).");
        }

        return (control!, userSets, stateValues);
    }

    /// <summary>D5/A13: oracle-free stepped-over EXEC batches can legitimately end
    /// without a control row (fact 24 shape b — a batch-aborting error inside the
    /// callee kills the physical batch under absorption, no exception thrown). The
    /// session classifies that shape itself; every other caller uses
    /// <see cref="Parse"/>, where a missing control row stays the §7.3 contract
    /// violation it always was.</summary>
    public static bool TryParse(
        BatchResult result, int variableCount,
        out ControlRow? control, out IReadOnlyList<ResultSet> userResultSets, out IReadOnlyList<object?>? stateValues)
    {
        var userSets = new List<ResultSet>();
        userResultSets = userSets;
        stateValues = null;
        control = null;
        ResultSet? controlSet = null;
        ResultSet? stateSet = null;
        foreach (var rs in result.ResultSets)
        {
            if (rs.Columns.Contains("__dbg_ctl"))
            {
                if (controlSet is not null)
                {
                    throw new InvalidOperationException("Composed batch returned more than one __dbg_ctl result set — builder bug.");
                }

                controlSet = rs;
            }
            else if (rs.Columns.Contains("__dbg_state"))
            {
                if (stateSet is not null)
                {
                    throw new InvalidOperationException("Composed batch returned more than one __dbg_state result set — builder bug.");
                }

                stateSet = rs;
            }
            else
            {
                userSets.Add(rs);
            }
        }

        if (stateSet is { Rows: [var stateRow, ..] })
        {
            // Column 0 is the __dbg_state marker; the rest align with the frame's
            // variable catalog order (the builder emits them from Variables.All).
            var values = new object?[stateRow.Count - 1];
            for (var i = 1; i < stateRow.Count; i++)
            {
                values[i - 1] = stateRow[i];
            }

            stateValues = values;
        }

        if (controlSet is null || controlSet.Rows.Count == 0)
        {
            return false;
        }

        var columns = controlSet.Columns;
        var row = controlSet.Rows[0];

        object? Col(string name)
        {
            for (var i = 0; i < columns.Count; i++)
            {
                if (columns[i] == name)
                {
                    return row[i];
                }
            }

            return null;
        }

        bool ok = AsBool(Col("ok"));
        var displayValues = new Dictionary<int, DisplayValue>();
        for (var i = 0; i < variableCount; i++)
        {
            var text = Col($"v_{i}") as string;
            var isNullCol = Col($"v_{i}_isnull");
            displayValues[i] = new DisplayValue(text, isNullCol is not null && AsBool(isNullCol));
        }

        control = new ControlRow(
            Ok: ok,
            Rc: AsIntOrNull(Col("rc")),
            ScopeIdentity: AsDecimalOrNull(Col("scope_identity")),
            Trancount: AsInt(Col("trancount")),
            XactState: AsInt(Col("xact_state")),
            ErrNumber: AsIntOrNull(Col("err_number")),
            ErrSeverity: AsIntOrNull(Col("err_severity")),
            ErrState: AsIntOrNull(Col("err_state")),
            ErrLine: AsIntOrNull(Col("err_line")),
            ErrProcedure: Col("err_procedure") as string,
            ErrMessage: Col("err_message") as string,
            DisplayValues: displayValues,
            ErrAfter: AsIntOrNull(Col("err_after")));

        return true;
    }

    private static bool AsBool(object? v) => v switch
    {
        bool b => b,
        null => throw new InvalidOperationException("Expected a non-null bit column."),
        _ => Convert.ToBoolean(v),
    };

    private static int AsInt(object? v) => v is null
        ? throw new InvalidOperationException("Expected a non-null int column.")
        : Convert.ToInt32(v);

    private static int? AsIntOrNull(object? v) => v is null ? null : Convert.ToInt32(v);

    private static decimal? AsDecimalOrNull(object? v) => v is null ? null : Convert.ToDecimal(v);
}
