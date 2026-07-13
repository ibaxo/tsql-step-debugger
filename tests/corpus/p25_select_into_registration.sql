-- DESIGN.md §20.4 corpus fixture: p25_select_into_registration
-- M6 R1 (A24): SELECT ... INTO #x is a create site under the SAME live-outer-collision
-- predicate as CREATE TABLE #x (§7.4 R2, §9) -- closing the M5-gate §9 item 1 residual
-- (caller SELECT INTO #x + callee CREATE TABLE #x). Both tables share the SAME column
-- shape (val int only) deliberately: SQL Server's deferred name resolution for a
-- nested proc's FIRST compile can bind its #temp reference to an already-live
-- outer-scope table of the same name rather than its own about-to-be-created one
-- (a documented native surprise, independent of this debugger) -- matching shapes
-- means the query is valid either way, so this fixture pins "debugger matches
-- whatever native actually does", not a specific resolution mechanism.
CREATE OR ALTER PROCEDURE dbo.p25_callee_creates_own
    @Seed int
AS
BEGIN
    SET NOCOUNT ON;
    CREATE TABLE #p25_data (val int);
    INSERT INTO #p25_data (val) VALUES (@Seed);
    DECLARE @CalleeSum int;
    SELECT @CalleeSum = SUM(val) FROM #p25_data;
    SELECT @CalleeSum AS CalleeSum;
END
GO

CREATE OR ALTER PROCEDURE dbo.p25_select_into_registration
    @Seed int
AS
BEGIN
    SET NOCOUNT ON;

    SELECT @Seed AS val INTO #p25_data;

    DECLARE @CalleeSeed int = @Seed + 100;
    EXEC dbo.p25_callee_creates_own @Seed = @CalleeSeed;

    -- the caller's OWN #p25_data (physically distinct on a real server, and made
    -- physically distinct on ours by the callee's collision rename) must be untouched.
    DECLARE @CallerSum int;
    SELECT @CallerSum = SUM(val) FROM #p25_data;
    SELECT @CallerSum AS CallerSum;
END
GO
