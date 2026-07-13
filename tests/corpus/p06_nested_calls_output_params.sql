-- DESIGN.md §20.4 corpus fixture: p06_nested_calls_output_params
-- M4 scope: OUTPUT param + EXEC @rc copy-back, completion-gated per fact 23/§11.5
-- (C15 discharge). Three modes distinguish the termination classes fact 23 named:
--   Mode 0 — normal completion: copy-back + @rc happen.
--   Mode 1 — abort under a caller TRY: no copy-back, no @rc; caller values stay at
--            their pre-call seed even though the callee assigned @Out before faulting.
--   Mode 2 — no caller TRY anywhere: the callee's internal statement-level fault
--            continues natively (fact 21/23-H) to its own RETURN — this IS a completed
--            pop, so copy-back + @rc happen here too, unlike Mode 1's identical fault.
CREATE OR ALTER PROCEDURE dbo.p06_callee_normal
    @In int,
    @Out int OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET @Out = @In * 10;
    RETURN @In + 1;
END
GO

CREATE OR ALTER PROCEDURE dbo.p06_callee_faulting
    @In int,
    @Out int OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @Dummy int;
    SET @Out = @In * 100;        -- assigned BEFORE the fault
    SET @Dummy = 1 / 0;          -- 8134
    SET @Out = 999;              -- fact 21: the NEXT statement still runs (continuation)
    RETURN 55;
END
GO

CREATE OR ALTER PROCEDURE dbo.p06_nested_calls_output_params
    @Mode int
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @Result int = -1;
    DECLARE @Rc int = -1;

    IF @Mode = 0
    BEGIN
        EXEC @Rc = dbo.p06_callee_normal @In = 5, @Out = @Result OUTPUT;
    END
    ELSE IF @Mode = 1
    BEGIN
        BEGIN TRY
            EXEC @Rc = dbo.p06_callee_faulting @In = 5, @Out = @Result OUTPUT;
        END TRY
        BEGIN CATCH
            SET @Rc = -999;      -- sentinel: the CALLER's own CATCH ran
        END CATCH
    END
    ELSE
    BEGIN
        EXEC @Rc = dbo.p06_callee_faulting @In = 5, @Out = @Result OUTPUT;
    END

    SELECT @Result AS Result, @Rc AS Rc;
END
GO
