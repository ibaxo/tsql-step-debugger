-- DESIGN.md §20.4 corpus fixture: p23_missing_temp_uncatchable
-- M7 D3 §3.4 (docs/archive/reviews/m7-hardening-design-notes-fable.md): §10.1 fact-1b
-- addendum -- caller-CATCH propagation of a callee's deferred-resolution error.
-- dbo.p23_inner references a #temp table that is never created anywhere in the whole
-- session; this is a same-scope compile-class error (fact 1b) -- p23_inner's OWN CATCH
-- never runs (natively unreachable: the whole reference fails before execution reaches
-- any CATCH machinery in that scope), but an ENCLOSING frame's CATCH legitimately
-- catches it (fact 6) -- the caller below. err_procedure is SCHEMA-QUALIFIED per fact
-- 31c/D2 on the Into pass (dbo.p23_inner); the Over pass reads it GENUINE off the
-- engine directly (a real nested-proc fault, no synthesis needed at all -- §10.2's
-- pre-existing "verbatim for a real module" rule).
IF OBJECT_ID('dbo.p23_log') IS NULL
CREATE TABLE dbo.p23_log (
    id int IDENTITY(1,1) PRIMARY KEY,
    err_number int,
    err_procedure sysname NULL,
    err_message nvarchar(4000)
);
GO
TRUNCATE TABLE dbo.p23_log;
GO

CREATE OR ALTER PROCEDURE dbo.p23_inner
AS
BEGIN
    SET NOCOUNT ON;
    BEGIN TRY
        INSERT INTO #p23_missing VALUES (1);   -- #p23_missing exists nowhere -- 208
    END TRY
    BEGIN CATCH
        -- Natively NEVER runs: a same-scope deferred-resolution (compile-class) error
        -- is uncatchable at this scope (fact 1b) -- the whole reference fails before
        -- this CATCH could ever be reached from within p23_inner's own execution.
        SELECT 1;
    END CATCH
END
GO

CREATE OR ALTER PROCEDURE dbo.p23_missing_temp_uncatchable
    @Result int OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    BEGIN TRY
        EXEC dbo.p23_inner;
    END TRY
    BEGIN CATCH
        INSERT INTO dbo.p23_log (err_number, err_procedure, err_message)
        VALUES (ERROR_NUMBER(), ERROR_PROCEDURE(), ERROR_MESSAGE());
        SET @Result = ERROR_NUMBER();
    END CATCH

    SELECT
        @Result AS Result,
        err_number AS ErrNumber, err_procedure AS ErrProcedure, err_message AS ErrMessage
    FROM dbo.p23_log;
END
GO
