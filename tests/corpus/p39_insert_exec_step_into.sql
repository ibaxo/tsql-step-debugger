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
