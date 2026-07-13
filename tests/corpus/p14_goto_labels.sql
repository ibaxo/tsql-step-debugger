-- DESIGN.md §20.4 corpus fixture: p14_goto_labels
-- M2 scope: GOTO/labels. Mirrors fact 14 A (forward GOTO over a DECLARE-with-
-- initializer -- hoisted and visible afterward, initializer never runs) and fact 14 D
-- (backward GOTO implementing a loop, landing past a DECLARE whose initializer
-- therefore never runs on any iteration).
CREATE OR ALTER PROCEDURE dbo.p14_goto_labels
    @Iterations int
AS
BEGIN
    SET NOCOUNT ON;

    -- fact 14 A: forward GOTO over a DECLARE-with-initializer; the variable is hoisted
    -- and visible afterward, but its initializer never runs (NULL, not error 137).
    GOTO SkipDeclare;
    DECLARE @Skipped int = 999;
    SkipDeclare:

    -- fact 14 D: backward GOTO implementing a loop, landing past a DECLARE whose
    -- initializer therefore never runs on ANY iteration (hoisted-but-uninitialized
    -- stays NULL for the whole module, not just the first pass).
    DECLARE @n int = 0, @log nvarchar(200) = N'';
    GOTO LoopTop;
    DECLARE @Never int = @n;
    LoopTop:
        SET @log += N'[n=' + CAST(@n AS nvarchar(10))
            + N' skipped=' + ISNULL(CAST(@Skipped AS nvarchar(10)), N'NULL')
            + N' never=' + ISNULL(CAST(@Never AS nvarchar(10)), N'NULL') + N']';
        SET @n += 1;
        IF @n < @Iterations
            GOTO LoopTop;

    SELECT @Skipped AS Skipped, @log AS Log, @n AS FinalN;
END
