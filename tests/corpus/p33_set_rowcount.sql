-- docs/DESIGN.md §20.4 corpus fixture: p33_set_rowcount
-- C13 (SET ROWCOUNT, frame-scoped): the debuggee's SET ROWCOUNT must limit the debuggee's own
-- statements EXACTLY as native, must revert at a callee's exit (§11.2 pop restore), and must NOT
-- truncate the debugger's own multi-row bookkeeping — specifically the TVP copy it performs when a
-- table-type variable is passed as a table-valued parameter (the step-OVER materialization AND the
-- A62 step-INTO formal seed are both INSERT … SELECT over the realization). Native truth (probed
-- live): LimitedSelect=2, TvpCount=5, AfterCalleeRevert=2.
--   LimitedSelect     — a debuggee SELECT under ROWCOUNT 2 sees 2 rows (already faithful pre-C13).
--   TvpCount          — the 5-row TVP passed under ROWCOUNT 2: the callee must still see all 5
--                       (native TVP passing is unaffected by ROWCOUNT). Pre-fix the debugger's copy
--                       was truncated to 2 → a real divergence (the point of the fixture).
--   AfterCalleeRevert — a callee that sets ROWCOUNT 1 must revert on exit; the caller's 2 is restored.

IF TYPE_ID('dbo.p33_Rows') IS NULL CREATE TYPE dbo.p33_Rows AS TABLE (v int);
GO
CREATE OR ALTER PROCEDURE dbo.p33_consume @Rows dbo.p33_Rows READONLY AS
BEGIN SET NOCOUNT ON; RETURN (SELECT COUNT(*) FROM @Rows); END      -- must see the whole TVP
GO
CREATE OR ALTER PROCEDURE dbo.p33_sets_rowcount AS
BEGIN SET NOCOUNT ON; SET ROWCOUNT 1; END                          -- changes ROWCOUNT; must revert at exit (§11.2)
GO
CREATE OR ALTER PROCEDURE dbo.p33_set_rowcount
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @t dbo.p33_Rows;
    INSERT INTO @t (v) VALUES (10), (20), (30), (40), (50);        -- 5 rows, BEFORE limiting

    SET ROWCOUNT 2;

    -- (a) a debuggee SELECT INTO is limited to 2 (faithful already — the limit persists on the connection)
    SELECT v INTO #lim FROM @t;
    DECLARE @LimitedSelect int = (SELECT COUNT(*) FROM #lim);

    -- (b) the 5-row TVP passed under ROWCOUNT 2 — the callee must still see all 5 rows
    DECLARE @TvpCount int;
    EXEC @TvpCount = dbo.p33_consume @Rows = @t;

    -- (c) a callee that changes ROWCOUNT reverts at exit; the caller's limit of 2 is restored
    EXEC dbo.p33_sets_rowcount;
    SELECT v INTO #lim2 FROM @t;
    DECLARE @AfterCalleeRevert int = (SELECT COUNT(*) FROM #lim2);

    SET ROWCOUNT 0;
    SELECT @LimitedSelect AS LimitedSelect, @TvpCount AS TvpCount, @AfterCalleeRevert AS AfterCalleeRevert;
END
