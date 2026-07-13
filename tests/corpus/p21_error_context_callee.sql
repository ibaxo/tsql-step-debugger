-- DESIGN.md §20.4 corpus fixture: p21_error_context_callee
-- M7 D3 §3.2 (docs/archive/reviews/m7-hardening-design-notes-fable.md): C21's exact-vs-exempted
-- split, in §20.4's own words. dbo.p21_logger is the archetypal no-arg indirect
-- ERROR_*() consumer (§10.7's own worked example pattern: "BEGIN CATCH EXEC
-- dbo.usp_LogError; ..."). err_procedure is SCHEMA-QUALIFIED per fact 31c (D2,
-- orchestrator ruling docs/archive/reviews/m7-hardening-core-opus.md §2) -- native and the Into
-- pass both read `dbo.p21_error_context_callee`, not a bare name.
IF OBJECT_ID('dbo.p21_log') IS NULL
CREATE TABLE dbo.p21_log (
    id int IDENTITY(1,1) PRIMARY KEY,
    err_number int,
    err_severity int,
    err_state int,
    err_line int,
    err_procedure sysname NULL,
    err_message nvarchar(4000)
);
GO
TRUNCATE TABLE dbo.p21_log;
GO

-- No parameters -- the archetypal indirect ERROR_*() consumer (§10.7): reads all six
-- ERROR_*() intrinsics with nothing for the rewriter to bind them to lexically at the
-- call site; only dynamic extent (whether an error context is active when it runs)
-- decides what it observes.
CREATE OR ALTER PROCEDURE dbo.p21_logger
AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO dbo.p21_log (err_number, err_severity, err_state, err_line, err_procedure, err_message)
    VALUES (ERROR_NUMBER(), ERROR_SEVERITY(), ERROR_STATE(), ERROR_LINE(), ERROR_PROCEDURE(), ERROR_MESSAGE());
END
GO

CREATE OR ALTER PROCEDURE dbo.p21_error_context_callee
    @Result int OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @x int;
    BEGIN TRY
        SET @x = 1 / 0;
    END TRY
    BEGIN CATCH
        EXEC dbo.p21_logger;
        SET @Result = -1;
    END CATCH

    SELECT
        @Result AS Result,
        err_number AS ErrNumber, err_severity AS ErrSeverity, err_state AS ErrState,
        err_line AS ErrLine, err_procedure AS ErrProcedure, err_message AS ErrMessage
    FROM dbo.p21_log;
END
GO
