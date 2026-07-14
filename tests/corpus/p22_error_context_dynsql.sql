-- DESIGN.md §20.4 corpus fixture: p22_error_context_dynsql -- C21 through dynamic SQL.
--
-- A58 (§11.6) gave this fixture an EXACT pass, which it did not have before. Previously C10
-- made step-into refuse dynamic SQL, so every pass stepped over the EXEC(@s) and
-- re-materialized the active error context (§10.7) around it; re-materialization raises a NEW
-- error, so the dynamic child's ERROR_NUMBER() read RAISERROR's 50000 rather than the real
-- 8134, and num was exempt (C21) on all three passes.
--
-- Now the INTO pass pushes a dynamic frame: R7 substitution reaches the ERROR_*() references
-- inside the dynamic text and the shadow serves the true 8134 -- exactly C21's own prescribed
-- remedy ("step into the callee for exact per-statement values"), which was unavailable for
-- dynamic SQL until A58. So Into is exact and zero-exemption (like p21, its procedure-callee
-- twin); over/boost still step over, still re-materialize, and keep the C21 exemption -- and
-- remain the corpus-level twin of Fact7RematerializationLiveTests, proving the §10.7 shell
-- reaches dynamic children.
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
