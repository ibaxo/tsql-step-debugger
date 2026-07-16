using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace TsqlDbg.Core.Sessions;

// DESIGN §11.2 / M4 design notes D6 (fact 9): the engine reverts proc-scoped SET
// options at module exit; the debugger's one persistent connection keeps them, so a
// frame pop must emit restoring SETs for whatever the callee changed. This tracker
// holds the session's CURRENT runtime option values (seeded from the documented
// SqlClient session defaults plus our own init statements), is updated from every
// executed SET SU, and diffs a pop-time state against the push-time snapshot.
//
// Covers the ANSI/arithmetic on/off family + isolation, and (A53) the value-carrying
// options DATEFIRST / DATEFORMAT / LANGUAGE / LOCK_TIMEOUT / DEADLOCK_PRIORITY / TEXTSIZE /
// ROWCOUNT — all displayed in the System scope (§12.1) and reverted at a frame pop (§11.2).
//
// Deliberately excluded:
//   - QUOTED_IDENTIFIER / ANSI_NULLS — parse-time options, pinned per frame (fact 16;
//     the A49 exception for the ad-hoc script frame folds them straight into
//     Frame.SetEnv, not here — a per-frame value, never this session-wide tracker);
//   - NOCOUNT — forced ON by every §7.1 preamble (C5, cosmetic);
//   - XACT_ABORT — tracked per frame (Frame.XactAbortOn) and re-asserted by the F5
//     preamble line every batch, so its pop restore is free.
// An option outside the seeded map (exotic) restores nothing — the session console-
// notes it once (M4 design notes §7 residual).
public sealed class RuntimeOptionTracker
{
    private const string IsolationKey = "TRANSACTION ISOLATION LEVEL";

    private readonly Dictionary<string, string> _current = new(StringComparer.OrdinalIgnoreCase)
    {
        // SqlClient/TDS session defaults on a fresh connection (M3 notes §3.5 pinned
        // ARITHABORT OFF live; the rest are the documented login defaults).
        ["ANSI_WARNINGS"] = "ON",
        ["ANSI_PADDING"] = "ON",
        ["ARITHABORT"] = "OFF",
        ["ARITHIGNORE"] = "OFF",
        ["CONCAT_NULL_YIELDS_NULL"] = "ON",
        ["ANSI_NULL_DFLT_ON"] = "ON",
        ["NUMERIC_ROUNDABORT"] = "OFF",
        ["CURSOR_CLOSE_ON_COMMIT"] = "OFF",
        ["IMPLICIT_TRANSACTIONS"] = "OFF",
        [IsolationKey] = "READ COMMITTED",

        // A53: value-carrying options (SET <name> <value>). Baselines are the debugger's
        // OWN SqlClient connection defaults, probed live (df=7, lock=-1, textsize=-1,
        // language=us_english → dateformat mdy) — NOT the raw server defaults; TEXTSIZE
        // especially is -1 (SqlClient), and `SET TEXTSIZE -1` is a valid restore (verified).
        // DEADLOCK_PRIORITY/ROWCOUNT have no read intrinsic; NORMAL/0 are the fixed defaults.
        ["DATEFIRST"] = "7",
        ["DATEFORMAT"] = "mdy",
        ["LANGUAGE"] = "us_english",
        ["LOCK_TIMEOUT"] = "-1",
        ["DEADLOCK_PRIORITY"] = "NORMAL",
        ["TEXTSIZE"] = "-1",
        ["ROWCOUNT"] = "0",
    };

    // A53: SetCommandStatement's GeneralSetCommand.CommandType → the re-emittable option name.
    // Only these five value commands are tracked; any other GeneralSetCommandType (CONTEXT_INFO,
    // FIPS_FLAGGER, …) is ignored (executes normally, just not display/restore-tracked).
    private static readonly Dictionary<GeneralSetCommandType, string> ValueCommandNames = new()
    {
        [GeneralSetCommandType.DateFirst] = "DATEFIRST",
        [GeneralSetCommandType.DateFormat] = "DATEFORMAT",
        [GeneralSetCommandType.Language] = "LANGUAGE",
        [GeneralSetCommandType.LockTimeout] = "LOCK_TIMEOUT",
        [GeneralSetCommandType.DeadlockPriority] = "DEADLOCK_PRIORITY",
    };

    private static readonly (SetOptions Flag, string Token)[] TrackedFlags =
    {
        (SetOptions.AnsiWarnings, "ANSI_WARNINGS"),
        (SetOptions.AnsiPadding, "ANSI_PADDING"),
        (SetOptions.ArithAbort, "ARITHABORT"),
        (SetOptions.ArithIgnore, "ARITHIGNORE"),
        (SetOptions.ConcatNullYieldsNull, "CONCAT_NULL_YIELDS_NULL"),
        (SetOptions.AnsiNullDfltOn, "ANSI_NULL_DFLT_ON"),
        (SetOptions.NumericRoundAbort, "NUMERIC_ROUNDABORT"),
        (SetOptions.CursorCloseOnCommit, "CURSOR_CLOSE_ON_COMMIT"),
        (SetOptions.ImplicitTransactions, "IMPLICIT_TRANSACTIONS"),
    };

