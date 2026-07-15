// M5 I6 (§12.3 REPL, A17 — design note §2, docs/archive/reviews/m5-inspection-design-notes-
// fable.md): Session.EvaluateReplAsync driven end-to-end through the §10.4 state
// matrix (healthy/doomed/detached/broken) x read/write, completing the §4.3
// enforcement-matrix acceptance criterion (the pure whitelist half lives in
// ReplWhitelistTests.cs) — refusals asserted by message class, allowed paths
// asserted by composed-batch shape (DebuggerInitiated sandwich, doomed redoom
// prefix, protection re-open call, trailing probe presence). Also the I9 pins:
// NeverArmsThePreflight mirror, fault-reported-not-thrown (F5), and the doomed-read
// shape against the fake executor.
using TsqlDbg.Core.Execution;
using TsqlDbg.Core.Sessions;
using TsqlDbg.Core.Tests.Fakes;
using Xunit;

namespace TsqlDbg.Core.Tests.Sessions;

public sealed class SessionReplTests
{
    private static BatchResult Ok(int trancount = 1, int xactState = 0, object?[]? state = null)
    {
        var sets = new List<ResultSet>
        {
            new(new[] { "__dbg_ctl", "ok", "rc", "scope_identity", "trancount", "xact_state" },
                new IReadOnlyList<object?>[] { new object?[] { 1, true, null, null, trancount, xactState } }),
        };
        if (state is not null)
        {
            sets.Add(StateSet(state));
        }

        return new BatchResult(sets, Array.Empty<string>());
    }

    private static ResultSet StateSet(object?[] values)
    {
        var columns = new string[values.Length + 1];
        var row = new object?[values.Length + 1];
        columns[0] = "__dbg_state";
        row[0] = 1;
        for (var i = 0; i < values.Length; i++)
        {
            columns[i + 1] = $"c{i}";
            row[i + 1] = values[i];
        }

        return new ResultSet(columns, new IReadOnlyList<object?>[] { row });
    }

    private static BatchResult DoomFault()
        => new(new List<ResultSet>
        {
            new(new[] { "__dbg_ctl", "ok", "err_number", "err_severity", "err_state", "err_line", "err_procedure", "err_message", "trancount", "xact_state" },
                new IReadOnlyList<object?>[] { new object?[] { 1, false, 8134, 16, 1, 1, null, "Divide by zero error encountered.", 1, -1 } }),
        }, Array.Empty<string>());

    private static BatchResult UserSelect(string column, object? value)
        => new(new List<ResultSet> { new(new[] { column }, new IReadOnlyList<object?>[] { new object?[] { value } }) }, Array.Empty<string>());

    private static BatchResult ReplFault(int number, int severity, int state, int? line, string? procedure, string message)
        => new(new List<ResultSet>
        {
            new(new[] { "__dbg_repl_err", "err_number", "err_severity", "err_state", "err_line", "err_procedure", "err_message" },
                new IReadOnlyList<object?>[] { new object?[] { 1, number, severity, state, line, procedure, message } }),
        }, Array.Empty<string>());

    private static BatchResult Empty() => new(Array.Empty<ResultSet>(), Array.Empty<string>());

    private static BatchResult WithProbe(BatchResult result, int trancount, int xactState)
    {
        var probeSet = new ResultSet(
            new[] { "__dbg_repl_probe", "trancount", "xact_state" },
            new IReadOnlyList<object?>[] { new object?[] { 1, trancount, xactState } });
        return result with { ResultSets = result.ResultSets.Append(probeSet).ToList() };
    }

    private static Session ScriptSession(string script, FakeStatementExecutor executor, bool allowConsoleWrites = false)
        => new(new SessionOptions("DEVSQL01", "SalesDb", LaunchMode.Script, null, null, script, AllowConsoleWrites: allowConsoleWrites), executor);

    private static FakeStatementExecutor Init(FakeStatementExecutor executor)
        => executor.ThenEmpty().ThenEmpty().ThenEmpty().ThenEmpty();   // SET opts, CREATE state, seed, BEGIN TRAN

    // ---- Healthy -----------------------------------------------------------------

