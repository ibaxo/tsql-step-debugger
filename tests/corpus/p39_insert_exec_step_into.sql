-- docs/DESIGN.md §20.4 corpus fixture: p39_insert_exec_step_into
-- C11 (A64): stepping INTO an `INSERT <target> EXEC proc` pushes a frame over the callee and
-- CAPTURES the callee's result-returning statements into the target (native result-capture
-- semantics), instead of streaming them. The callee emits two result sets (captured into #t) and
-- one assignment SELECT (not captured). With @Seed=5 the target ends up {5, 50}, compared against
-- a native EXEC. Two procs, GO-separated (the fidelity test splits on GO before deploying).
CREATE OR ALTER PROCEDURE dbo.p39_ie_callee
    @Seed int
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @x int = @Seed * 10;
    SELECT @Seed AS v;          -- result-returning → captured into the caller's target
    SELECT @x AS v;             -- result-returning → captured
    DECLARE @ignore int;
    SELECT @ignore = 999;       -- variable assignment → NOT captured, runs normally
END
GO
CREATE OR ALTER PROCEDURE dbo.p39_insert_exec
    @Seed int
AS
BEGIN
    SET NOCOUNT ON;
    CREATE TABLE #t (v int);
    INSERT INTO #t (v) EXEC dbo.p39_ie_callee @Seed = @Seed;
    SELECT v FROM #t ORDER BY v;
END
GO
-- S1 (review): a CTE-headed SELECT cannot be wrapped `INSERT INTO #t WITH cte …` (msg 156). The
-- capture-safety scan refuses step-into → faithful native step-over. With @Seed=7 → {7, 8}.
CREATE OR ALTER PROCEDURE dbo.p39_cte_callee
    @Seed int
AS
BEGIN
    SET NOCOUNT ON;
    WITH cte AS (SELECT @Seed AS v UNION ALL SELECT @Seed + 1) SELECT v FROM cte;
END
GO
CREATE OR ALTER PROCEDURE dbo.p39_cte_caller
    @Seed int
AS
BEGIN
    SET NOCOUNT ON;
    CREATE TABLE #t (v int);
    INSERT INTO #t (v) EXEC dbo.p39_cte_callee @Seed = @Seed;
    SELECT v FROM #t ORDER BY v;
END
GO
-- I3 (review): a WHILE loop in the callee — boost must be refused in a capture frame so the loop's
-- SELECTs are captured (interpreted), not streamed raw. With continue+boost → {1, 2, 3}.
CREATE OR ALTER PROCEDURE dbo.p39_boost_callee
    @Seed int
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @i int = 1;
    WHILE @i <= 3
    BEGIN
        SELECT @i AS v;
        SET @i += 1;
    END
END
GO
CREATE OR ALTER PROCEDURE dbo.p39_boost_caller
    @Seed int
AS
BEGIN
    SET NOCOUNT ON;
    CREATE TABLE #t (v int);
    INSERT INTO #t (v) EXEC dbo.p39_boost_callee @Seed = @Seed;
    SELECT v FROM #t ORDER BY v;
END
GO
-- A65 (§11.7): a permanent target table so the callee references it by a STABLE name (no per-frame
-- #temp renaming), keeping the phantom-read fixture purely about capture semantics. Idempotent.
IF OBJECT_ID('dbo.p39_captgt', 'U') IS NULL CREATE TABLE dbo.p39_captgt (v int);
GO
-- I7 (A65): a MID-capture callee error. Native BUFFERS INSERT…EXEC and materializes the target only
-- on the callee's success, so the captured rows NEVER reach the target — while the caller's own
-- pre-existing target rows are untouched — and the CATCH continues. The A65 stage is discarded on the
-- abnormal pop, reproducing exactly this ({77}); the A64 incremental wrap left the partially-captured
-- rows behind ({5, 15, 77} — the I7 residual this closes). (Side-effect atomicity in autocommit is a
-- SEPARATE, inherent safety-transaction property — not staged here, so no side table is involved.)
CREATE OR ALTER PROCEDURE dbo.p39_atomic_callee
    @Seed int
AS
BEGIN
    SET NOCOUNT ON;
    SELECT @Seed AS v;                                  -- captured (buffered)
    SELECT @Seed + 10 AS v;                             -- captured (buffered)
    RAISERROR('p39 mid-capture boom', 16, 1);           -- abort mid-capture
    SELECT 999 AS v;                                    -- never reached
END
GO
CREATE OR ALTER PROCEDURE dbo.p39_atomic_caller
    @Seed int
AS
BEGIN
    SET NOCOUNT ON;
    CREATE TABLE #t (v int);
    INSERT INTO #t (v) VALUES (77);                     -- pre-existing target row — must be untouched
    BEGIN TRY
        INSERT INTO #t (v) EXEC dbo.p39_atomic_callee @Seed = @Seed;
    END TRY
    BEGIN CATCH
        SELECT ERROR_NUMBER() AS caught;                -- the CATCH runs (50000)
    END CATCH
    SELECT v FROM #t ORDER BY v;                        -- {77} only — captured rows never materialized (I7)
END
GO
-- Success-path fidelity (A65): a callee that READS the capture target mid-execution. Native buffers,
-- so the callee sees only the target's PRE-EXISTING rows (count 1), never captured-so-far rows; the
-- A65 stage reproduces this. The A64 incremental wrap showed phantom rows (count 2), which could send
-- the debuggee down a DIFFERENT branch than native — the worst divergence class. With @Seed=5 → the
-- captured COUNT is 1, so the target ends {1, 5, 7, 99}.
CREATE OR ALTER PROCEDURE dbo.p39_phantom_callee
    @Seed int
AS
BEGIN
    SET NOCOUNT ON;
    SELECT @Seed AS v;                                  -- captured (buffered)
    SELECT COUNT(*) AS v FROM dbo.p39_captgt;           -- reads the target MID-capture (sees only {99})
    SELECT @Seed + 2 AS v;                              -- captured
END
GO
CREATE OR ALTER PROCEDURE dbo.p39_phantom_caller
    @Seed int
AS
BEGIN
    SET NOCOUNT ON;
    DELETE FROM dbo.p39_captgt;
    INSERT INTO dbo.p39_captgt (v) VALUES (99);         -- pre-existing target row
    INSERT INTO dbo.p39_captgt (v) EXEC dbo.p39_phantom_callee @Seed = @Seed;
    SELECT v FROM dbo.p39_captgt ORDER BY v;            -- {1, 5, 7, 99}
END
GO
-- F1 (A65 §10 review): the stage's seq IDENTITY must not corrupt the callee's SCOPE_IDENTITY shadow.
-- The callee does a REAL identity insert (50), then captures a plain SELECT (whose stage insert would
-- clobber the shadow to the stage seq if unguarded), then captures SCOPE_IDENTITY() — which must
-- still read 50. Target ends {50, 111}. Unguarded, the last row would be the stage seq.
CREATE OR ALTER PROCEDURE dbo.p39_scopeid_callee
    @Seed int
AS
BEGIN
    SET NOCOUNT ON;
    CREATE TABLE #log (x int IDENTITY(50, 1));
    INSERT INTO #log DEFAULT VALUES;                    -- real identity insert → SCOPE_IDENTITY() = 50
    SELECT 111 AS v;                                    -- captured (its stage insert must NOT set the shadow)
    SELECT CAST(SCOPE_IDENTITY() AS int) AS v;          -- captured — must read 50, not the stage seq
END
GO
CREATE OR ALTER PROCEDURE dbo.p39_scopeid_caller
    @Seed int
AS
BEGIN
    SET NOCOUNT ON;
    CREATE TABLE #t (v int);
    INSERT INTO #t (v) EXEC dbo.p39_scopeid_callee @Seed = @Seed;
    SELECT v FROM #t ORDER BY v;                        -- {50, 111}
END
GO
-- F-flush-fault (A65 §10 review, deferRoute): a captured value violates a target CHECK at
-- MATERIALIZATION. The stage is constraint-free (so nothing fires while the callee streams — native's
-- timing), and the flush `INSERT <target> SELECT … FROM <stage>` raises 547 as one atomic statement
-- (caught by the caller, target empty). The flush DEFERS its route (pends → next step routes at top
-- level) rather than routing re-entrantly from inside the pop. Result: {caught = 547, n = 0}.
CREATE OR ALTER PROCEDURE dbo.p39_flushfault_callee
    @Seed int
AS
BEGIN
    SET NOCOUNT ON;
    SELECT @Seed AS v;                                  -- 5 — within the CHECK
    SELECT 111 AS v;                                    -- violates the target CHECK (v < 100) at the flush
END
GO
CREATE OR ALTER PROCEDURE dbo.p39_flushfault_caller
    @Seed int
AS
BEGIN
    SET NOCOUNT ON;
    CREATE TABLE #t (v int CHECK (v < 100));
    BEGIN TRY
        INSERT INTO #t (v) EXEC dbo.p39_flushfault_callee @Seed = @Seed;
        SELECT -1 AS caught;                            -- reached only if the flush wrongly "succeeded"
    END TRY
    BEGIN CATCH
        SELECT ERROR_NUMBER() AS caught;                -- 547
    END CATCH
    SELECT COUNT(*) AS n FROM #t;                       -- 0 (atomic — no partial rows)
END
