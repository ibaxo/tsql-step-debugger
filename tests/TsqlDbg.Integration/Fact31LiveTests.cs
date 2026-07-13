using Microsoft.Data.SqlClient;
using Xunit;

namespace TsqlDbg.Integration;

// M7 hardening probes — facts 31a–31d (docs/archive/reviews/m7-hardening-design-notes-fable.md §9),
// probed live BEFORE the D1 chain-sync fix / D2 error-procedure synthesis, per the Fable
// lane order (note §11 item 0). Results recorded in docs/engine-facts.md; the seam
// evaluation and the 31c→A27 STOP are in docs/archive/reviews/m7-hardening-core-opus.md.
//
//   31a — @@IDENTITY reset rule: does an insert into a NON-identity table perturb the
//         session-global @@IDENTITY (mirror of 26d for SCOPE_IDENTITY)? DECIDES caveat
//         C26 and p15-ext cell b's @@IDENTITY assertion.
//   31b — insert-family chain membership: which statement classes move SCOPE_IDENTITY?
//         DECIDES D1's classifier table; a runtime-conditional shape would trip the
//         §1.2.5 contingency.
//   31c — native ERROR_PROCEDURE()/ERROR_LINE() shapes: name-only vs schema-qualified
//         inside a proc's own CATCH, and both values in a caller's CATCH for a callee
//         uncatchable 208. UNDERPINS already-ratified A27 (§10.2 synthesis).
//   31d — 26d stickiness restated across autocommit (no explicit transaction) batches —
//         the detached regime D1 relies on while detached.
//
// Like facts 26c/28/30, 31a/31c need client-level transport (parameterized sp_executesql,
// deployed modules) that a run-once sqlcmd script is a poor home for; keeping them here
// pins the discovered engine truth permanently in the live suite (the fact-24/26 precedent).
public sealed class Fact31LiveTests
{
    private const string ConnEnvVar = "TSQLDBG_TEST_CONN";

