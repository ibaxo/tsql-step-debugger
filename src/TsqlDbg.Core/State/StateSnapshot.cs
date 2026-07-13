using TsqlDbg.Core.Execution;

namespace TsqlDbg.Core.State;

// DESIGN §8.1: the adapter's typed buffer read from `SELECT * FROM #__dbg_s{n}`
// after each step — the Locals-pane source and the rollback-resurrection source
// (§9/§10.4). Keyed by variable name (with '@'), not by column name.
public sealed class StateSnapshot
{
    private readonly IReadOnlyDictionary<string, object?> _byVariableName;

    private StateSnapshot(IReadOnlyDictionary<string, object?> byVariableName)
    {
        _byVariableName = byVariableName;
    }

    public bool TryGet(string variableName, out object? value) => _byVariableName.TryGetValue(variableName, out value);

    public static StateSnapshot Empty { get; } = new(new Dictionary<string, object?>());

    // Maps the state table's bracket-quoted, '@'-stripped columns back to '@'-prefixed
    // variable names using the frame's own variable catalog (ordinal-independent —
    // matches by name, tolerant of the placeholder column when present).
    /// <summary>M3 (§10.4): builds from the session's in-memory snapshot values (the
    /// __dbg_state piggyback set, ordinal-aligned with the variable catalog) — the
    /// authoritative source while the transaction is doomed, when the state table is
    /// unwritable and therefore stale.</summary>
    public static StateSnapshot FromValues(IReadOnlyList<object?> values, IReadOnlyList<Interpreter.VariableSlot> variables)
    {
        var map = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var slot in variables)
        {
            if (slot.Ordinal < values.Count)
            {
                map[slot.Declaration.Name] = values[slot.Ordinal];
            }
        }

        return new StateSnapshot(map);
    }

    public static StateSnapshot FromResultSet(ResultSet resultSet, IReadOnlyList<Interpreter.VariableSlot> variables)
    {
        if (resultSet.Rows.Count == 0)
        {
            return Empty;
        }

        var row = resultSet.Rows[0];
        var map = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var slot in variables)
        {
            var columnName = StateTableIdentifiers.ColumnName(slot.Declaration.Name).Trim('[', ']');
            var index = resultSet.Columns.ToList().FindIndex(c => string.Equals(c, columnName, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                map[slot.Declaration.Name] = row[index];
            }
        }

        return new StateSnapshot(map);
    }
}
