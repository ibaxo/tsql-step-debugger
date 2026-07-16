-- docs/DESIGN.md §20.4 corpus fixture: p31_tvp_step_into
-- A62 scope: STEP INTO a callee that takes a table-valued (READONLY) parameter — the half
-- of C9 that A59 left refused. Two callee kinds share the §11.3-step-2 push path, and this
-- fixture exercises BOTH:
--   (1) a stored PROCEDURE with a TVP formal (dbo.p31_consume), and
--   (2) an sp_executesql DYNAMIC BATCH with a TVP formal (the user-reported case),
--       mixed with scalar OUTPUT parameters so the actual-to-formal matching is tested
--       across a TVP and two OUTPUTs at once.
-- In each, the callee's own TVP realization (#temp) is seeded from the caller's table-type
-- variable realization (#temp -> #temp copy, §11.3 step 2). A TVP formal is READONLY, so
-- there is nothing to copy back — the callee only READS the rows.
--
-- Identity values are inserted CONTIGUOUSLY (1,2,3), so C28 (regenerated identities on the
-- copy) is not reachable and there are ZERO exemptions: the debugger must reproduce the
-- callee's aggregates over the copied rows exactly.

IF TYPE_ID('dbo.p31_Rows') IS NULL
    CREATE TYPE dbo.p31_Rows AS TABLE (
        id  int IDENTITY(1,1) NOT NULL PRIMARY KEY,
        nm  nvarchar(30) NOT NULL,
        qty int NOT NULL
    );
GO
CREATE OR ALTER PROCEDURE dbo.p31_consume
    @Rows dbo.p31_Rows READONLY               -- a TVP formal: always READONLY
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @s int = (SELECT SUM(qty) FROM @Rows);
    RETURN @s;                                -- proves the callee saw the copied rows
END
GO
CREATE OR ALTER PROCEDURE dbo.p31_tvp_step_into
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @t dbo.p31_Rows;
    INSERT INTO @t (nm, qty) VALUES (N'a', 10), (N'b', 20), (N'c', 30);

    -- (1) procedure callee with a TVP formal — stepped INTO under A62
    DECLARE @ProcSum int;
    EXEC @ProcSum = dbo.p31_consume @Rows = @t;

    -- (2) sp_executesql dynamic batch with a TVP formal + scalar OUTPUTs — stepped INTO
    DECLARE @DynCount int, @DynSum int;
    DECLARE @sql nvarchar(200) = N'SELECT @cnt = COUNT(*), @sum = SUM(qty) FROM @rows;';
    EXEC sys.sp_executesql @sql,
         N'@rows dbo.p31_Rows READONLY, @cnt int OUTPUT, @sum int OUTPUT',
         @rows = @t, @cnt = @DynCount OUTPUT, @sum = @DynSum OUTPUT;

    SELECT @ProcSum AS ProcSum, @DynCount AS DynCount, @DynSum AS DynSum;
END