    [Fact]
    public async Task Healthy_Read_Allowed_RendersResultSet_NoTrailingProbe()
    {
        var executor = Init(new FakeStatementExecutor()).Then(_ => UserSelect("x", 42));
        var session = ScriptSession("SELECT 1;", executor);
        await session.InitializeAsync();
        var frame = session.Frames[0];

        var result = await session.EvaluateReplAsync(frame, "SELECT 1 AS x;");

        Assert.Equal(Session.ReplOutcome.Rendered, result.Outcome);
        Assert.Contains("42", result.Rendered);
        Assert.Contains("SET XACT_ABORT OFF", executor.ReceivedBatches[^1]);   // DebuggerInitiated sandwich
        Assert.DoesNotContain("__dbg_repl_probe", executor.ReceivedBatches[^1]);
        Assert.False(result.TableContentChanged);   // A61: a pure read refetches nothing
        await session.TeardownAsync();
    }

    [Fact]
    public async Task Healthy_Write_Allowed_IncludesTrailingProbe()
    {
        var executor = Init(new FakeStatementExecutor()).Then(_ => WithProbe(Empty(), trancount: 1, xactState: 0));
        var session = ScriptSession("SELECT 1;", executor, allowConsoleWrites: true);
        await session.InitializeAsync();
        var frame = session.Frames[0];

        var result = await session.EvaluateReplAsync(frame, "INSERT INTO dbo.T (a) VALUES (1);");

        Assert.Equal(Session.ReplOutcome.Rendered, result.Outcome);
        Assert.Contains("__dbg_repl_probe", executor.ReceivedBatches[^1]);
        Assert.True(result.TableContentChanged);   // A61: a non-variable write → the adapter refetches Temp Tables
        Assert.False(result.VariablesChanged);      // no variable write-back for a bare table write
        Assert.False(session.IsTransactionDetached);   // probe read trancount=1 -> no edge
        await session.TeardownAsync();
    }

    [Fact]
    public async Task Print_SurfacesMessageStream_ToConsole()
    {
        // §12.3 "messages inline": PRINT text arrives on the batch's message stream
        // (InfoMessage, fact 18) — it must render to the console, not be swallowed.
        var executor = Init(new FakeStatementExecutor())
            .Then(_ => WithProbe(Empty(), trancount: 1, xactState: 0) with { Messages = new[] { "OK" } });
        var session = ScriptSession("SELECT 1;", executor, allowConsoleWrites: true);
        await session.InitializeAsync();
        var frame = session.Frames[0];

        var result = await session.EvaluateReplAsync(frame, "PRINT 'OK';");

        Assert.Equal(Session.ReplOutcome.Rendered, result.Outcome);
        Assert.Contains("OK", result.Rendered);
        Assert.DoesNotContain("no result sets", result.Rendered);
        await session.TeardownAsync();
    }

    [Fact]
    public async Task NoOutput_RendersEmpty_NotNoResultSetsFiller()
    {
        // A write with no result set and no message stream (NOCOUNT forced, C5) renders
        // as empty — the old "(no result sets)" filler is gone; the effect surfaces via
        // the Variables / Temp Tables refresh instead.
        var executor = Init(new FakeStatementExecutor())
            .Then(_ => WithProbe(Empty(), trancount: 1, xactState: 0));
        var session = ScriptSession("SELECT 1;", executor, allowConsoleWrites: true);
        await session.InitializeAsync();
        var frame = session.Frames[0];

        var result = await session.EvaluateReplAsync(frame, "INSERT INTO dbo.T (a) VALUES (1);");

        Assert.Equal(Session.ReplOutcome.Rendered, result.Outcome);
        Assert.Equal(string.Empty, result.Rendered);
        await session.TeardownAsync();
    }

