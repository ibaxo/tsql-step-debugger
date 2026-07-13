-- DESIGN.md §20.4 corpus fixture: p03_while_break_continue
-- M2 scope: WHILE/BREAK/CONTINUE. Exercises D3's WHILE-predicate variant of fact 12
-- (predicate reads @@ROWCOUNT, first body statement reads it again before BREAK),
-- fact 14 C (a DECLARE-with-initializer inside a loop body reruns its initializer every
-- iteration, no duplicate-declaration error), and nested BREAK/CONTINUE resuming the
-- correct enclosing loop.
CREATE OR ALTER PROCEDURE dbo.p03_while_break_continue
    @Outer int
AS
BEGIN
    SET NOCOUNT ON;

    CREATE TABLE #p03 (id int IDENTITY(1,1) PRIMARY KEY, val int);
    INSERT INTO #p03 (val) VALUES (1), (2), (3);        -- @@ROWCOUNT := 3

    -- fact-12/D3 WHILE variant: predicate reads @@ROWCOUNT, first body statement reads
    -- it again before BREAK.
    DECLARE @RowcountInLoop int;
    WHILE @@ROWCOUNT = 3
    BEGIN
        SET @RowcountInLoop = @@ROWCOUNT;
        BREAK;
    END

    -- fact 14 C: DECLARE-with-initializer inside a WHILE body reruns its initializer
    -- every iteration without a duplicate-declaration error.
    DECLARE @i int = 0, @log nvarchar(200) = N'';
    WHILE @i < @Outer
    BEGIN
        DECLARE @Doubled int = @i * 2;
        SET @log += N'[' + CAST(@Doubled AS nvarchar(10)) + N']';
        SET @i += 1;
    END

    -- Nested BREAK/CONTINUE: the outer loop CONTINUEs past even values; the inner loop
    -- BREAKs once it passes its own midpoint. Neither should disturb the other's loop.
    DECLARE @j int = 0, @k int, @nested nvarchar(200) = N'';
    WHILE @j < @Outer
    BEGIN
        SET @j += 1;
        IF @j % 2 = 0
            CONTINUE;

        SET @k = 0;
        WHILE @k < @Outer
        BEGIN
            SET @k += 1;
            IF @k > 2
                BREAK;
            SET @nested += N'(' + CAST(@j AS nvarchar(10)) + N',' + CAST(@k AS nvarchar(10)) + N')';
        END
    END

    SELECT
        @RowcountInLoop AS RowcountInLoop,
        @log AS DoubledLog,
        @nested AS NestedLog;
END