    private static async Task<SqlConnection> OpenAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnEnvVar);
        Skip.If(
            string.IsNullOrWhiteSpace(connectionString),
            $"{ConnEnvVar} is not set; skipping live probe (never fake a pass — CLAUDE.md).");
        var connection = new SqlConnection(connectionString);
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
        while (await reader.NextResultAsync()) { }
        return values;
    }

    private static bool IsNull(object? v) => v is null or DBNull;

    // ---- Fact 31a — @@IDENTITY is PERTURBED (reset to NULL) by any insert into a
    // non-identity table, exactly like SCOPE_IDENTITY (fact 26d), AND the parameterized
    // (sp_executesql child) variant perturbs the session-global value too. This is the
    // perturbation case: the debugger's own bookkeeping INSERTs (frame-push state seeds,
    // plain or doomed-mode parameterized) reset @@IDENTITY where native execution of the
    // stepped-over/into module would not — the F3-3 finding. → C26 (contingent register
    // row, orchestrator applies). ----
    [SkippableFact]
    public async Task Fact31a_AtIdentity_PerturbedByNonIdentityInserts_PlainAndParameterizedChild()
    {
        await using var connection = await OpenAsync();

        await ExecAsync(connection, """
            IF OBJECT_ID('tempdb..#i31a') IS NOT NULL DROP TABLE #i31a;
            IF OBJECT_ID('tempdb..#d31a') IS NOT NULL DROP TABLE #d31a;
            CREATE TABLE #i31a(id int IDENTITY(5,1), v int);
            CREATE TABLE #d31a(x int);
            """);

        await ExecAsync(connection, "INSERT #i31a(v) VALUES (10);");                 // identity insert
        var afterIdentity = await QuerySingleRowAsync(connection, "SELECT @@IDENTITY AS ii, SCOPE_IDENTITY() AS si;");
        Assert.Equal(5, Convert.ToInt32(afterIdentity[0]));                          // @@IDENTITY = 5
        Assert.Equal(5, Convert.ToInt32(afterIdentity[1]));                          // SCOPE_IDENTITY = 5

        var afterPlain = await QuerySingleRowAsync(connection,
            "INSERT #d31a VALUES (1); SELECT @@IDENTITY AS ii, SCOPE_IDENTITY() AS si;");
        Assert.True(IsNull(afterPlain[0]));                                          // @@IDENTITY RESET to NULL
        Assert.True(IsNull(afterPlain[1]));                                          // SCOPE_IDENTITY RESET to NULL (26d)

        await ExecAsync(connection, "INSERT #i31a(v) VALUES (11);");                 // re-establish
        var afterZeroRow = await QuerySingleRowAsync(connection,
            "INSERT #d31a SELECT 1 WHERE 1 = 0; SELECT @@IDENTITY AS ii, SCOPE_IDENTITY() AS si;");
        Assert.True(IsNull(afterZeroRow[0]));                                        // even a ZERO-ROW non-identity insert perturbs @@IDENTITY
        Assert.True(IsNull(afterZeroRow[1]));

        await ExecAsync(connection, "INSERT #i31a(v) VALUES (12);");                 // @@IDENTITY = SCOPE_IDENTITY = 7
        // Parameterized transport → sp_executesql child scope (fact 26e). SCOPE_IDENTITY
        // is scope-local (outer value survives) but @@IDENTITY is session-global — the
        // child's non-identity insert resets the session @@IDENTITY.
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "INSERT #d31a VALUES (@n);";
            command.Parameters.AddWithValue("@n", 2);
            await command.ExecuteNonQueryAsync();
        }
        var afterChild = await QuerySingleRowAsync(connection, "SELECT @@IDENTITY AS ii, SCOPE_IDENTITY() AS si;");
        Assert.True(IsNull(afterChild[0]));                                          // @@IDENTITY reset by the parameterized child's non-identity insert
        Assert.Equal(7, Convert.ToInt32(afterChild[1]));                            // SCOPE_IDENTITY untouched (26e: child is a separate scope)
    }

    // ---- Fact 31b — insert-family SCOPE_IDENTITY membership is STATEMENT-CLASS-BASED
    // (not runtime-conditional). Chain-movers: INSERT (incl. zero-row / INSERT…EXEC),
    // SELECT…INTO, and MERGE WITH AN INSERT ACTION CLAUSE (even when it inserts ZERO
    // rows). NEUTRAL: UPDATE/DELETE (incl. OUTPUT and OUTPUT INTO — the internal output
    // insert does NOT move the caller's chain), and MERGE with only UPDATE/DELETE
    // actions. This REFINES the design note's proposed classifier by excluding
    // UPDATE/DELETE … OUTPUT INTO — recording it so D1's classifier matches the engine. ----
    [SkippableFact]
    public async Task Fact31b_InsertFamilyChainMembership_IsStatementClassBased()
    {
        await using var connection = await OpenAsync();

        await ExecAsync(connection, """
            IF OBJECT_ID('tempdb..#si31b') IS NOT NULL DROP TABLE #si31b;
            IF OBJECT_ID('tempdb..#tgt31b') IS NOT NULL DROP TABLE #tgt31b;
            IF OBJECT_ID('tempdb..#out31b') IS NOT NULL DROP TABLE #out31b;
            IF OBJECT_ID('tempdb..#src31b') IS NOT NULL DROP TABLE #src31b;
            CREATE TABLE #si31b(id int IDENTITY(5,1), v int);
            CREATE TABLE #tgt31b(k int, v int);
            CREATE TABLE #out31b(v int);
            CREATE TABLE #src31b(v int);
            INSERT #tgt31b VALUES (1, 100);
            INSERT #src31b VALUES (1),(2);
            """);

        // Helper: reset the chain to a fresh identity value V, run `probe`, read SCOPE_IDENTITY().
        async Task<object?> ChainAfterAsync(string probe)
        {
            await ExecAsync(connection, "INSERT #si31b(v) VALUES (0);");
            var before = await QuerySingleRowAsync(connection, "SELECT SCOPE_IDENTITY() AS si;");
            Assert.False(IsNull(before[0]), "identity insert should establish the chain");
            var row = await QuerySingleRowAsync(connection, probe + " SELECT SCOPE_IDENTITY() AS si;");
            return row[0];
        }

        // MERGE with only an UPDATE action → NEUTRAL (chain survives).
        var mergeUpdateOnly = await ChainAfterAsync(
            "MERGE #tgt31b AS t USING (SELECT 1 AS k, 999 AS v) AS s ON t.k = s.k WHEN MATCHED THEN UPDATE SET v = s.v;");
        Assert.False(IsNull(mergeUpdateOnly));                                       // NEUTRAL

        // MERGE with an INSERT action that inserts 1 row → RESET.
        var mergeInsert1 = await ChainAfterAsync(
            "MERGE #tgt31b AS t USING (SELECT 900 AS k, 1 AS v) AS s ON t.k = s.k WHEN NOT MATCHED THEN INSERT (k, v) VALUES (s.k, s.v);");
        Assert.True(IsNull(mergeInsert1));                                           // RESET (insert-family)

        // MERGE with an INSERT action that inserts ZERO rows (target already matched)
        // → RESET anyway. THIS is the statement-class-based proof (not runtime-conditional).
        var mergeInsertZero = await ChainAfterAsync(
            "MERGE #tgt31b AS t USING (SELECT 900 AS k, 2 AS v) AS s ON t.k = s.k WHEN NOT MATCHED THEN INSERT (k, v) VALUES (s.k, s.v);");
        Assert.True(IsNull(mergeInsertZero));                                        // RESET even at 0 rows inserted

        // UPDATE … OUTPUT INTO → NEUTRAL (the design note listed this as insert-family;
        // the engine says otherwise — the classifier must EXCLUDE it).
        var updateOutputInto = await ChainAfterAsync(
            "UPDATE #tgt31b SET v = v + 1 OUTPUT inserted.v INTO #out31b;");
        Assert.False(IsNull(updateOutputInto));                                      // NEUTRAL — NOT insert-family

        // DELETE … OUTPUT INTO → NEUTRAL.
        var deleteOutputInto = await ChainAfterAsync(
            "DELETE #tgt31b OUTPUT deleted.v INTO #out31b WHERE k = 900;");
        Assert.False(IsNull(deleteOutputInto));                                      // NEUTRAL — NOT insert-family

        // SELECT … INTO → RESET (creates + inserts a non-identity target).
        await ExecAsync(connection, "IF OBJECT_ID('tempdb..#into31b') IS NOT NULL DROP TABLE #into31b;");
        var selectInto = await ChainAfterAsync("SELECT v INTO #into31b FROM #src31b;");
        Assert.True(IsNull(selectInto));                                             // RESET (insert-family)

        // INSERT … EXEC → RESET (an InsertStatement).
        var insertExec = await ChainAfterAsync("INSERT #out31b EXEC('SELECT 99');");
        Assert.True(IsNull(insertExec));                                             // RESET (insert-family)
    }

    // ---- Fact 31c — native ERROR_PROCEDURE() is SCHEMA-QUALIFIED (schema.name,
    // unbracketed) for stored procedures — CONTRADICTING A27's "name-only" premise.
    // Confirmed inside a proc's OWN catch and in a CALLER's catch for a callee's
    // uncatchable 208 (both compile-class #temp and deferred-resolution permanent-table
    // shapes — the p23 cell). ERROR_LINE() = the origin statement's line within the
    // module (fault on module line 4 here). Triggers, by contrast, are NAME-ONLY — a
    // format inconsistency recorded for completeness (the debugger does not step into
    // triggers). This is the 31c→A27 STOP: see docs/archive/reviews/m7-hardening-core-opus.md. ----
    [SkippableFact]
    public async Task Fact31c_ErrorProcedure_IsSchemaQualifiedForProcedures_NameOnlyForTriggers()
    {
        await using var connection = await OpenAsync();

        // CREATE PROCEDURE must be the only statement in its batch — one ExecAsync each.
        await ExecAsync(connection, """
            CREATE OR ALTER PROCEDURE dbo.p31_self AS
            BEGIN
                BEGIN TRY
                    SELECT 1 / 0;
                END TRY
                BEGIN CATCH
                    SELECT ERROR_PROCEDURE() AS ep, ERROR_LINE() AS el, ERROR_NUMBER() AS en;
                END CATCH
            END;
            """);
        await ExecAsync(connection, """
            CREATE OR ALTER PROCEDURE dbo.p31_inner_temp AS
            BEGIN
                BEGIN TRY
                    INSERT INTO #p31_missing_temp VALUES (1);
                END TRY
                BEGIN CATCH
                    SELECT 0 AS should_not_run;
                END CATCH
            END;
            """);
        await ExecAsync(connection, """
            CREATE OR ALTER PROCEDURE dbo.p31_inner_perm AS
            BEGIN
                BEGIN TRY
                    SELECT * FROM dbo.p31_nonexistent_perm;
                END TRY
                BEGIN CATCH
                    SELECT 0 AS should_not_run;
                END CATCH
            END;
            """);
        await ExecAsync(connection, """
            CREATE OR ALTER PROCEDURE dbo.p31_caller_temp AS
            BEGIN
                BEGIN TRY
                    EXEC dbo.p31_inner_temp;
                END TRY
                BEGIN CATCH
                    SELECT ERROR_PROCEDURE() AS ep, ERROR_LINE() AS el, ERROR_NUMBER() AS en;
                END CATCH
            END;
            """);
        await ExecAsync(connection, """
            CREATE OR ALTER PROCEDURE dbo.p31_caller_perm AS
            BEGIN
                BEGIN TRY
                    EXEC dbo.p31_inner_perm;
                END TRY
                BEGIN CATCH
                    SELECT ERROR_PROCEDURE() AS ep, ERROR_LINE() AS el, ERROR_NUMBER() AS en;
                END CATCH
            END;
            """);

        // (A) A proc's OWN catch — schema-qualified, NOT name-only.
        var self = await QuerySingleRowAsync(connection, "EXEC dbo.p31_self;");
        Assert.Equal("dbo.p31_self", (string)self[0]!);                             // QUALIFIED (contradicts A27 name-only)
        Assert.Equal(4, Convert.ToInt32(self[1]));                                  // fault on module line 4
        Assert.Equal(8134, Convert.ToInt32(self[2]));

        // (B) Caller's catch for a callee's compile-class 208 (missing #temp — p23 shape):
        // names the INNER proc, schema-qualified, at the inner's origin line.
        var callerTemp = await QuerySingleRowAsync(connection, "EXEC dbo.p31_caller_temp;");
        Assert.Equal("dbo.p31_inner_temp", (string)callerTemp[0]!);                 // names the callee, QUALIFIED
        Assert.Equal(4, Convert.ToInt32(callerTemp[1]));                            // the inner's INSERT line
        Assert.Equal(208, Convert.ToInt32(callerTemp[2]));

        // (C) Caller's catch for a callee's deferred-resolution 208 (missing permanent
        // table — fact 23-F): same shape, schema-qualified inner name.
        var callerPerm = await QuerySingleRowAsync(connection, "EXEC dbo.p31_caller_perm;");
        Assert.Equal("dbo.p31_inner_perm", (string)callerPerm[0]!);                 // QUALIFIED
        Assert.Equal(208, Convert.ToInt32(callerPerm[2]));

        // (D) Trigger — NAME-ONLY (no schema): the format inconsistency vs procedures.
        await ExecAsync(connection, "IF OBJECT_ID('dbo.p31_trg_tbl') IS NOT NULL DROP TABLE dbo.p31_trg_tbl;");
        await ExecAsync(connection, "CREATE TABLE dbo.p31_trg_tbl(id int);");
        await ExecAsync(connection, """
            CREATE OR ALTER TRIGGER dbo.p31_trg ON dbo.p31_trg_tbl AFTER INSERT AS
            BEGIN
                SELECT 1 / 0;
            END;
            """);
        var trg = await QuerySingleRowAsync(connection, """
            BEGIN TRY
                INSERT dbo.p31_trg_tbl VALUES (1);
            END TRY
            BEGIN CATCH
                SELECT ERROR_PROCEDURE() AS ep, ERROR_NUMBER() AS en;
            END CATCH
            """);
        Assert.Equal("p31_trg", (string)trg[0]!);                                   // NAME-ONLY for triggers (no "dbo.")
        Assert.Equal(8134, Convert.ToInt32(trg[1]));
    }

    // ---- Fact 31d — SCOPE_IDENTITY() (and @@IDENTITY) are sticky across AUTOCOMMIT
    // (no explicit transaction) batch boundaries, at trancount 0 — the detached-mode
    // regime restated (fact 26d probed the session's normal shapes; D1 relies on this
    // stickiness while detached). ----
    [SkippableFact]
    public async Task Fact31d_ScopeIdentity_StickyAcrossAutocommitBatches()
    {
        await using var connection = await OpenAsync();

        await ExecAsync(connection, """
            IF OBJECT_ID('tempdb..#i31d') IS NOT NULL DROP TABLE #i31d;
            CREATE TABLE #i31d(id int IDENTITY(5,1), v int);
            """);

        // Autocommit identity insert (no BEGIN TRAN).
        var batch1 = await QuerySingleRowAsync(connection,
            "INSERT #i31d(v) VALUES (10); SELECT SCOPE_IDENTITY() AS si, @@IDENTITY AS ii, @@TRANCOUNT AS tc;");
        Assert.Equal(5, Convert.ToInt32(batch1[0]));
        Assert.Equal(5, Convert.ToInt32(batch1[1]));
        Assert.Equal(0, Convert.ToInt32(batch1[2]));                                // autocommit — trancount 0

        // A separate batch on the same connection still reads the sticky value.
        var batch2 = await QuerySingleRowAsync(connection,
            "SELECT SCOPE_IDENTITY() AS si, @@IDENTITY AS ii, @@TRANCOUNT AS tc;");
        Assert.Equal(5, Convert.ToInt32(batch2[0]));                                // sticky across the autocommit boundary
        Assert.Equal(5, Convert.ToInt32(batch2[1]));
        Assert.Equal(0, Convert.ToInt32(batch2[2]));
    }
}
