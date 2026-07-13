// M4 (Fable) — DESIGN §11.2/D6 (fact 9): runtime SET options tracked from executed
// SUs, diffed against a push-time snapshot to emit the pop's restoring SETs.
using Microsoft.SqlServer.TransactSql.ScriptDom;
using TsqlDbg.Core.Parsing;
using TsqlDbg.Core.Sessions;
using Xunit;

namespace TsqlDbg.Core.Tests.Sessions;

public sealed class RuntimeOptionTrackerTests
{
    private static TSqlStatement Parse(string sql)
    {
        var fragment = ScriptParser.Parse(sql, initialQuotedIdentifiers: true, compatLevel: 150, out var errors);
        Assert.Empty(errors);
        return ((TSqlScript)fragment).Batches[0].Statements[0];
    }

    [Fact]
    public void ChangedOnOffOption_IsRestoredToSnapshotValue()
    {
        var tracker = new RuntimeOptionTracker();
        var atEntry = tracker.Snapshot();
        tracker.RecordExecuted(Parse("SET ARITHABORT ON;"));

        var restores = tracker.RestoreStatements(atEntry);

        Assert.Equal(new[] { "SET ARITHABORT OFF;" }, restores);
        // Applying the restore folded the value back — a second diff is clean.
        Assert.Empty(tracker.RestoreStatements(atEntry));
    }

    [Fact]
    public void IsolationLevel_IsRestored()
    {
        var tracker = new RuntimeOptionTracker();
        var atEntry = tracker.Snapshot();
        tracker.RecordExecuted(Parse("SET TRANSACTION ISOLATION LEVEL SERIALIZABLE;"));

        Assert.Equal(new[] { "SET TRANSACTION ISOLATION LEVEL READ COMMITTED;" }, tracker.RestoreStatements(atEntry));
    }

    [Fact]
    public void UnchangedOptions_EmitNothing()
    {
        var tracker = new RuntimeOptionTracker();
        var atEntry = tracker.Snapshot();
        tracker.RecordExecuted(Parse("SET ANSI_WARNINGS ON;"));       // already the baseline

        Assert.Empty(tracker.RestoreStatements(atEntry));
    }

    [Fact]
    public void NestedPops_RestoreToTheirOwnEntryState()
    {
        // caller sets SERIALIZABLE, pushes; callee sets SNAPSHOT, pops → restore to
        // SERIALIZABLE (the callee's entry), not to the session default.
        var tracker = new RuntimeOptionTracker();
        tracker.RecordExecuted(Parse("SET TRANSACTION ISOLATION LEVEL SERIALIZABLE;"));
        var calleeEntry = tracker.Snapshot();
        tracker.RecordExecuted(Parse("SET TRANSACTION ISOLATION LEVEL SNAPSHOT;"));

        Assert.Equal(new[] { "SET TRANSACTION ISOLATION LEVEL SERIALIZABLE;" }, tracker.RestoreStatements(calleeEntry));
    }

    [Fact]
    public void XactAbortQiAnsiNullsNoCount_AreHandledElsewhere_NotTrackedHere()
    {
        var tracker = new RuntimeOptionTracker();
        var atEntry = tracker.Snapshot();
        tracker.RecordExecuted(Parse("SET XACT_ABORT ON;"));          // per-frame + F5 preamble
        tracker.RecordExecuted(Parse("SET NOCOUNT OFF;"));            // preamble-forced (C5)

        Assert.Empty(tracker.RestoreStatements(atEntry));
        Assert.Empty(tracker.UntrackedOptionsSeen);                   // known-elsewhere ≠ untracked
    }

    // ---- A53: value-carrying options -------------------------------------------------

    [Fact]
    public void ValueOptions_SeededWithTheLiveVerifiedConnectionDefaults()
    {
        var snap = new RuntimeOptionTracker().Snapshot();

        Assert.Equal("7", snap["DATEFIRST"]);
        Assert.Equal("mdy", snap["DATEFORMAT"]);
        Assert.Equal("us_english", snap["LANGUAGE"]);
        Assert.Equal("-1", snap["LOCK_TIMEOUT"]);
        Assert.Equal("NORMAL", snap["DEADLOCK_PRIORITY"]);
        Assert.Equal("-1", snap["TEXTSIZE"]);
        Assert.Equal("0", snap["ROWCOUNT"]);
    }

    [Theory]
    [InlineData("SET DATEFIRST 1;", "SET DATEFIRST 7;")]                            // integer, GeneralSetCommand
    [InlineData("SET LOCK_TIMEOUT 5000;", "SET LOCK_TIMEOUT -1;")]                  // integer
    [InlineData("SET DATEFORMAT dmy;", "SET DATEFORMAT mdy;")]                      // identifier value
    [InlineData("SET LANGUAGE Deutsch;", "SET LANGUAGE us_english;")]              // identifier value
    [InlineData("SET DEADLOCK_PRIORITY LOW;", "SET DEADLOCK_PRIORITY NORMAL;")]     // identifier value
    [InlineData("SET TEXTSIZE 2048;", "SET TEXTSIZE -1;")]                          // SetTextSizeStatement
    [InlineData("SET ROWCOUNT 100;", "SET ROWCOUNT 0;")]                            // SetRowCountStatement
    public void ValueOption_IsRecorded_AndRestoredToItsSeededDefault(string setSql, string expectedRestore)
    {
        var tracker = new RuntimeOptionTracker();
        var atEntry = tracker.Snapshot();
        tracker.RecordExecuted(Parse(setSql));

        Assert.Equal(new[] { expectedRestore }, tracker.RestoreStatements(atEntry));
        Assert.Empty(tracker.RestoreStatements(atEntry));            // applying the restore folds it back
    }

    [Fact]
    public void ValueOption_NestedPop_RestoresToTheCallersEntryValue_NotTheSeed()
    {
        // caller sets DATEFIRST 3, pushes; callee sets 5, pops → restore to 3 (the callee's
        // entry), exactly like the isolation case.
        var tracker = new RuntimeOptionTracker();
        tracker.RecordExecuted(Parse("SET DATEFIRST 3;"));
        var calleeEntry = tracker.Snapshot();
        tracker.RecordExecuted(Parse("SET DATEFIRST 5;"));

        Assert.Equal(new[] { "SET DATEFIRST 3;" }, tracker.RestoreStatements(calleeEntry));
    }

    [Fact]
    public void ValueOption_WithAVariableValue_IsNotStored_RatherThanStoreAnUnrestorableReference()
    {
        var tracker = new RuntimeOptionTracker();
        var atEntry = tracker.Snapshot();
        tracker.RecordExecuted(Parse("SET LOCK_TIMEOUT @ms;"));      // non-literal → left at the default

        Assert.Empty(tracker.RestoreStatements(atEntry));
    }
}