    // M6 R4 (design note §5-R4): the "flip" positive test — only the no-flip case
    // (above) was pinned at the M4/M5 gate. A console write can move the transaction
    // (e.g. a DML statement whose AFTER TRIGGER contains a ROLLBACK — invisible to the
    // REPL whitelist, which only inspects the statement's own AST), and the trailing
    // probe feeds that edge through the SAME ObserveControlRowAsync path a debuggee
    // control row would. Asserts the §10.4 side effects fire for real: IsTransactionDetached
    // flips, and every frame's state table is reseeded from the snapshot (A9) — here
    // observable as the reseed UPDATE against frame 0's own state table.
    [Fact]
    public async Task Write_TrailingProbeFlipsToDetached_ReseedsFromSnapshot()
    {
        const string script = "DECLARE @x int = 1;\nSELECT 1;";
        var executor = Init(new FakeStatementExecutor())
            .Then(_ => Ok(state: new object?[] { 1 }));           // DECLARE initializer seeds frame0.Snapshot
        var session = ScriptSession(script, executor, allowConsoleWrites: true);
        await session.InitializeAsync();
        await session.StepAsync();                                // DECLARE @x = 1
        Assert.False(session.IsTransactionDetached);
        var frame = session.Frames[0];

        executor
            .Then(_ => WithProbe(Empty(), trancount: 0, xactState: 0))   // the write itself; its trigger rolled back
            .ThenEmpty();                                          // A9 reseed: UPDATE #__dbg_s0 SET [x] = @p (frame 0, 1 variable)
        var result = await session.EvaluateReplAsync(frame, "INSERT INTO dbo.T (a) VALUES (1);");

        Assert.Equal(Session.ReplOutcome.Rendered, result.Outcome);
        Assert.True(session.IsTransactionDetached);
        Assert.Contains("UPDATE #__dbg_s0 SET", executor.ReceivedBatches[^1]);   // A9's reseed, from the snapshot
        Assert.Equal(1, executor.ReceivedParameters[executor.ReceivedBatches.Count - 1][0].Value);
        await session.TeardownAsync();
    }

    [Fact]
    public async Task Healthy_Write_Refused_WhenAllowConsoleWritesFalse()
    {
        var executor = Init(new FakeStatementExecutor());
        var session = ScriptSession("SELECT 1;", executor, allowConsoleWrites: false);
        await session.InitializeAsync();
        var frame = session.Frames[0];
        var batchesBefore = executor.ReceivedBatches.Count;

        var result = await session.EvaluateReplAsync(frame, "INSERT INTO dbo.T (a) VALUES (1);");

        Assert.Equal(Session.ReplOutcome.Refused, result.Outcome);
        Assert.Contains("read-only", result.RefusalMessage);
        Assert.Equal(batchesBefore, executor.ReceivedBatches.Count);
        await session.TeardownAsync();
    }

    // ---- A45: frame-variable seeding (docs/archive/reviews/repl-variable-seed-opus.md) ------

    // The report: `SELECT @x` in the console returned error 137 ("must declare @x")
    // because BuildForRepl never declared/seeded the frame's variables. A45 seeds them
    // read-only, mirroring Build's interpreted-statement arms. Healthy = a plain
    // language-batch read from the frame's state table (NO parameters — so a
    // console-created #temp still persists).
    [Fact]
    public async Task Healthy_Read_SeedsFrameVariables_FromStateTable_NoParameters()
    {
        const string script = "DECLARE @x int = 42;\nSELECT 1;";
        var executor = Init(new FakeStatementExecutor())
            .Then(_ => Ok(state: new object?[] { 42 }))    // DECLARE @x = 42 -> frame0 state/snapshot
            .Then(_ => UserSelect("x", 42));                // the REPL read itself
        var session = ScriptSession(script, executor);
        await session.InitializeAsync();
        await session.StepAsync();                          // DECLARE @x = 42
        var frame = session.Frames[0];

        var result = await session.EvaluateReplAsync(frame, "SELECT @x AS x;");

        Assert.Equal(Session.ReplOutcome.Rendered, result.Outcome);
        var lastBatch = executor.ReceivedBatches[^1];
        Assert.Contains("DECLARE @x", lastBatch);                            // the frame var is declared
        Assert.Contains("SELECT @x = [x] FROM #__dbg_s0;", lastBatch);       // ...and seeded read-only from the state table
        Assert.DoesNotContain("CONVERT", lastBatch);                         // healthy -> table read, not a param seed
        Assert.False(executor.ReceivedParameters.ContainsKey(executor.ReceivedBatches.Count - 1));  // plain language batch
        await session.TeardownAsync();
    }

