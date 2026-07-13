-- DESIGN.md §20.4 corpus fixture: p15_scope_identity_rowcount_chains
-- M1 scope: strictly linear (DECLARE/SET/DML/SELECT only -- no RETURN/IF/WHILE/TRY/
-- transactions; decision on record, docs/archive/phase0-integration-log.md /
-- phase0-integration-notes.md's classifier gates). Exercises R4 (@@ROWCOUNT) and R6
-- (SCOPE_IDENTITY()) as a *chain*: each DECLARE ... = SCOPE_IDENTITY()/@@ROWCOUNT
-- becomes its own composed batch (§7.2/§8.2 synthetic assignment), so the shadow
-- substitute must correctly read back the PREVIOUS statement's captured control-row
-- value across that batch boundary -- the whole point of R4/R6 existing.
CREATE OR ALTER PROCEDURE dbo.p15_scope_identity_rowcount_chains
    @Seed int
AS
BEGIN
    SET NOCOUNT ON;

    CREATE TABLE #p15 (id int IDENTITY(1,1) PRIMARY KEY, val int);

    INSERT INTO #p15 (val) VALUES (@Seed);
    DECLARE @FirstId int = SCOPE_IDENTITY();
    DECLARE @FirstRowcount int = @@ROWCOUNT;

    INSERT INTO #p15 (val) VALUES (@Seed + 1);
    DECLARE @SecondId int = SCOPE_IDENTITY();
    DECLARE @SecondRowcount int = @@ROWCOUNT;

    UPDATE #p15 SET val = val * 10;
    DECLARE @UpdateRowcount int = @@ROWCOUNT;

    SELECT
        @FirstId AS FirstId, @FirstRowcount AS FirstRowcount,
        @SecondId AS SecondId, @SecondRowcount AS SecondRowcount,
        @UpdateRowcount AS UpdateRowcount;
END
GO

-- ---------------------------------------------------------------------------------
-- M7 D1 extension (docs/archive/reviews/m7-hardening-design-notes-fable.md §1.4): the
-- SCOPE_IDENTITY() chain-sync fix (A26 §7.4) across FRAMES and DOOMED mode — shapes
-- the strictly-linear p15 proc above cannot reach. Deployed by the same GO-split loader
-- (P15Ext / P15 tests). Engine truths: facts 26d/26e/31a/31b.
-- ---------------------------------------------------------------------------------

-- Reads SCOPE_IDENTITY() into an OUTPUT param as its FIRST statement (F3-1b: native
-- callee entry is NULL — a new scope), then optionally performs one identity insert
-- into its own #temp identity table.
CREATE OR ALTER PROCEDURE dbo.p15_ext_callee
    @DoInsert bit,
    @SiAtEntry int OUTPUT
AS
BEGIN
    -- SCOPE_IDENTITY() is the FIRST statement (no SET NOCOUNT before it): natively NULL
    -- at callee entry. Pre-D1 the debugger leaks the caller's chain here (F3-1b); D1
    -- nulls the shadow at push. (The debugger's own composed batches carry NOCOUNT in
    -- their preamble, so the fixture does not need SET NOCOUNT ON of its own.)
    SET @SiAtEntry = SCOPE_IDENTITY();
    IF @DoInsert = 1
    BEGIN
        CREATE TABLE #p15_ext_callee_id (id int IDENTITY(500, 1), v int);
        INSERT INTO #p15_ext_callee_id (v) VALUES (1);
    END
END
GO

-- Frame push/pop chain-sync. Into pass (biting): pre-D1 the callee-entry read leaks the
-- caller's chain (F3-1b) and the caller's post-pop read is poisoned (F3-1); D1 nulls the
-- shadow at push and gates the capture while poisoned. Over/boost: the stepped-over EXEC
-- is a true module scope — exact even pre-D1. @@IDENTITY (at_identity_0) is C26-exempt on
-- the Into pass: the debugger's push-seed INSERT perturbs the session-global @@IDENTITY
-- (fact 31a), which SCOPE_IDENTITY()'s chain-sync flag deliberately does not shadow.
CREATE OR ALTER PROCEDURE dbo.p15_ext_frames
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @e0 int, @e1 int, @a int, @b int, @ii0 int;

    CREATE TABLE #p15_ext_frames_id (id int IDENTITY(100, 1), v int);
    INSERT INTO #p15_ext_frames_id (v) VALUES (1);         -- caller chain = 100

    EXEC dbo.p15_ext_callee @DoInsert = 0, @SiAtEntry = @e0 OUTPUT;
    SET @a = SCOPE_IDENTITY();                             -- cell a: caller si after a no-insert callee
    SET @ii0 = @@IDENTITY;                                 -- C26 witness (Into: perturbed to NULL by the push seed)

    EXEC dbo.p15_ext_callee @DoInsert = 1, @SiAtEntry = @e1 OUTPUT;
    SET @b = SCOPE_IDENTITY();                             -- cell b: caller si after an inserting callee

    SELECT @e0 AS si_entry_0, @e1 AS si_entry_1, @a AS si_a, @b AS si_b, @ii0 AS at_identity_0;
END
GO

-- Doomed-mode chain-sync (F3-2). All passes: pre-D1 the doomed parameterized ROLLBACK
-- batch reads an sp_executesql child scope (fact 26e), poisoning the shadow to NULL; D1
-- poisons the flag at doom entry and skips the capture, so the post-resurrection read
-- serves the pre-doom value (native SCOPE_IDENTITY() survives doom + rollback — fact 26d).
CREATE OR ALTER PROCEDURE dbo.p15_ext_doom
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    DECLARE @si_after int;

    CREATE TABLE #p15_ext_doom_id (id int IDENTITY(100, 1), v int);
    INSERT INTO #p15_ext_doom_id (v) VALUES (1);           -- chain = 100

    BEGIN TRY
        DECLARE @Dummy int = 1 / 0;                        -- 8134, dooms under XACT_ABORT ON
    END TRY
    BEGIN CATCH
        IF XACT_STATE() = -1
            ROLLBACK;
        SET @si_after = SCOPE_IDENTITY();                  -- cell d: post-resurrection si = 100 natively
    END CATCH

    SELECT @si_after AS si_d;
END
