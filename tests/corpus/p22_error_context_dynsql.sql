-- DESIGN.md §20.4 corpus fixture: p22_error_context_dynsql
-- M7 D3 §3.3 (docs/archive/reviews/m7-hardening-design-notes-fable.md): C21 through dynamic
-- SQL -- NO exact pass exists for this fixture. C10 (dynamic SQL is an atomic black
-- box) makes step-into refuse and fall back to step-over, so EVERY pass re-materializes
-- the active error context (§10.7) around the EXEC(@s) -- exempt num (C21) on all three
-- passes; msg stays faithful everywhere (the corpus-level twin of
-- Fact7RematerializationLiveTests, proving the §10.7 shell reaches dynamic children).
IF OBJECT_ID('dbo.p22_log') IS NULL
CREATE TABLE dbo.p22_log (id int IDENTITY(1,1) PRIMARY KEY, msg nvarchar(4000), num int);
GO
TRUNCATE TABLE dbo.p22_log;
GO

CREATE OR ALTER PROCEDURE dbo.p22_error_context_dynsql
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @x int;
    BEGIN TRY
        SET @x = 1 / 0;
    END TRY
    BEGIN CATCH
        DECLARE @s nvarchar(max) = N'INSERT dbo.p22_log(msg, num) SELECT ERROR_MESSAGE(), ERROR_NUMBER();';
        EXEC (@s);
    END CATCH

    SELECT msg AS Msg, num AS Num FROM dbo.p22_log;
END
GO
