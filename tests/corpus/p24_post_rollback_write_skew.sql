-- DESIGN.md §20.4 corpus fixture: p24_post_rollback_write_skew
-- M4-exit obligation (docs/archive/reviews/m3-gate-review-fable.md §3, ruled on again in
-- docs/archive/reviews/m4-c23-doom-temp-severity-fable.md §5.3): C24's caveat, still owed
-- after C23's fixture obligation turned out unfulfillable as originally phrased.
-- C24 is a pure value skew (unlike C23's object-existence/session-terminating face):
-- after the debuggee's own ROLLBACK exits a doomed transaction, @@TRANCOUNT stays
-- faithful to native (0) only until the first statement requiring rollback
-- protection -- from THAT statement onward it reads +1 vs native (the §16 safety
-- net re-opens, deferred per A9). See tests/corpus/p24.manifest.json for the
-- §20.3.1.6 exemption citing C24.
CREATE OR ALTER PROCEDURE dbo.p24_post_rollback_write_skew
    @Seed int
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    CREATE TABLE #p24 (id int IDENTITY(1,1) PRIMARY KEY, val int);
    INSERT INTO #p24 (val) VALUES (@Seed);

    DECLARE @Dummy int;
    DECLARE @CaughtNumber int;
    BEGIN TRY
        SET @Dummy = 1 / 0;         -- dooms the transaction (XACT_ABORT ON)
    END TRY
    BEGIN CATCH
        SET @CaughtNumber = ERROR_NUMBER();
        IF XACT_STATE() = -1
            ROLLBACK;
    END CATCH

    -- #p24 was created in-transaction, so the rollback destroys it natively too
    -- (fact 1 -- not a C24 concern, identical on both sides). The first PROTECTED
    -- write after the rollback is C24's boundary: native stays at trancount 0
    -- (autocommit, nothing re-opened a transaction); the debugger's deferred
    -- resurrection (A9) re-opens its own safety transaction right before this write.
    CREATE TABLE #p24_after (id int IDENTITY(1,1) PRIMARY KEY, val int);
    INSERT INTO #p24_after (val) VALUES (999);

    DECLARE @TrancountAfterWrite int = @@TRANCOUNT;

    SELECT @CaughtNumber AS CaughtNumber, @TrancountAfterWrite AS TrancountAfterWrite;
END