    // Doomed: the state table is stale/unwritable under 3930 (§10.4), so the seed rides
    // parameters from the binary snapshot — the SAME redoom + param shape the debuggee
    // doomed batch already uses (Build's doomed arm). The parameter carries the live
    // value; the batch CONVERTs it to the declared type.
    [Fact]
    public async Task Doomed_Read_SeedsFrameVariables_FromSnapshot_ViaParameters()
    {
        // 1/0 inside TRY/CATCH so the doom is CAUGHT (session doomed-alive, not broken).
        const string script = """
            DECLARE @x int = 5;
            BEGIN TRY
                SELECT 1/0;
            END TRY
            BEGIN CATCH
                SELECT 1;
            END CATCH
            """;
        var executor = Init(new FakeStatementExecutor())
            .Then(_ => Ok(state: new object?[] { 5 }))     // DECLARE @x = 5 -> frame0.Snapshot = [5]
            .Then(_ => DoomFault());                        // 1/0 dooms, routes to CATCH
        var session = ScriptSession(script, executor);
        await session.InitializeAsync();
        await session.StepAsync();                          // DECLARE @x = 5
        await session.StepAsync();                          // 1/0 -> doomed, cursor at CATCH
        Assert.True(session.IsDoomed);
        var frame = session.Frames[0];

        executor.Then(_ => UserSelect("x", 5));             // the doomed console read
        var result = await session.EvaluateReplAsync(frame, "SELECT @x AS x;");

        Assert.Equal(Session.ReplOutcome.Rendered, result.Outcome);
        var lastIndex = executor.ReceivedBatches.Count - 1;
        var lastBatch = executor.ReceivedBatches[lastIndex];
        Assert.Contains("DECLARE @x", lastBatch);                    // the frame var is declared
        Assert.Contains("@x = CONVERT(", lastBatch);                 // ...and seeded from a parameter (doomed)
        Assert.Contains("BEGIN TRANSACTION;", lastBatch);            // redoom prefix present
        Assert.True(executor.ReceivedParameters.ContainsKey(lastIndex));   // sp_executesql param transport
        Assert.Equal(5, executor.ReceivedParameters[lastIndex][0].Value);  // the snapshot value
        await session.TeardownAsync();
    }

    // ---- A46: write-mode variable persistence (docs/archive/reviews/repl-variable-seed-opus.md) ----

    // Under allowConsoleWrites, a console SET @x persists like an interpreted statement:
    // the batch writes the variable back to the state table and reads it back in a
    // __dbg_state set, which refreshes Frame.Snapshot. (Ivan's ruling: writes are gated
    // on allowConsoleWrites; read-only stays read-only — see the ReadOnly test below.)
    [Fact]
    public async Task WriteMode_SetVariable_WritesBackAndRefreshesSnapshot()
    {
        const string script = "DECLARE @x int = 1;\nSELECT 1;";
        var executor = Init(new FakeStatementExecutor())
            .Then(_ => Ok(state: new object?[] { 1 }))    // DECLARE @x = 1
            .Then(_ => new BatchResult(new List<ResultSet> { StateSet(new object?[] { 99 }) }, Array.Empty<string>()));  // SET @x=99 read-back
        var session = ScriptSession(script, executor, allowConsoleWrites: true);
        await session.InitializeAsync();
        await session.StepAsync();                         // DECLARE @x = 1
        var frame = session.Frames[0];

        var result = await session.EvaluateReplAsync(frame, "SET @x = 99;");

        Assert.Equal(Session.ReplOutcome.Rendered, result.Outcome);
        var lastBatch = executor.ReceivedBatches[^1];
        Assert.Contains("UPDATE #__dbg_s0 SET", lastBatch);       // A46: write-back to the state table
        Assert.Contains("__dbg_state", lastBatch);                // A46: snapshot read-back
        Assert.True(result.VariablesChanged);                     // session refreshed from the read-back
        Assert.False(result.TableContentChanged);                 // A61: a variable-only SET touches no table
        Assert.Equal(99, Convert.ToInt32(frame.Snapshot![0]));    // Frame.Snapshot now holds the new value
        await session.TeardownAsync();
    }