    /// <summary>Options an executed SET SU touched that the tracker has no baseline
    /// for — surfaced once as a console note (restore is skipped for them).</summary>
    public IReadOnlyCollection<string> UntrackedOptionsSeen => _untrackedSeen;
    private readonly HashSet<string> _untrackedSeen = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Push-time snapshot for the §11.2 pop diff.</summary>
    public IReadOnlyDictionary<string, string> Snapshot() => new Dictionary<string, string>(_current, StringComparer.OrdinalIgnoreCase);

    /// <summary>C13 (§11.2): the session's current <c>SET ROWCOUNT</c> literal ("0" = unlimited).
    /// The debuggee's limit persists on our one connection and correctly limits the debuggee's own
    /// statements — but it must NOT truncate the debugger's own multi-row bookkeeping (the TVP copy
    /// a table-type argument/formal needs, §9/§11.3). Those copies reset ROWCOUNT to 0 and restore
    /// this value afterwards; there is no intrinsic to read the setting back at runtime, so the
    /// value is threaded from here into the composed batch (and the §11.3 push seed).</summary>
    public string CurrentRowCount => _current["ROWCOUNT"];

    /// <summary>C13 (F2): record a SET ROWCOUNT value the caller resolved itself — a non-literal
    /// <c>SET ROWCOUNT @v</c> / <c>SET ROWCOUNT @a+1</c> whose value the AST-only
    /// <see cref="RecordExecuted"/> cannot read. Same effect as a recorded literal: the value is
    /// re-applied around the debuggee's later statements and reverted at a callee's exit.</summary>
    public void SetRowCount(string value) => _current["ROWCOUNT"] = value;

    /// <summary>Folds one successfully-executed SET SU into the tracked state.</summary>
    public void RecordExecuted(TSqlStatement statement)
    {
        switch (statement)
        {
            case PredicateSetStatement { Options: var options, IsOn: var isOn }:
                var matchedAny = false;
                foreach (var (flag, token) in TrackedFlags)
                {
                    if ((options & flag) != 0)
                    {
                        _current[token] = isOn ? "ON" : "OFF";
                        matchedAny = true;
                    }
                }

                // QI/ANSI_NULLS/NOCOUNT/XACT_ABORT arrive here too; they are handled
                // elsewhere (see class remarks) and are not "untracked".
                const SetOptions handledElsewhere = SetOptions.QuotedIdentifier | SetOptions.AnsiNulls
                    | SetOptions.NoCount | SetOptions.XactAbort | SetOptions.AnsiDefaults;
                if (!matchedAny && (options & ~handledElsewhere) != 0)
                {
                    _untrackedSeen.Add(options.ToString());
                }

                break;

            case SetTransactionIsolationLevelStatement { Level: var level }:
                _current[IsolationKey] = level switch
                {
                    IsolationLevel.ReadUncommitted => "READ UNCOMMITTED",
                    IsolationLevel.ReadCommitted => "READ COMMITTED",
                    IsolationLevel.RepeatableRead => "REPEATABLE READ",
                    IsolationLevel.Serializable => "SERIALIZABLE",
                    IsolationLevel.Snapshot => "SNAPSHOT",
                    _ => _current[IsolationKey],
                };
                break;

            // A53: value options. Each restores uniformly as `SET <name> <value>` (the
            // RestoreStatements default arm), so only the current value need be tracked. Only
            // a LITERAL value (integer, or an identifier like mdy / us_english / LOW) is
            // recorded — a rare `SET LOCK_TIMEOUT @v` leaves the tracked value untouched
            // rather than store an un-restorable reference.
            case SetCommandStatement { Commands: var commands }:
                foreach (var command in commands)
                {
                    if (command is GeneralSetCommand { CommandType: var commandType, Parameter: Literal { Value: { } value } }
                        && ValueCommandNames.TryGetValue(commandType, out var optionName))
                    {
                        _current[optionName] = value;
                    }
                }

                break;

            case SetTextSizeStatement { TextSize: Literal { Value: { } textSize } }:
                _current["TEXTSIZE"] = textSize;
                break;

            case SetRowCountStatement { NumberRows: Literal { Value: { } rowCount } }:
                _current["ROWCOUNT"] = rowCount;
                break;
        }
    }

    /// <summary>
    /// §11.2 pop: the SET statements that revert the connection to
    /// <paramref name="atEntry"/> (the callee's push-time snapshot). Applying them also
    /// folds the restored values back into the tracked state.
    /// </summary>
    public IReadOnlyList<string> RestoreStatements(IReadOnlyDictionary<string, string> atEntry)
    {
        var restores = new List<string>();
        foreach (var (option, entryValue) in atEntry)
        {
            if (!_current.TryGetValue(option, out var currentValue) || currentValue == entryValue)
            {
                continue;
            }

            restores.Add(option == IsolationKey
                ? $"SET TRANSACTION ISOLATION LEVEL {entryValue};"
                : $"SET {option} {entryValue};");
            _current[option] = entryValue;
        }

        return restores;
    }
}
