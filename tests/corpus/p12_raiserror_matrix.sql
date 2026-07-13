-- DESIGN.md §20.4 corpus fixture: p12_raiserror_matrix
-- M3 scope: the RAISERROR severity matrix (§10.2). Severity 0/10 are InfoMessages, not
-- errors (never route, never fault); severity 16 caught by an immediate TRY/CATCH;
-- severity 16 uncaught continues the batch natively (fact 18/20/21) -- the next
-- statement's @@ERROR/@@ROWCOUNT lock the §10 line review's F2 fix into the harness.
-- WITH NOWAIT exercises the same InfoMessage path with the flush option (cosmetic on
-- the wire; native behavior is otherwise identical).
CREATE OR ALTER PROCEDURE dbo.p12_raiserror_matrix
    @Mode int
AS
BEGIN
    SET NOCOUNT ON;

    RAISERROR('info sev 0', 0, 1);
    RAISERROR('info sev 10', 10, 1) WITH NOWAIT;

    DECLARE @CaughtNumber int;
    DECLARE @CaughtMessage nvarchar(4000);
    BEGIN TRY
        RAISERROR('caught sev 16', 16, 1);
    END TRY
    BEGIN CATCH
        SET @CaughtNumber = ERROR_NUMBER();
        SET @CaughtMessage = ERROR_MESSAGE();
    END CATCH

    -- fact 21/F2: uncaught statement-level RAISERROR continues; next statement's
    -- shadows read the fault number/0.
    DECLARE @UncaughtErrorNumber int;
    DECLARE @UncaughtRowcount int;
    RAISERROR('uncaught sev 16', 16, 1);
    SET @UncaughtErrorNumber = @@ERROR;
    SET @UncaughtRowcount = @@ROWCOUNT;

    SELECT
        @CaughtNumber AS CaughtNumber,
        @CaughtMessage AS CaughtMessage,
        @UncaughtErrorNumber AS UncaughtErrorNumber,
        @UncaughtRowcount AS UncaughtRowcount;
END
