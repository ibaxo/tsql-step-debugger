using Microsoft.Data.SqlClient;
using Xunit;

namespace TsqlDbg.Integration;

// M6 boost design probes — facts 26–29 (docs/archive/reviews/m6-boost-design-notes-fable.md §7),
// probed live BEFORE the §14/A21 boost core is built, per the Fable lane order
// (note §10 item 1). Results recorded in docs/engine-facts.md. These are the engine
// halves the boost mechanism load-bears on:
//   26 — marker-statement intrinsic effects (clobbers @@ROWCOUNT/@@ERROR, neutral to
//        SCOPE_IDENTITY/@@FETCH_STATUS/@@TRANCOUNT/XACT_STATE; guarded form no-ops
//        while doomed) — why B3 refuses intrinsic references and B4 suppresses
//        trailing markers;
//   27 — post-subtree intrinsic shapes (fact-12 refinement): a capture placed after
//        the node reads native values regardless of interior marker clobbering —
//        B4's V-invariant, both halves;
//   28 — attention mid-multi-statement-batch rolls back ONLY the in-flight statement;
//        completed statements' effects and mid-transaction #temp writes persist and
//        are visible to the same session's next command (B7's recovery-read premise;
//        includes fact 26's attention-visibility half);
//   29 — ERROR_LINE() in a multi-statement, multi-line batch reports the faulting
//        STATEMENT's starting line, including inside IF/WHILE bodies and for
//        multi-line statements and predicates (B6's err_line → unique-SU premise).
//
// Unlike facts 11–25 these have no docs/engine-facts/factNN_*.sql companion scripts:
// facts 26c/28 need client-level attention (SqlCommand.Cancel) that sqlcmd cannot
// drive (the fact-24 group-A precedent), and keeping all four here pins them
// permanently in the live suite instead of in run-once scripts.
public sealed class Fact26To29BoostProbesLiveTests
{
    private const string ConnEnvVar = "TSQLDBG_TEST_CONN";