    // A61 (the reported scenario): a `DELETE TOP(1) FROM @tv` in the console changes the
    // table variable's contents (persisted to the real backing object, A46) but writes no
    // scalar variable — so it must flag TableContentChanged (adapter drops the Temp Tables
    // rowcount cache and refetches) WITHOUT flagging VariablesChanged.
    [Fact]
    public async Task WriteMode_TableVariableDelete_SetsTableContentChanged_NotVariablesChanged()
    {
        var executor = Init(new FakeStatementExecutor()).Then(_ => WithProbe(Empty(), trancount: 1, xactState: 0));
        var session = ScriptSession("SELECT 1;", executor, allowConsoleWrites: true);
        await session.InitializeAsync();
        var frame = session.Frames[0];

        var result = await session.EvaluateReplAsync(frame, "DELETE TOP(1) FROM @tv;");

        Assert.Equal(Session.ReplOutcome.Rendered, result.Outcome);
        Assert.True(result.TableContentChanged);
        Assert.False(result.VariablesChanged);
        await session.TeardownAsync();
    }

    // The gate: in read-only mode (allowConsoleWrites:false) an assignment SELECT is
    // allowed to run (it's a SELECT) but must NOT persist — no write-back is emitted, so
    // the debuggee's @x is untouched. This is what keeps "read-only" honest.
    [Fact]
    public async Task ReadOnly_AssignmentSelect_RunsButDoesNotWriteBack()
    {
        const string script = "DECLARE @x int = 1;\nSELECT 1;";
        var executor = Init(new FakeStatementExecutor())
            .Then(_ => Ok(state: new object?[] { 1 }))    // DECLARE @x = 1
            .Then(_ => Empty());                           // the assignment select (no result set)
        var session = ScriptSession(script, executor, allowConsoleWrites: false);
        await session.InitializeAsync();
        await session.StepAsync();
        var frame = session.Frames[0];

        var result = await session.EvaluateReplAsync(frame, "SELECT @x = 99;");

        Assert.Equal(Session.ReplOutcome.Rendered, result.Outcome);
        Assert.DoesNotContain("UPDATE #__dbg_s0", executor.ReceivedBatches[^1]);   // read-only: no write-back
        Assert.False(result.VariablesChanged);
        await session.TeardownAsync();
    }

    // ---- Detached ------------------------------------------------------------------

    [Fact]
    public async Task Detached_Read_Allowed_Autocommit_NoProtectionReopen()
    {
        const string script = "ROLLBACK;\nSELECT 1;";
        var executor = Init(new FakeStatementExecutor())
            .Then(_ => Ok(trancount: 0))    // ROLLBACK -> detached edge
            .Then(_ => UserSelect("x", 7)); // the REPL read itself
        var session = ScriptSession(script, executor);
        await session.InitializeAsync();
        await session.StepAsync();          // ROLLBACK -> detached
        Assert.True(session.IsTransactionDetached);
        var frame = session.Frames[0];
        var batchesBefore = executor.ReceivedBatches.Count;

        var result = await session.EvaluateReplAsync(frame, "SELECT 1 AS x;");

        Assert.Equal(Session.ReplOutcome.Rendered, result.Outcome);
        Assert.Equal(batchesBefore + 1, executor.ReceivedBatches.Count);   // exactly the read, no BEGIN TRAN
        Assert.True(session.IsTransactionDetached);
        await session.TeardownAsync();
    }

    [Fact]
    public async Task Detached_Write_ReopensProtectionFirst_ThenRuns_WithConsoleNote()
    {
        const string script = "ROLLBACK;\nSELECT 1;";
        var executor = Init(new FakeStatementExecutor())
            .Then(_ => Ok(trancount: 0))    // ROLLBACK -> detached edge
            .ThenEmpty()                    // protection re-open BEGIN TRANSACTION
            .Then(_ => WithProbe(Empty(), trancount: 1, xactState: 0));   // the write itself
        var session = ScriptSession(script, executor, allowConsoleWrites: true);
        await session.InitializeAsync();
        await session.StepAsync();
        Assert.True(session.IsTransactionDetached);
        var frame = session.Frames[0];
        var batchesBefore = executor.ReceivedBatches.Count;

        var result = await session.EvaluateReplAsync(frame, "INSERT INTO dbo.T (a) VALUES (1);");

        Assert.Equal(Session.ReplOutcome.Rendered, result.Outcome);
        Assert.Equal(batchesBefore + 2, executor.ReceivedBatches.Count);   // BEGIN TRAN + the write
        Assert.Equal("BEGIN TRANSACTION;", executor.ReceivedBatches[^2]);
        Assert.False(session.IsTransactionDetached);   // resurrected
        Assert.Contains("C24", result.Rendered);        // console note citing C24 is part of the rendered output
        await session.TeardownAsync();
    }

