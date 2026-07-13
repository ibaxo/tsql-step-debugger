namespace TsqlDbg.Core.Execution;

public sealed record ResultSet(
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<object?>> Rows);
