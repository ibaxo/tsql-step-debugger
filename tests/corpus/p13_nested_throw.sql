-- DESIGN.md §20.4 corpus fixture: p13_nested_throw
-- M3 scope: nested TRY/CATCH + bare THROW re-raise (§10.2 D4). The inner CATCH reads
-- ERROR_NUMBER() (direct R7 reference, exact) then re-raises with a bare THROW; the
-- outer CATCH must see the SAME original number/message, not a new error -- the D4
-- exactness claim (bare THROW is interpreted client-side as an exact re-raise, never a
-- server round trip).
CREATE OR ALTER PROCEDURE dbo.p13_nested_throw
    @Mode int
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @Dummy int;
    DECLARE @InnerNumber int;
    DECLARE @OuterNumber int;
    DECLARE @OuterMessage nvarchar(4000);

    BEGIN TRY
        BEGIN TRY
            SET @Dummy = 1 / 0;          -- 8134
        END TRY
        BEGIN CATCH
            SET @InnerNumber = ERROR_NUMBER();
            THROW;                        -- bare re-raise: exact original values
        END CATCH
    END TRY
    BEGIN CATCH
        SET @OuterNumber = ERROR_NUMBER();
        SET @OuterMessage = ERROR_MESSAGE();
    END CATCH

    SELECT
        @InnerNumber AS InnerNumber,
        @OuterNumber AS OuterNumber,
        @OuterMessage AS OuterMessage;
END