    // ---- Doomed --------------------------------------------------------------------

    private const string DoomedScript = """
        BEGIN TRY
            SELECT 1/0;
        END TRY
        BEGIN CATCH
            SELECT 1;
        END CATCH
        """;

    [Fact]
    public async Task Doomed_Read_Allowed_UsesRedoomShape_NoProbe()
    {
        var executor = Init(new FakeStatementExecutor())
            .Then(_ => DoomFault())         // 1/0 dooms, routes to CATCH
            .Then(_ => UserSelect("x", 1)); // the doomed read itself
        var session = ScriptSession(DoomedScript, executor);
        await session.InitializeAsync();
        await session.StepAsync();
        Assert.True(session.IsDoomed);
        var frame = session.Frames[0];

        var result = await session.EvaluateReplAsync(frame, "SELECT 1 AS x;");

        Assert.Equal(Session.ReplOutcome.Rendered, result.Outcome);
        var lastBatch = executor.ReceivedBatches[^1];
        Assert.Contains("BEGIN TRANSACTION;", lastBatch);       // redoom prefix
        Assert.Contains("1/0", lastBatch);                      // the redoom "caught error" line
        Assert.DoesNotContain("__dbg_repl_probe", lastBatch);    // read: no trailing probe
        await session.TeardownAsync();
    }

    [Fact]
    public async Task Doomed_Write_Refused_Native3930Truth_NoRoundTrip()
    {
        var executor = Init(new FakeStatementExecutor())
            .Then(_ => DoomFault());
        var session = ScriptSession(DoomedScript, executor, allowConsoleWrites: true);
        await session.InitializeAsync();
        await session.StepAsync();
        Assert.True(session.IsDoomed);
        var frame = session.Frames[0];
        var batchesBefore = executor.ReceivedBatches.Count;

        var result = await session.EvaluateReplAsync(frame, "INSERT INTO dbo.T (a) VALUES (1);");

        Assert.Equal(Session.ReplOutcome.Refused, result.Outcome);
        Assert.Contains("XACT_STATE() = -1", result.RefusalMessage);
        Assert.Contains("3930", result.RefusalMessage);
        Assert.Equal(batchesBefore, executor.ReceivedBatches.Count);   // refused BEFORE sending
        await session.TeardownAsync();
    }

    // A46: while doomed, a variable-only SET @x is ALLOWED (it touches no table, so no
    // 3930) and persists to Frame.Snapshot via the __dbg_state read-back — the state-table
    // write-back is XACT_STATE-guarded and self-skips. (A database write stays refused —
    // Doomed_Write_Refused_Native3930Truth_NoRoundTrip above.)
    [Fact]
    public async Task Doomed_SetVariable_Allowed_PersistsToSnapshot()
    {
        const string script = """
            DECLARE @x int = 5;
            BEGIN TRY
                SELECT 1/0;
            END TRY
            BEGIN CATCH
                SELECT 1;
            END CATCH
            """;
        var executor = Init(new FakeStatementExecutor())
            .Then(_ => Ok(state: new object?[] { 5 }))    // DECLARE @x = 5
            .Then(_ => DoomFault());                       // 1/0 dooms, routes to CATCH
        var session = ScriptSession(script, executor, allowConsoleWrites: true);
        await session.InitializeAsync();
        await session.StepAsync();                         // DECLARE @x = 5
        await session.StepAsync();                         // 1/0 -> doomed
        Assert.True(session.IsDoomed);
        var frame = session.Frames[0];

        // The doomed console SET returns its read-back as __dbg_state = [88].
        executor.Then(_ => new BatchResult(new List<ResultSet> { StateSet(new object?[] { 88 }) }, Array.Empty<string>()));
        var result = await session.EvaluateReplAsync(frame, "SET @x = 88;");

        Assert.Equal(Session.ReplOutcome.Rendered, result.Outcome);   // NOT refused while doomed
        Assert.True(result.VariablesChanged);
        Assert.Equal(88, Convert.ToInt32(frame.Snapshot![0]));        // persisted to the doomed-mode source
        var lastBatch = executor.ReceivedBatches[^1];
        Assert.Contains("XACT_STATE() <> -1", lastBatch);             // the write-back UPDATE self-skips while doomed
        Assert.Contains("__dbg_state", lastBatch);                    // the read-back that carries the value
        Assert.Contains("BEGIN TRANSACTION;", lastBatch);             // redoom prefix (doomed shape)
        await session.TeardownAsync();
    }

