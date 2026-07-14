using TsqlDbg.Core.Interpreter;

namespace TsqlDbg.Core.State;

// DESIGN §8.1: exact-type state table DDL, seed row, and the §10.4 resurrection
// re-seed. Column types are the untouched source slice of the declared type
// (VariableDeclaration.DataTypeSql), so length/precision/scale/collation/MAX are
// preserved without any client-side type mapping. §8.2's mid-flow ALTER was retired by
// the ratified declaration-hoisting amendment (fact 14) — no frame can gain variables
// the parse didn't reveal; the BuildAlterAdd it required was deleted at M3.
public static class StateTableDdlBuilder
{
    // T-SQL requires >=1 column. A frame with zero parameters and no DECLAREs yet
    // (rare — every M1 corpus fixture has at least one parameter) still needs a table
    // to exist from frame creation onward, since composed batches guard writes with
    // OBJECT_ID(...) IS NOT NULL (§7.1/§10.7) regardless of whether it has real
    // columns yet. A harmless placeholder column covers that edge case.
    private const string PlaceholderColumn = "[__dbg_placeholder] bit NULL";

    public static string BuildCreateTable(int frameOrdinal, IReadOnlyList<VariableSlot> variables)
    {
        var table = StateTableIdentifiers.TableName(frameOrdinal);
        var columns = variables.Count == 0
            ? PlaceholderColumn
            : string.Join(", ", variables.Select(ColumnDefinition));
        return $"CREATE TABLE {table} ({columns});";
    }

    // DESIGN §10.4 resurrection step 2: after a debuggee ROLLBACK/COMMIT drops
    // trancount to 0, the state table survives (created before BEGIN TRAN) but its
    // CONTENT reverted with the transaction — while native variables are
    // non-transactional and kept their values. Re-seed every column from the session's
    // binary snapshot, carried as ADO.NET parameters (values can be any SQL type;
    // literals can't round-trip them) and normalized through CONVERT to the declared
    // type so precision/scale/collation are exact.
    public static string BuildReseedUpdate(
        int frameOrdinal, IReadOnlyList<VariableSlot> variables, Func<VariableSlot, string> parameterName)
    {
        if (variables.Count == 0)
        {
            throw new ArgumentException("A zero-variable frame has nothing to re-seed.", nameof(variables));
        }

        var table = StateTableIdentifiers.TableName(frameOrdinal);
        var sets = string.Join(", ", variables.Select(v =>
            $"{StateTableIdentifiers.ColumnName(v.Declaration.Name)} = CONVERT({v.Declaration.StorageType}, {parameterName(v)})"));
        return $"UPDATE {table} SET {sets};";
    }

    // DESIGN §4 step 4 / §8.1: frame 0's seed row carries launch parameter values;
    // mid-flow DECLAREs (§8.2) seed NULL for the ALTER'd column and rely on a
    // synthetic SET for any initializer. `seeds` maps a subset of `variables` to a
    // literal T-SQL value; any variable without an entry seeds NULL.
    public static string BuildSeedInsert(int frameOrdinal, IReadOnlyList<VariableSlot> variables, IReadOnlyDictionary<string, string> seeds)
    {
        var table = StateTableIdentifiers.TableName(frameOrdinal);
        if (variables.Count == 0)
        {
            return $"INSERT INTO {table} DEFAULT VALUES;";
        }

        var columns = string.Join(", ", variables.Select(v => StateTableIdentifiers.ColumnName(v.Declaration.Name)));
        var values = string.Join(", ", variables.Select(v =>
            seeds.TryGetValue(v.Declaration.Name, out var literal) ? literal : "NULL"));
        return $"INSERT INTO {table} ({columns}) VALUES ({values});";
    }

    // DESIGN §8.1: "Adapter keeps a binary snapshot after every step: SELECT * FROM
    // #__dbg_s{n}" — used both for the Locals-pane source and rollback resurrection.
    public static string BuildSelectAll(int frameOrdinal) => $"SELECT * FROM {StateTableIdentifiers.TableName(frameOrdinal)};";

    // DESIGN §11.5 (M4, fact 23 — completed pops only): server-side callee→caller
    // OUTPUT copy-back "to stay type-faithful" (values never round-trip the client),
    // plus the `EXEC @rc = …` assignment from the callee's captured __ret, in ONE
    // statement. Aborted pops never call this (fact 23 C–G: caller variables keep
    // their pre-call values).
    public static string BuildOutputCopyBack(
        int callerOrdinal, int calleeOrdinal, IReadOnlyList<OutputPair> outputPairs,
        string? returnCodeVariable, string returnCodeParameterName)
    {
        var caller = StateTableIdentifiers.TableName(callerOrdinal);
        var callee = StateTableIdentifiers.TableName(calleeOrdinal);
        var sets = new List<string>(outputPairs.Count + 1);
        foreach (var pair in outputPairs)
        {
            sets.Add($"{StateTableIdentifiers.ColumnName(pair.CallerVariable)} = " +
                     $"(SELECT {StateTableIdentifiers.ColumnName(pair.CalleeParameter)} FROM {callee})");
        }

        if (returnCodeVariable is not null)
        {
            sets.Add($"{StateTableIdentifiers.ColumnName(returnCodeVariable)} = CONVERT(int, {returnCodeParameterName})");
        }

        if (sets.Count == 0)
        {
            throw new ArgumentException("Copy-back with no OUTPUT pairs and no @rc target — caller should have skipped the batch.");
        }

        return $"UPDATE {caller} SET {string.Join(", ", sets)};";
    }

    // M4 (§11.5 pop cleanup / §10.4 A9 step 2): guarded drop — the table may already be
    // gone (debuggee ROLLBACK past the frame's push, fact 1), and pops while doomed
    // never even send this (3930; the fact-22 forced rollback reaps it anyway).
    public static string BuildDropTable(int frameOrdinal)
    {
        var table = StateTableIdentifiers.TableName(frameOrdinal);
        return $"IF OBJECT_ID('tempdb..{table}') IS NOT NULL DROP TABLE {table};";
    }

    // M4 (§10.4 A9 step 2): re-create a rollback-destroyed frame>0 state table before
    // its parameterized reseed. The NULL seed row makes BuildReseedUpdate's UPDATE hit
    // exactly one row, same as frame creation at push.
    public static string BuildCreateIfMissing(int frameOrdinal, IReadOnlyList<VariableSlot> variables)
    {
        var table = StateTableIdentifiers.TableName(frameOrdinal);
        var create = BuildCreateTable(frameOrdinal, variables);
        var seed = BuildSeedInsert(frameOrdinal, variables, EmptySeeds);
        return $"IF OBJECT_ID('tempdb..{table}') IS NULL BEGIN {create} {seed} END;";
    }

    private static readonly IReadOnlyDictionary<string, string> EmptySeeds = new Dictionary<string, string>();

    // A59: the STORAGE type, not the declared one — the state table lives in tempdb, which
    // cannot see a user-defined alias type (fact 34a, msg 2715). For every other type the
    // two are the same string. The COLUMN form carries the collation; the CONVERT sites use
    // the bare form, which is the only one CONVERT accepts. (§8.1)
    private static string ColumnDefinition(VariableSlot v) =>
        $"{StateTableIdentifiers.ColumnName(v.Declaration.Name)} {v.Declaration.StorageColumnType} NULL";
}
