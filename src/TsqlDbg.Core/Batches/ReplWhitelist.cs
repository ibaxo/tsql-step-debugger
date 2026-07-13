using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace TsqlDbg.Core.Batches;

// DESIGN §12.3 / M5 I6 (design note §2, docs/archive/reviews/m5-inspection-design-notes-fable.md):
// the REPL statement whitelist — pure, parse-time classification, no server contact.
// Read-only mode: SELECT only (not SELECT INTO), and additionally no NEXT VALUE FOR (a
// sequence fetch is a durable side effect hiding in an expression). Write mode still
// refuses, regardless of allowConsoleWrites: transaction control, SET (session
// options — NOT a plain variable assignment), USE, and any statement referencing a
// __dbg-prefixed identifier (harness self-protection).
public enum ReplRefusalKind
{
    None,
    NotOneStatement,
    ParseError,
    ReadOnlyNonSelect,
    SelectIntoInReadOnly,
    NextValueForInReadOnly,
    TransactionControl,
    SetOption,
    UseStatement,
    DbgIdentifierReference,
}

// IsVariableOnlyWrite (A46): the statement is a write (needs allowConsoleWrites) that
// modifies NO table — a plain `SET @x = expr`. It is the one write shape allowed while
// the transaction is doomed (it persists to the frame snapshot, not the dead state
// table) and the one that never re-opens detached protection (it is not a debuggee write).
public sealed record ReplClassification(bool IsWrite, bool CreatesNewTempObject, bool IsVariableOnlyWrite, ReplRefusalKind Refusal, string? RefusalMessage)
{
    public bool IsAllowed => Refusal == ReplRefusalKind.None;
}

public static class ReplWhitelist
{
    public static ReplClassification Classify(TSqlStatement statement, string rawText, bool allowConsoleWrites)
    {
        // Regardless of allowConsoleWrites: transaction control, SET (options), USE,
        // and any __dbg-prefixed identifier reference are ALWAYS refused.
        if (statement is TransactionStatement)
        {
            return Refuse(ReplRefusalKind.TransactionControl,
                "transaction-control statements are refused here — they would desync the §10.4 watchdog; " +
                "the debuggee's own BEGIN/COMMIT/ROLLBACK (or the launch config's commitMode) governs the transaction.");
        }

        if (statement is UseStatement)
        {
            return Refuse(ReplRefusalKind.UseStatement, "USE is refused — this session is bound to its launch database.");
        }

        if (IsSetOptionStatement(statement))
        {
            return Refuse(ReplRefusalKind.SetOption,
                "SET statements are refused — they would desync the §11.2 runtime-option tracker; SET options are debuggee-controlled.");
        }

        if (ContainsDbgIdentifier(rawText))
        {
            return Refuse(ReplRefusalKind.DbgIdentifierReference,
                "statements referencing a __dbg-prefixed identifier are refused (harness self-protection).");
        }

        var isSelect = statement is SelectStatement;
        var isSelectInto = statement is SelectStatement { Into: not null };
        var isWrite = !isSelect || isSelectInto;

        if (!allowConsoleWrites)
        {
            if (!isSelect)
            {
                return Refuse(ReplRefusalKind.ReadOnlyNonSelect,
                    "read-only mode: only a plain SELECT is allowed here (launch config allowConsoleWrites is false).");
            }

            if (isSelectInto)
            {
                return Refuse(ReplRefusalKind.SelectIntoInReadOnly,
                    "SELECT INTO is a write (creates a table) — refused in read-only mode (allowConsoleWrites is false).");
            }

            if (ContainsNextValueFor(statement))
            {
                return Refuse(ReplRefusalKind.NextValueForInReadOnly,
                    "NEXT VALUE FOR is a durable side effect (a sequence fetch) — refused in read-only mode.");
            }
        }

        var createsNewTempObject = statement is CreateTableStatement { SchemaObjectName.BaseIdentifier.Value: { } tableName } && IsTempName(tableName)
            || statement is SelectStatement { Into.BaseIdentifier.Value: { } intoName } && IsTempName(intoName)
            || statement is DeclareTableVariableStatement
            || statement is DeclareCursorStatement;

        // A46: a variable-only write (`SET @x = expr`) modifies no table, so it is the one
        // write allowed while doomed (persists to the frame snapshot, not the dead state
        // table). NEXT VALUE FOR is excluded — a sequence fetch is a durable,
        // non-transactional side effect that must NOT ride the doomed variable-write path.
        var isVariableOnlyWrite = statement is SetVariableStatement && !ContainsNextValueFor(statement);

        return new ReplClassification(isWrite, createsNewTempObject, isVariableOnlyWrite, ReplRefusalKind.None, null);
    }

    private static ReplClassification Refuse(ReplRefusalKind kind, string message) => new(false, false, false, kind, message);

    private static bool IsTempName(string? name) => name is { Length: > 1 } && name[0] == '#';

    // SET statements: every ScriptDom "Set*Statement" EXCEPT SetVariableStatement
    // (`SET @x = expr` is a plain variable assignment, not a session option toggle —
    // not desync-risky the way ANSI_NULLS/ARITHABORT/isolation level/etc. are).
    // PredicateSetStatement is the ScriptDom type for `SET ANSI_NULLS ON` / `SET
    // ARITHABORT OFF` / etc. — its type name does NOT start with "Set", so it needs
    // its own explicit check alongside the name-prefix catch-all (which correctly
    // covers SetTransactionIsolationLevelStatement, SetRowCountStatement, and so on).
    private static bool IsSetOptionStatement(TSqlStatement statement)
        => statement is PredicateSetStatement
           || (statement is not SetVariableStatement && statement.GetType().Name.StartsWith("Set", StringComparison.Ordinal));

    private static bool ContainsDbgIdentifier(string rawText)
        => rawText.Contains("__dbg", StringComparison.OrdinalIgnoreCase);

    // M5 I7 (§12.4 watch): watches are read-shaped by construction, so "the read-only
    // NEXT VALUE FOR block applies" regardless of allowConsoleWrites — exposed for
    // Session.EvaluateWatchAsync to reuse the same check.
    public static bool ContainsNextValueFor(TSqlFragment fragment)
    {
        var finder = new NextValueForFinder();
        fragment.Accept(finder);
        return finder.Found;
    }

    private sealed class NextValueForFinder : TSqlFragmentVisitor
    {
        public bool Found { get; private set; }

        public override void Visit(NextValueForExpression node) => Found = true;
    }
}