    // ---- Broken ----------------------------------------------------------------------

    [Fact]
    public async Task Broken_ReadAndWrite_BothRefused()
    {
        var executor = Init(new FakeStatementExecutor())
            .Then(_ => throw new StatementExecutionException("compile error", 16, 999)); // sameScopeUncatchable, terminal
        var session = ScriptSession("SELECT 1/0;", executor, allowConsoleWrites: true);
        await session.InitializeAsync();
        await session.StepAsync();
        Assert.True(session.IsBroken);
        var frame = session.Frames[0];

        var readResult = await session.EvaluateReplAsync(frame, "SELECT 1;");
        Assert.Equal(Session.ReplOutcome.Refused, readResult.Outcome);
        Assert.Contains("terminated", readResult.RefusalMessage);

        var writeResult = await session.EvaluateReplAsync(frame, "INSERT INTO dbo.T (a) VALUES (1);");
        Assert.Equal(Session.ReplOutcome.Refused, writeResult.Outcome);
        Assert.Contains("terminated", writeResult.RefusalMessage);
        await session.TeardownAsync();
    }

    // ---- Whitelist edge cases (session-level: one-statement + parse error) ---------

    [Fact]
    public async Task MultipleStatements_Refused()
    {
        var executor = Init(new FakeStatementExecutor());
        var session = ScriptSession("SELECT 1;", executor);
        await session.InitializeAsync();
        var frame = session.Frames[0];

        var result = await session.EvaluateReplAsync(frame, "SELECT 1; SELECT 2;");

        Assert.Equal(Session.ReplOutcome.Refused, result.Outcome);
        Assert.Contains("one statement at a time", result.RefusalMessage);
        await session.TeardownAsync();
    }

    [Fact]
    public async Task UnparseableText_Refused()
    {
        var executor = Init(new FakeStatementExecutor());
        var session = ScriptSession("SELECT 1;", executor);
        await session.InitializeAsync();
        var frame = session.Frames[0];

        var result = await session.EvaluateReplAsync(frame, "SELECT FROM WHERE;;;");

        Assert.Equal(Session.ReplOutcome.Refused, result.Outcome);
        Assert.Contains("could not parse", result.RefusalMessage);
        await session.TeardownAsync();
    }

    // ---- I9 pins ---------------------------------------------------------------------

    [Fact]
    public async Task Fault_ReportedNotThrown_F5Contract()
    {
        var executor = Init(new FakeStatementExecutor())
            .Then(_ => ReplFault(207, 16, 1, 1, null, "Invalid column name 'nope'."));
        var session = ScriptSession("SELECT 1;", executor);
        await session.InitializeAsync();
        var frame = session.Frames[0];

        var result = await session.EvaluateReplAsync(frame, "SELECT nope;");

        Assert.Equal(Session.ReplOutcome.Rendered, result.Outcome);   // NOT an exception
        Assert.Contains("207", result.Rendered);
        Assert.Contains("Invalid column name", result.Rendered);
        await session.TeardownAsync();
    }

    [Fact]
    public async Task ExecutorLevelFailure_ReportedNotThrown()
    {
        var executor = Init(new FakeStatementExecutor())
            .Then(_ => throw new StatementExecutionException("Timeout expired.", 0, -2));
        var session = ScriptSession("SELECT 1;", executor);
        await session.InitializeAsync();
        var frame = session.Frames[0];

        var result = await session.EvaluateReplAsync(frame, "SELECT 1;");

        Assert.Equal(Session.ReplOutcome.Refused, result.Outcome);
        Assert.Contains("faulted", result.RefusalMessage);
        await session.TeardownAsync();
    }

