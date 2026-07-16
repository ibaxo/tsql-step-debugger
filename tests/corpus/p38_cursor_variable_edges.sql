-- docs/DESIGN.md §20.4 corpus fixture: p38_cursor_variable_edges
-- A63 Fable-review fixes (F1/F2/F3). Four procs, GO-separated (the fidelity test splits on
-- GO before deploying — each CREATE must be first in its batch):
--   p38_unallocated  (F1): OPEN a DECLARE'd-but-never-SET cursor variable → native error 16950,
--                    statement-level (the CATCH runs, the batch continues). Must NOT 137-session-kill.
--   p38_rollback     (F3): a cursor variable opened inside a transaction that ROLLS BACK is still
--                    usable (cursors are non-transactional — fact 24 corrected). FETCH still returns 42.
--   p38_leak_callee/_caller (F2): a callee's OWN unallocated @c must fault with 16950 (its own frame),
--                    NOT silently resolve to the CALLER's open cursor. The caller then reads its own row.
CREATE OR ALTER PROCEDURE dbo.p38_unallocated
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @c CURSOR;
    DECLARE @err int = 0, @after int = 0;
    BEGIN TRY
        OPEN @c;
    END TRY
    BEGIN CATCH
        SET @err = ERROR_NUMBER();
    END CATCH
    SET @after = 1;
    SELECT @err AS ErrNum, @after AS AfterRan;
END
GO
CREATE OR ALTER PROCEDURE dbo.p38_rollback
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @c CURSOR;
    DECLARE @v int = -1, @fs int = -99;
    BEGIN TRAN;
    SET @c = CURSOR STATIC FOR SELECT 42;
    OPEN @c;
    ROLLBACK;
    FETCH NEXT FROM @c INTO @v;
    SET @fs = @@FETCH_STATUS;
    CLOSE @c;
    DEALLOCATE @c;
    SELECT @v AS Val, @fs AS FetchStatus;
END
GO
CREATE OR ALTER PROCEDURE dbo.p38_leak_callee
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @c CURSOR;
    DECLARE @v int = -1, @n int = -99;
    BEGIN TRY
        FETCH NEXT FROM @c INTO @v;
    END TRY
    BEGIN CATCH
        SET @n = ERROR_NUMBER();
    END CATCH
    SELECT @v AS CalleeVal, @n AS CalleeErr;
END
GO
CREATE OR ALTER PROCEDURE dbo.p38_leak_caller
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @c CURSOR;
    DECLARE @v int, @fs int;
    SET @c = CURSOR FOR SELECT 999;
    OPEN @c;
    EXEC dbo.p38_leak_callee;
    FETCH NEXT FROM @c INTO @v;
    SET @fs = @@FETCH_STATUS;
    CLOSE @c;
    DEALLOCATE @c;
    SELECT @v AS CallerVal, @fs AS CallerFetch;
END
