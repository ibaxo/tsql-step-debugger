-- DESIGN.md §20.4 corpus fixture: p09_temp_tables_caller_callee
-- M4 scope: R2 (#temp tables) cross-frame visibility -- a #temp table the CALLER
-- creates is visible to a CALLEE it steps into (chain lookup, innermost-first, per
-- §9's registry) without the callee ever declaring it. Callee both reads and writes
-- the caller's table; the caller observes the callee's write after the call returns.
CREATE OR ALTER PROCEDURE dbo.p09_callee_uses_callers_temp
    @Increment int
AS
BEGIN
    SET NOCOUNT ON;
    -- #p09_shared is never declared in this frame -- it resolves through the frame
    -- chain to the caller's table (R2/§9).
    UPDATE #p09_shared SET val = val + @Increment;
    DECLARE @Sum int;
    SELECT @Sum = SUM(val) FROM #p09_shared;
    SELECT @Sum AS CalleeSum;
END
GO

CREATE OR ALTER PROCEDURE dbo.p09_temp_tables_caller_callee
    @Seed int
AS
BEGIN
    SET NOCOUNT ON;

    CREATE TABLE #p09_shared (id int IDENTITY(1,1) PRIMARY KEY, val int);
    INSERT INTO #p09_shared (val) VALUES (@Seed), (@Seed + 1), (@Seed + 2);

    EXEC dbo.p09_callee_uses_callers_temp @Increment = 10;

    -- the caller observes the callee's write to what is, physically, the SAME table.
    DECLARE @FinalSum int;
    SELECT @FinalSum = SUM(val) FROM #p09_shared;
    SELECT @FinalSum AS FinalSum;
END
GO