    // Mirrors SessionC23PreflightTests.DebuggerInitiatedEval_NeverArmsThePreflight: a
    // REPL read of a doomed-and-destroyed #temp fails honestly via the executor-level
    // fault path and must NOT arm/consume the A14 pre-flight for the NEXT debuggee
    // step (REPL composes via ComposedBatchBuilder.BuildForRepl directly — it never
    // calls Session.ComposeDebuggeeBatch at all, so the capture gate can never arm).
    [Fact]
    public async Task DoomedTempRead_NeverArmsThePreflight()
    {
        const string script = """
            CREATE TABLE #t (a int);
            BEGIN TRY
                SELECT 1 / 0 AS a;
            END TRY
            BEGIN CATCH
                SELECT COUNT(*) AS c FROM #t;
            END CATCH
            """;
        var executor = Init(new FakeStatementExecutor())
            .Then(_ => Ok())          // CREATE TABLE #t
            .Then(_ => DoomFault());  // 1/0 dooms + routes
        var session = ScriptSession(script, executor);
        await session.InitializeAsync();
        await session.StepAsync();   // CREATE #t -> registry Add
        await session.StepAsync();   // fault -> doomed, cursor at CATCH
        Assert.True(session.IsDoomed);
        var frame = session.Frames[0];

        executor.Then(_ => throw new StatementExecutionException("Invalid object name '#t__f0'.", 16, 208));
        var replResult = await session.EvaluateReplAsync(frame, "SELECT COUNT(*) FROM #t;");
        Assert.Equal(Session.ReplOutcome.Refused, replResult.Outcome);   // eval fails, session stays healthy-ish (doomed, not broken)

        var batchesBefore = executor.ReceivedBatches.Count;
        await session.StepAsync();   // the debuggee SU: its OWN pre-flight fires fresh

        Assert.Equal(StepDisposition.DoomedTempPreflight, session.LastStep.Disposition);
        Assert.Equal(batchesBefore, executor.ReceivedBatches.Count);
        await session.TeardownAsync();
    }

    // ---- Frame-targeted evaluation (design note §5 item 7) ---------------------------

    [Fact]
    public async Task TempTableReference_ResolvesThroughTheSelectedFramesChain()
    {
        // A20: frame-0 session-created temps keep original names (resolved-to-itself
        // is unpatched), so the chain-resolution proof uses a hand-registered
        // COLLIDED-shape entry (physical ≠ original) — a callee's colliding create.
        var executor = Init(new FakeStatementExecutor())
            .Then(_ => UserSelect("c", 0)); // the REPL read
        var session = ScriptSession("SELECT 1;", executor);
        await session.InitializeAsync();
        var frame = session.Frames[0];
        frame.TempObjects.Add(new Core.Interpreter.TempObjectEntry
        {
            OriginalName = "#work",
            PhysicalName = "#work__f9",
            Kind = Core.Interpreter.TempObjectKind.TempTable,
            CreatedAtTrancount = 1,
        });

        var result = await session.EvaluateReplAsync(frame, "SELECT COUNT(*) AS c FROM #work;");

        Assert.Equal(Session.ReplOutcome.Rendered, result.Outcome);
        Assert.Contains("[#work__f9]", executor.ReceivedBatches[^1]);   // R2-resolved, not the literal name
        await session.TeardownAsync();
    }

    [Fact]
    public async Task ConsoleCreatedTempTable_NotR2Patched_KeepsLiteralName()
    {
        var executor = Init(new FakeStatementExecutor()).Then(_ => Empty());
        var session = ScriptSession("SELECT 1;", executor, allowConsoleWrites: true);
        await session.InitializeAsync();
        var frame = session.Frames[0];

        var result = await session.EvaluateReplAsync(frame, "CREATE TABLE #console_only (a int);");

        Assert.Equal(Session.ReplOutcome.Rendered, result.Outcome);
        Assert.Contains("CREATE TABLE #console_only", executor.ReceivedBatches[^1]);
        Assert.DoesNotContain("#console_only__f0", executor.ReceivedBatches[^1]);
        await session.TeardownAsync();
    }
}
