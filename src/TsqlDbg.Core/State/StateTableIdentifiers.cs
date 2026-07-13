namespace TsqlDbg.Core.State;

// DESIGN §8.1: "CREATE TABLE #__dbg_s{n} ( [<var>] <exact declared type...>, ... )".
public static class StateTableIdentifiers
{
    public static string TableName(int frameOrdinal) => $"#__dbg_s{frameOrdinal}";

    // M6 (§14/A21): the boost position table — session-scoped, one row (seq, pos).
    // Physical #__dbg_* names follow the existing no-nonce convention (per-session #
    // scope already isolates them). Created + seeded ONCE at session init when
    // boost:true (F1 ruling, docs/archive/reviews/m6-boost-core-fable.md §2: the seed INSERT
    // resets the session-sticky SCOPE_IDENTITY chain — fact 26d — so it runs only on
    // the fresh connection where the chain is already NULL, and at trancount 0 so no
    // debuggee ROLLBACK can ever destroy it — fact 1).
    public const string BoostPositionTable = "#__dbg_boost";

    // Column name is the variable name with its leading '@' stripped, bracket-quoted
    // (matches Appendix A: @Id -> [Id]).
    public static string ColumnName(string variableName)
    {
        var bare = variableName.StartsWith('@') ? variableName[1..] : variableName;
        return Bracket(bare);
    }

    public static string Bracket(string identifier) => "[" + identifier.Replace("]", "]]") + "]";
}
