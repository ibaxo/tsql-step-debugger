-- docs/DESIGN.md §20.4 corpus fixture: p35_rowcount_edges
-- C13 review follow-ups (Fable NO-GO F2 + F7). Exercises two SET ROWCOUNT edges the p33 fixture
-- did not: (F2) a NON-LITERAL SET ROWCOUNT @n — the tracker only reads literals, so the debugger
-- must resolve the expression, else the limit is silently dropped and later statements run
-- unlimited; (F7) a genuine WHILE subtree so the boost pass actually boosts under the limit.
-- Native truth (probed live): VarLimited=2, LoopAcc=6.
--   VarLimited — SELECT INTO under SET ROWCOUNT @n (@n=2) sees 2 of 5 rows.
--   LoopAcc    — a WHILE (boostable) summing a COUNT(*) (aggregate: immune to ROWCOUNT) 3× = 2*3.
-- The dbo.p35_rows table type is also created here for the companion GO-boundary script test (F1).

IF TYPE_ID('dbo.p35_rows') IS NULL CREATE TYPE dbo.p35_rows AS TABLE (v int);
GO
CREATE OR ALTER PROCEDURE dbo.p35_rowcount_edges
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @src TABLE (v int);
    INSERT INTO @src (v) VALUES (1), (2), (3), (4), (5);

    -- (F2) non-literal limit — the debugger resolves @n so subsequent statements are limited
    DECLARE @n int = 2;
    SET ROWCOUNT @n;
    SELECT v INTO #a FROM @src;
    DECLARE @VarLimited int = (SELECT COUNT(*) FROM #a);

    -- (F7) a boostable WHILE subtree runs under the same limit; COUNT(*) is immune, so 2 each pass
    DECLARE @i int = 0, @acc int = 0;
    WHILE @i < 3
    BEGIN
        SET @acc = @acc + (SELECT COUNT(*) FROM #a);
        SET @i = @i + 1;
    END

    SET ROWCOUNT 0;
    SELECT @VarLimited AS VarLimited, @acc AS LoopAcc;
END
