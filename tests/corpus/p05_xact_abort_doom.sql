-- DESIGN.md §20.4 corpus fixture: p05_xact_abort_doom
-- M3 scope: the §10.4 transaction watchdog end-to-end. XACT_ABORT ON dooms the
-- transaction on a runtime error even when TRY/CATCH catches it (fact 19 -- NOT
-- exempt); the CATCH's own `IF XACT_STATE() = -1 ROLLBACK` is the archetypal exit,
-- exercising doomed-mode variable seeding (§10.4/A6), the fact-19/D2 re-materialization
-- sandwich (this fixture's CATCH reads ERROR_NUMBER()/ERROR_MESSAGE() directly, so R7
-- handles it -- re-materialization only matters for INDIRECT readers, but the sandwich
-- still guards against dooming the transaction AGAIN from the RAISERROR that would
-- re-materialize the context for anything reached afterward), and resurrection
-- end-to-end (a statement AFTER the rollback must see the pre-rollback variable value,
-- proving the state table was re-seeded from the binary snapshot, not left stale).
CREATE OR ALTER PROCEDURE dbo.p05_xact_abort_doom
    @Seed int
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Before int = @Seed;
    DECLARE @Dummy int;
    DECLARE @CaughtNumber int;
    DECLARE @CaughtMessage nvarchar(4000);
    DECLARE @AfterRollback int;

    BEGIN TRY
        SET @Dummy = 1 / 0;       -- 8134, dooms the transaction under XACT_ABORT ON
    END TRY
    BEGIN CATCH
        SET @CaughtNumber = ERROR_NUMBER();
        SET @CaughtMessage = ERROR_MESSAGE();
        IF XACT_STATE() = -1
            ROLLBACK;
    END CATCH

    -- resurrection: @Before must survive past the debuggee's own ROLLBACK (real T-SQL
    -- variables are non-transactional; the state TABLE is not, so this only comes out
    -- right if the watchdog re-seeded it from the adapter's snapshot).
    SET @AfterRollback = @Before * 2;

    SELECT
        @Before AS Before,
        @CaughtNumber AS CaughtNumber,
        @CaughtMessage AS CaughtMessage,
        @AfterRollback AS AfterRollback,
        XACT_STATE() AS FinalXactState;
END