    private static async Task<SqlConnection> OpenAsync(bool fireInfoMessageOnUserErrors = false)
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnEnvVar);
        Skip.If(
            string.IsNullOrWhiteSpace(connectionString),
            $"{ConnEnvVar} is not set; skipping live probe (never fake a pass — CLAUDE.md).");
        var connection = new SqlConnection(connectionString);
        // Fact-24 group-A technique: sev ≤ 16 errors arrive as InfoMessage instead of
        // throwing, so unhandled-continuation shapes (fact 18) stream to completion.
        connection.FireInfoMessageEventOnUserErrors = fireInfoMessageOnUserErrors;
        await connection.OpenAsync();
        return connection;
    }

    private static async Task ExecAsync(SqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?[]> QuerySingleRowAsync(SqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();
        while (!await reader.ReadAsync())
        {
            Assert.True(await reader.NextResultAsync(), "probe batch produced no readable row");
        }
        var values = new object?[reader.FieldCount];
        reader.GetValues(values!);
        // Drain remaining result sets so trailing errors (if any) surface here, not on dispose.
        while (await reader.NextResultAsync()) { }
        return values;
    }

    // ---- Fact 26a (clobber half) — a marker UPDATE sets @@ROWCOUNT=1/@@ERROR=0
    // (the clobber B4's trailing suppression + B3's intrinsic refusals account for). ----
    [SkippableFact]
    public async Task Fact26a_MarkerUpdate_ClobbersRowcountAndError()
    {
        await using var connection = await OpenAsync();

        await ExecAsync(connection, """
            CREATE TABLE #b(seq int NOT NULL, pos int NOT NULL);
            INSERT #b VALUES (42, -1);
            CREATE TABLE #w(v int);
            """);

        var row = await QuerySingleRowAsync(connection, """
            INSERT #w VALUES (1),(2),(3);
            UPDATE #b SET pos = 7;
            SELECT @@ROWCOUNT AS rc, @@ERROR AS err;
            """);

        Assert.Equal(1, Convert.ToInt32(row[0]));   // rc: the marker's own 1, NOT the INSERT's 3 — markers clobber
        Assert.Equal(0, Convert.ToInt32(row[1]));   // err: 0 after a successful marker
    }

    // ---- Fact 26a (neutrality half) — a marker UPDATE leaves SCOPE_IDENTITY()/
    // @@FETCH_STATUS/@@TRANCOUNT/XACT_STATE() untouched. (No non-identity INSERT may
    // precede the capture: fact 26d shows any insert-family statement into a
    // non-identity table resets SCOPE_IDENTITY() to NULL natively.) ----
    [SkippableFact]
    public async Task Fact26a_MarkerUpdate_NeutralToScopeIdentityFetchStatusAndTranState()
    {
        await using var connection = await OpenAsync();

        await ExecAsync(connection, """
            CREATE TABLE #b(seq int NOT NULL, pos int NOT NULL);
            INSERT #b VALUES (42, -1);
            CREATE TABLE #i(id int IDENTITY(5,1), v int);
            """);

        var row = await QuerySingleRowAsync(connection, """
            DECLARE @v int;
            DECLARE c CURSOR LOCAL FAST_FORWARD FOR SELECT v FROM #i;
            INSERT #i(v) VALUES (10);
            OPEN c;
            FETCH NEXT FROM c INTO @v;
            BEGIN TRAN;
            UPDATE #b SET pos = 7;
            SELECT SCOPE_IDENTITY() AS si, @@FETCH_STATUS AS fs, @@TRANCOUNT AS tc, XACT_STATE() AS xs;
            ROLLBACK;
            """);

        Assert.Equal(5, Convert.ToInt32(row[0]));   // SCOPE_IDENTITY untouched by the marker
        Assert.Equal(0, Convert.ToInt32(row[1]));   // @@FETCH_STATUS untouched (p08's fetch loops depend on this)
        Assert.Equal(1, Convert.ToInt32(row[2]));   // @@TRANCOUNT untouched
        Assert.Equal(1, Convert.ToInt32(row[3]));   // XACT_STATE untouched
    }

    // ---- Fact 26d — SCOPE_IDENTITY() chain semantics (NEW, previously unrecorded;
    // the existing R6 live-capture model implicitly rides these): session-sticky
    // across plain ad-hoc batches; ANY insert-family statement into a table without
    // an identity column (even zero-row) resets it to NULL; CREATE TABLE, DROP
    // TABLE, and ROLLBACK are neutral (rollback does NOT revert it). This is what
    // makes B4's prologue seed INSERT non-neutral — the finding escalated to Ivan. ----
    [SkippableFact]
    public async Task Fact26d_ScopeIdentity_StickyAcrossBatches_ResetByNonIdentityInserts()
    {
        await using var connection = await OpenAsync();

        await ExecAsync(connection, "CREATE TABLE #i(id int IDENTITY(5,1), v int); CREATE TABLE #d(x int);");
        await ExecAsync(connection, "INSERT #i(v) VALUES (10);");

        var sticky = await QuerySingleRowAsync(connection, "SELECT SCOPE_IDENTITY() AS si;");
        Assert.Equal(5, Convert.ToInt32(sticky[0]));            // sticky across plain batch boundaries

        var afterDdl = await QuerySingleRowAsync(connection,
            "CREATE TABLE #d2(x int); SELECT SCOPE_IDENTITY() AS si;");
        Assert.Equal(5, Convert.ToInt32(afterDdl[0]));          // CREATE TABLE is neutral

        var afterZeroRow = await QuerySingleRowAsync(connection,
            "INSERT #d SELECT 1 WHERE 1 = 0; SELECT SCOPE_IDENTITY() AS si;");
        Assert.True(afterZeroRow[0] is DBNull);                 // even a ZERO-ROW non-identity insert resets to NULL

        await ExecAsync(connection, "INSERT #i(v) VALUES (20);");
        var reestablished = await QuerySingleRowAsync(connection, "SELECT SCOPE_IDENTITY() AS si;");
        Assert.Equal(6, Convert.ToInt32(reestablished[0]));     // an identity insert re-establishes the chain

        var afterRollback = await QuerySingleRowAsync(connection, """
            BEGIN TRAN;
            INSERT #i(v) VALUES (30);
            ROLLBACK;
            SELECT SCOPE_IDENTITY() AS si;
            """);
        Assert.Equal(7, Convert.ToInt32(afterRollback[0]));     // ROLLBACK does not revert it

        var afterDrop = await QuerySingleRowAsync(connection,
            "DROP TABLE #d2; SELECT SCOPE_IDENTITY() AS si;");
        Assert.Equal(7, Convert.ToInt32(afterDrop[0]));         // DROP TABLE is neutral
    }

    // ---- Fact 26e — sp_executesql children are TRUE separate scopes for
    // SCOPE_IDENTITY(): NULL at child entry (the outer sticky value does NOT flow
    // in), the child's own identity inserts do NOT leak out, and the outer value is
    // restored at child exit. Parameterized SqlClient commands ride sp_executesql,
    // so composed-batch transport decides which regime a batch runs under —
    // plain/parameter-free batches (the healthy per-SU pipeline) ride the sticky
    // chain of fact 26d instead. ----
    [SkippableFact]
    public async Task Fact26e_ScopeIdentity_SpExecutesqlChildIsASeparateScope()
    {
        await using var connection = await OpenAsync();

        await ExecAsync(connection, "CREATE TABLE #i(id int IDENTITY(5,1), v int);");
        await ExecAsync(connection, "INSERT #i(v) VALUES (10);");

        // A parameter forces sp_executesql transport.
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT SCOPE_IDENTITY() AS si, @p AS p;";
            command.Parameters.AddWithValue("@p", 1);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.True(reader.IsDBNull(0));                    // NULL at child-scope entry, not the sticky 5
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "INSERT #i(v) VALUES (20); SELECT SCOPE_IDENTITY() AS si, @p AS p;";
            command.Parameters.AddWithValue("@p", 1);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(6, Convert.ToInt32(reader.GetValue(0))); // the child sees its OWN insert
        }

        var outer = await QuerySingleRowAsync(connection, "SELECT SCOPE_IDENTITY() AS si;");
        Assert.Equal(5, Convert.ToInt32(outer[0]));             // outer sticky value restored — child insert did not leak
    }

    // ---- Fact 26a (error half) — a marker after an unhandled sev-16 error clobbers
    // the 50000 back to 0 (the control run without a marker reads 50000). ----
    [SkippableFact]
    public async Task Fact26a_MarkerUpdate_ClobbersNonzeroError()
    {
        await using var connection = await OpenAsync(fireInfoMessageOnUserErrors: true);

        await ExecAsync(connection, """
            CREATE TABLE #b(seq int NOT NULL, pos int NOT NULL);
            INSERT #b VALUES (1, -1);
            """);

        // Control: unhandled continuation (fact 18) leaves @@ERROR = 50000.
        var control = await QuerySingleRowAsync(connection, """
            RAISERROR('fact26-control', 16, 1);
            SELECT @@ERROR AS err;
            """);
        Assert.Equal(50000, Convert.ToInt32(control[0]));

        // With the marker in between, the capture reads the marker's 0.
        var probed = await QuerySingleRowAsync(connection, """
            RAISERROR('fact26-probe', 16, 1);
            UPDATE #b SET pos = 8;
            SELECT @@ERROR AS err;
            """);
        Assert.Equal(0, Convert.ToInt32(probed[0]));
    }

    // ---- Fact 26b — the guarded marker/state-write shapes no-op cleanly inside a
    // doomed transaction (no 3930, position unchanged, reads still work). ----
    [SkippableFact]
    public async Task Fact26b_GuardedMarker_NoOpsCleanly_WhileDoomed()
    {
        await using var connection = await OpenAsync();

        await ExecAsync(connection, """
            CREATE TABLE #b(seq int NOT NULL, pos int NOT NULL);
            INSERT #b VALUES (1, -1);
            """);

        // fact-5/fact-22 doom shape: XACT_ABORT ON + conversion fault inside TRY.
        var row = await QuerySingleRowAsync(connection, """
            SET XACT_ABORT ON;
            BEGIN TRAN;
            BEGIN TRY
                DECLARE @d int = CONVERT(int, 'fact26b-doom');
            END TRY
            BEGIN CATCH
                DECLARE @xs int = XACT_STATE();
                IF XACT_STATE() <> -1 UPDATE #b SET pos = 9;
                IF XACT_STATE() <> -1 AND OBJECT_ID('tempdb..#b') IS NOT NULL UPDATE #b SET seq = 99;
                SELECT @xs AS xs, (SELECT pos FROM #b) AS pos, (SELECT seq FROM #b) AS seq;
                ROLLBACK;
            END CATCH
            SET XACT_ABORT OFF;
            """);

        Assert.Equal(-1, Convert.ToInt32(row[0]));  // genuinely doomed when the guards ran
        Assert.Equal(-1, Convert.ToInt32(row[1]));  // guarded marker did not fire
        Assert.Equal(1, Convert.ToInt32(row[2]));   // guarded state-write shape did not fire

        var after = await QuerySingleRowAsync(connection,
            "SELECT @@TRANCOUNT AS tc, (SELECT pos FROM #b) AS pos;");
        Assert.Equal(0, Convert.ToInt32(after[0])); // in-batch ROLLBACK exited doom cleanly (fact 22)
        Assert.Equal(-1, Convert.ToInt32(after[1])); // #b created pre-tran survives the rollback
    }

    // ---- Fact 28 (+ fact 26c) — attention mid-multi-statement-batch: completed
    // statements persist (incl. mid-transaction #temp marker writes, visible to the
    // session's next command — B7's recovery read), the in-flight statement rolls
    // back statement-level, later statements never run, the open transaction and the
    // connection survive. ----
    [SkippableFact]
    public async Task Fact28_AttentionMidBatch_RollsBackOnlyInFlightStatement()
    {
        await using var connection = await OpenAsync();

        await ExecAsync(connection, """
            CREATE TABLE #b(seq int NOT NULL, pos int NOT NULL);
            INSERT #b VALUES (7, -1);
            CREATE TABLE #w(v int);
            """);

        await using (var command = connection.CreateCommand())
        {
            command.CommandTimeout = 120;
            // Statement 3 is the in-flight victim: a multi-second INSERT the cancel
            // interrupts mid-write (TOP 20M off a triple cross join — minutes of work,
            // cancelled after ~1.5s; if it ever completed, the v=2 count below fails).
            command.CommandText = """
                BEGIN TRAN;
                INSERT #w VALUES (1);
                UPDATE #b SET pos = 1;
                INSERT #w SELECT TOP (20000000) 2
                FROM sys.all_objects a CROSS JOIN sys.all_objects b CROSS JOIN sys.all_objects c;
                UPDATE #b SET pos = 2;
                INSERT #w VALUES (3);
                """;

            var execution = command.ExecuteNonQueryAsync();
            await Task.Delay(1500);
            command.Cancel();

            var exception = await Assert.ThrowsAsync<SqlException>(() => execution);
            Assert.Contains(exception.Errors.Cast<SqlError>(), e => e.Number == 0); // "Operation cancelled by user"
        }

        // Same connection, next command — the B7 recovery read's exact shape.
        var row = await QuerySingleRowAsync(connection, """
            SELECT @@TRANCOUNT AS tc,
                   (SELECT pos FROM #b) AS pos,
                   (SELECT COUNT(*) FROM #w WHERE v = 1) AS completed,
                   (SELECT COUNT(*) FROM #w WHERE v = 2) AS inflight,
                   (SELECT COUNT(*) FROM #w WHERE v = 3) AS neverRan;
            """);

        Assert.Equal(1, Convert.ToInt32(row[0]));   // open transaction survives attention (XACT_ABORT OFF)
        Assert.Equal(1, Convert.ToInt32(row[1]));   // fact 26c: mid-tran #temp marker write visible after attention
        Assert.Equal(1, Convert.ToInt32(row[2]));   // completed statement's effect persists
        Assert.Equal(0, Convert.ToInt32(row[3]));   // in-flight statement rolled back statement-level
        Assert.Equal(0, Convert.ToInt32(row[4]));   // statements after the attention point never ran

        await ExecAsync(connection, "ROLLBACK;");
        var healthy = await QuerySingleRowAsync(connection, "SELECT 1;");
        Assert.Equal(1, Convert.ToInt32(healthy[0])); // connection healthy for the next command
    }

    // ---- Fact 27 — post-subtree intrinsic shapes (V-invariant, both halves):
    // a capture after the node reads native values regardless of interior
    // marker-style clobbering; loop exits reset via the final false predicate,
    // taken-branch tails flow through untouched. ----
    [SkippableFact]
    public async Task Fact27_PostSubtreeCapture_ReadsNativeValues_DespiteInteriorMarkers()
    {
        await using var connection = await OpenAsync(fireInfoMessageOnUserErrors: true);

        await ExecAsync(connection, """
            CREATE TABLE #t(v int);
            CREATE TABLE #b(pos int);
            INSERT #b VALUES (-1);
            """);

        // (a) WHILE exit with a marker-style UPDATE as the LAST body action (loop-body
        // markers are NOT suppressed — B4): the final false predicate is still the
        // last resetter, so the post-loop capture reads 0/0 (fact 12's reset).
        var afterWhile = await QuerySingleRowAsync(connection, """
            DECLARE @i int = 0;
            WHILE @i < 3
            BEGIN
                INSERT #t VALUES (1),(2);
                SET @i = @i + 1;
                UPDATE #b SET pos = @i;
            END
            SELECT @@ROWCOUNT AS rc, @@ERROR AS err;
            """);
        Assert.Equal(0, Convert.ToInt32(afterWhile[0]));
        Assert.Equal(0, Convert.ToInt32(afterWhile[1]));

        // (a2) same shape with a nonzero @@ERROR live in the body (unhandled sev-16,
        // fact 18 continuation): the final predicate evaluation still resets it.
        var afterWhileErr = await QuerySingleRowAsync(connection, """
            DECLARE @j int = 0;
            WHILE @j < 2
            BEGIN
                RAISERROR('fact27-body', 16, 1);
                SET @j = @j + 1;
            END
            SELECT @@ERROR AS err;
            """);
        Assert.Equal(0, Convert.ToInt32(afterWhileErr[0]));

        // (b) skipped IF: the false predicate's reset governs the post-node read
        // (0), not the pre-IF statement's rowcount (3).
        var afterSkippedIf = await QuerySingleRowAsync(connection, """
            INSERT #t VALUES (1),(2),(3);
            IF 1 = 0
                UPDATE #b SET pos = 99;
            SELECT @@ROWCOUNT AS rc;
            """);
        Assert.Equal(0, Convert.ToInt32(afterSkippedIf[0]));

        // (c) taken IF with an interior (mid-branch) marker: nothing between the
        // branch's last user statement and the post-node capture — the capture reads
        // that statement's rowcount (2). This is the engine half of B4's
        // trailing-marker suppression: suppress the tail insertion and the native
        // value flows out of the subtree untouched.
        var afterTakenIf = await QuerySingleRowAsync(connection, """
            IF 1 = 1
            BEGIN
                UPDATE #b SET pos = 5;
                INSERT #t VALUES (1),(2);
            END
            SELECT @@ROWCOUNT AS rc;
            """);
        Assert.Equal(2, Convert.ToInt32(afterTakenIf[0]));
    }

    // ---- Fact 29 — ERROR_LINE() reports the faulting STATEMENT's starting line in
    // multi-statement, multi-line batches (B6's mapping premise). ----
    [SkippableFact]
    public async Task Fact29_ErrorLine_ReportsFaultingStatementStartLine()
    {
        await using var connection = await OpenAsync();

        // (A) Nth simple statement of a multi-statement batch.
        var a = await QuerySingleRowAsync(connection,
            "BEGIN TRY\n" +               // line 1
            "    DECLARE @x int;\n" +     // line 2
            "    SET @x = 1;\n" +         // line 3
            "    SET @x = 1/0;\n" +       // line 4  <- fault
            "    SET @x = 2;\n" +         // line 5
            "END TRY\n" +
            "BEGIN CATCH\n" +
            "    SELECT ERROR_LINE() AS el;\n" +
            "END CATCH\n");
        Assert.Equal(4, Convert.ToInt32(a[0]));

        // (B) multi-line statement: the STARTING line, not the faulting token's line.
        var b = await QuerySingleRowAsync(connection,
            "BEGIN TRY\n" +               // line 1
            "    DECLARE @x int;\n" +     // line 2
            "    SET @x =\n" +            // line 3  <- statement starts here
            "        1/0;\n" +            // line 4  (faulting expression)
            "END TRY\n" +
            "BEGIN CATCH\n" +
            "    SELECT ERROR_LINE() AS el;\n" +
            "END CATCH\n");
        Assert.Equal(3, Convert.ToInt32(b[0]));

        // (C) inside an IF body.
        var c = await QuerySingleRowAsync(connection,
            "BEGIN TRY\n" +               // line 1
            "    DECLARE @x int = 1;\n" + // line 2
            "    IF @x = 1\n" +           // line 3
            "    BEGIN\n" +               // line 4
            "        SET @x = 1/0;\n" +   // line 5  <- fault
            "    END\n" +                 // line 6
            "END TRY\n" +
            "BEGIN CATCH\n" +
            "    SELECT ERROR_LINE() AS el;\n" +
            "END CATCH\n");
        Assert.Equal(5, Convert.ToInt32(c[0]));

        // (D) a faulted WHILE predicate reports the WHILE's line.
        var d = await QuerySingleRowAsync(connection,
            "BEGIN TRY\n" +               // line 1
            "    DECLARE @x int = 0;\n" + // line 2
            "    WHILE (1/0) = 1\n" +     // line 3  <- fault at the predicate
            "        SET @x = 1;\n" +     // line 4
            "END TRY\n" +
            "BEGIN CATCH\n" +
            "    SELECT ERROR_LINE() AS el;\n" +
            "END CATCH\n");
        Assert.Equal(3, Convert.ToInt32(d[0]));

        // (E) a later-iteration fault inside a WHILE body still maps to the
        // statement's lexical line (faults on iteration 2, when @i = 2).
        var e = await QuerySingleRowAsync(connection,
            "BEGIN TRY\n" +                       // line 1
            "    DECLARE @i int = 0, @x int;\n" + // line 2
            "    WHILE @i < 5\n" +                // line 3
            "    BEGIN\n" +                       // line 4
            "        SET @i = @i + 1;\n" +        // line 5
            "        SET @x = 1 / (2 - @i);\n" +  // line 6  <- faults when @i = 2
            "    END\n" +                         // line 7
            "END TRY\n" +
            "BEGIN CATCH\n" +
            "    SELECT ERROR_LINE() AS el;\n" +
            "END CATCH\n");
        Assert.Equal(6, Convert.ToInt32(e[0]));
    }
}
