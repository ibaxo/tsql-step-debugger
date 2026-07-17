-- docs/DESIGN.md §20.4 corpus fixture: p41_capture_propagation
-- C11 (A67, §11.7): stepping INTO a plain nested `EXEC proc` while inside an `INSERT … EXEC`
-- capture PROPAGATES the capture — the child frame inherits the ancestor stage's insert ref
-- (CaptureTargetSql) but owns no stage and no flush, so its result-returning statements redirect
-- into the SAME shared seq-ordered stage that the owner materializes at its completed pop. Native
-- buffers the WHOLE callee subtree's result stream (engine fact 35), so stepping Over and Into must
-- both reproduce a native EXEC byte-for-byte. Every proc takes @Seed int (the harness passes 5).

-- === one-level success: a plain nested EXEC's result rows are captured too (fact 35a) ===
CREATE OR ALTER PROCEDURE dbo.p41_prop_deep @Seed int AS
BEGIN
    SET NOCOUNT ON;
    SELECT @Seed * 100 AS v;    -- 500 — captured into the SHARED stage via propagation
    SELECT @Seed * 200 AS v;    -- 1000 — captured
END
GO
CREATE OR ALTER PROCEDURE dbo.p41_prop_mid @Seed int AS
BEGIN
    SET NOCOUNT ON;
    SELECT @Seed AS v;                              -- 5 — captured (owner frame)
    EXEC dbo.p41_prop_deep @Seed = @Seed;          -- plain nested EXEC → step-into PROPAGATES
    SELECT @Seed * 4 AS v;                          -- 20 — captured
END
GO
CREATE OR ALTER PROCEDURE dbo.p41_prop @Seed int AS
BEGIN
    SET NOCOUNT ON;
    CREATE TABLE #t (v int);
    INSERT INTO #t (v) EXEC dbo.p41_prop_mid @Seed = @Seed;
    SELECT v FROM #t ORDER BY v;                    -- {5, 20, 500, 1000}
END
GO

-- === two-level success: propagation recurses (deep steps into deeper, same shared stage) ===
CREATE OR ALTER PROCEDURE dbo.p41_d2_deeper @Seed int AS
BEGIN
    SET NOCOUNT ON;
    SELECT @Seed * 999 AS v;    -- 4995 — captured two levels down
END
GO
CREATE OR ALTER PROCEDURE dbo.p41_d2_deep @Seed int AS
BEGIN
    SET NOCOUNT ON;
    SELECT @Seed * 100 AS v;    -- 500
    EXEC dbo.p41_d2_deeper @Seed = @Seed;          -- propagate again
    SELECT @Seed * 200 AS v;    -- 1000
END
GO
CREATE OR ALTER PROCEDURE dbo.p41_d2_mid @Seed int AS
BEGIN
    SET NOCOUNT ON;
    SELECT @Seed AS v;          -- 5
    EXEC dbo.p41_d2_deep @Seed = @Seed;
    SELECT @Seed * 4 AS v;      -- 20
END
GO
CREATE OR ALTER PROCEDURE dbo.p41_deep2 @Seed int AS
BEGIN
    SET NOCOUNT ON;
    CREATE TABLE #t (v int);
    INSERT INTO #t (v) EXEC dbo.p41_d2_mid @Seed = @Seed;
    SELECT v FROM #t ORDER BY v;                    -- {5, 20, 500, 1000, 4995}
END
GO

-- === fault in a nested child caught by the ancestor's TRY (fact 35b) — no special handling ===
-- The child's pre-fault rows STAY in the shared stage (its abnormal pop retains it — it owns none);
-- the ancestor catches, its CATCH's SELECT is captured, and the owner's completed-pop flush emits
-- {child-pre-fault, ancestor-pre-EXEC, catch-row} by seq. With @Seed=5 → target {5, 100, -1}.
CREATE OR ALTER PROCEDURE dbo.p41_fp_deep @Seed int AS
BEGIN
    SET NOCOUNT ON;
    SELECT @Seed * 20 AS v;     -- 100 — captured BEFORE the fault (survives the buffer)
    SELECT 1 / 0 AS v;          -- 8134 divide-by-zero mid-stream → propagates out to the ancestor TRY
    SELECT 777 AS v;            -- never reached
END
GO
CREATE OR ALTER PROCEDURE dbo.p41_fp_mid @Seed int AS
BEGIN
    SET NOCOUNT ON;
    BEGIN TRY
        SELECT @Seed AS v;                          -- 5 — captured
        EXEC dbo.p41_fp_deep @Seed = @Seed;        -- faults; caught here (cross-frame route)
        SELECT @Seed * 4 AS v;                      -- 20 — never reached
    END TRY
    BEGIN CATCH
        SELECT -1 AS v;                             -- captured (fact 35b: the CATCH handler's rows ARE captured)
    END CATCH
END
GO
CREATE OR ALTER PROCEDURE dbo.p41_faultprop @Seed int AS
BEGIN
    SET NOCOUNT ON;
    CREATE TABLE #t (v int);
    INSERT INTO #t (v) EXEC dbo.p41_fp_mid @Seed = @Seed;
    SELECT v FROM #t ORDER BY v;                    -- {-1, 5, 100}
END
GO

-- === propagation refused on an unsafe child body (CTE-headed result SELECT) → transparent step-over ===
-- The child body is re-scanned by CaptureSafetyScanner at its OWN push (the scanner cannot cross an
-- EXEC boundary), so a CTE-headed result SELECT refuses step-into → the child steps OVER, where native
-- captures it as one batch. The RESULT is identical (Into == Over == native); only the ability to step
-- into the child is lost. With @Seed=5 the cte yields {5, 6} → target {5, 5, 6, 20, 500, 1000}.
CREATE OR ALTER PROCEDURE dbo.p41_cte_deep @Seed int AS
BEGIN
    SET NOCOUNT ON;
    SELECT @Seed * 100 AS v;    -- 500
    WITH cte AS (SELECT @Seed AS v UNION ALL SELECT @Seed + 1)
    SELECT v FROM cte;          -- CTE-headed result SELECT → propagation REFUSED (deep stepped over)
    SELECT @Seed * 200 AS v;    -- 1000
END
GO
CREATE OR ALTER PROCEDURE dbo.p41_cte_mid @Seed int AS
BEGIN
    SET NOCOUNT ON;
    SELECT @Seed AS v;          -- 5
    EXEC dbo.p41_cte_deep @Seed = @Seed;
    SELECT @Seed * 4 AS v;      -- 20
END
GO
CREATE OR ALTER PROCEDURE dbo.p41_ctedeep @Seed int AS
BEGIN
    SET NOCOUNT ON;
    CREATE TABLE #t (v int);
    INSERT INTO #t (v) EXEC dbo.p41_cte_mid @Seed = @Seed;
    SELECT v FROM #t ORDER BY v;                    -- {5, 5, 6, 20, 500, 1000}
END
GO

-- === propagation refused: a nested INSERT…EXEC one level down (fact 35c → native msg 8164) ===
-- deep's body contains a nested INSERT…EXEC, so stepping into deep is refused → deep steps OVER as
-- `INSERT INTO <stage> EXEC deep`, and native raises 8164 (nested INSERT…EXEC). The fault has no TRY
-- in deep/mid, so it routes to the caller's TRY; the owner (mid) abnormal-pops and discards the stage,
-- leaving the target empty. Native EXEC does the same. Result {caught = 8164, n = 0}.
CREATE OR ALTER PROCEDURE dbo.p41_nie_deeper @Seed int AS
BEGIN
    SET NOCOUNT ON;
    SELECT @Seed * 7 AS v;      -- 35
END
GO
CREATE OR ALTER PROCEDURE dbo.p41_nie_deep @Seed int AS
BEGIN
    SET NOCOUNT ON;
    SELECT @Seed * 100 AS v;    -- 500
    DECLARE @inner TABLE (v int);
    INSERT INTO @inner (v) EXEC dbo.p41_nie_deeper @Seed = @Seed;   -- nested INSERT…EXEC → 8164
    SELECT @Seed * 200 AS v;    -- never reached
END
GO
CREATE OR ALTER PROCEDURE dbo.p41_nie_mid @Seed int AS
BEGIN
    SET NOCOUNT ON;
    SELECT @Seed AS v;          -- 5 — captured before the fault
    EXEC dbo.p41_nie_deep @Seed = @Seed;
    SELECT @Seed * 4 AS v;      -- never reached
END
GO
CREATE OR ALTER PROCEDURE dbo.p41_nie @Seed int AS
BEGIN
    SET NOCOUNT ON;
    CREATE TABLE #t (v int);
    BEGIN TRY
        INSERT INTO #t (v) EXEC dbo.p41_nie_mid @Seed = @Seed;
    END TRY
    BEGIN CATCH
        SELECT ERROR_NUMBER() AS caught;            -- 8164
    END CATCH
    SELECT COUNT(*) AS n FROM #t;                   -- 0 (buffer discarded on the abnormal pop)
END
GO

-- === cross-frame STREAM ORDER into an IDENTITY target (the load-bearing ordering property, C28) ===
-- The other fixtures re-sort with ORDER BY v, which MASKS seq order; this one makes it observable. The
-- target's IDENTITY auto-generates in flush order = the one shared stage's seq order = stream order, so
-- the id↔label pairing diverges if propagation broke seq monotonicity across the boundary. Native
-- assigns 1→mid-1, 2→deep-a, 3→deep-b, 4→mid-2 (verified live).
CREATE OR ALTER PROCEDURE dbo.p41_id_deep @Seed int AS
BEGIN
    SET NOCOUNT ON;
    SELECT 'deep-a' AS label;   -- captured 2nd (propagated child, same shared stage)
    SELECT 'deep-b' AS label;   -- captured 3rd
END
GO
CREATE OR ALTER PROCEDURE dbo.p41_id_mid @Seed int AS
BEGIN
    SET NOCOUNT ON;
    SELECT 'mid-1' AS label;    -- captured 1st (owner frame)
    EXEC dbo.p41_id_deep @Seed = @Seed;
    SELECT 'mid-2' AS label;    -- captured 4th
END
GO
CREATE OR ALTER PROCEDURE dbo.p41_ident @Seed int AS
BEGIN
    SET NOCOUNT ON;
    CREATE TABLE #t (id int IDENTITY(1, 1), label varchar(20));
    INSERT INTO #t (label) EXEC dbo.p41_id_mid @Seed = @Seed;
    SELECT id, label FROM #t ORDER BY id;           -- {(1,mid-1),(2,deep-a),(3,deep-b),(4,mid-2)}
END
GO

-- === dynamic child inside a capture → propagation refused → transparent step-over (both forms) ===
-- Stepping into a dynamic child (`EXEC(@sql)` / `sp_executesql`) while capturing is refused (its body
-- isn't redirected → would stream to the client; dynamic-source capture is a separate unbuilt item), so
-- the dynamic call steps OVER as `INSERT INTO <stage> EXEC(…)`, which native captures faithfully. The
-- result is identical (Into == Over == native); only the ability to step into the dynamic text is lost.
CREATE OR ALTER PROCEDURE dbo.p41_dyn_mid @Seed int AS
BEGIN
    SET NOCOUNT ON;
    SELECT @Seed AS v;                              -- 5 (owner)
    EXEC('SELECT 42 AS v');                         -- EXEC(@sql) dynamic child → refused → step-over, captured
    EXEC sp_executesql N'SELECT 43 AS v';           -- sp_executesql dynamic child → refused → step-over, captured
    SELECT @Seed * 4 AS v;                          -- 20
END
GO
CREATE OR ALTER PROCEDURE dbo.p41_dynchild @Seed int AS
BEGIN
    SET NOCOUNT ON;
    CREATE TABLE #t (v int);
    INSERT INTO #t (v) EXEC dbo.p41_dyn_mid @Seed = @Seed;
    SELECT v FROM #t ORDER BY v;                    -- {5, 20, 42, 43}
END
GO
