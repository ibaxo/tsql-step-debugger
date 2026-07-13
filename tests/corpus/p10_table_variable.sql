-- DESIGN.md §20.4 corpus fixture: p10_table_variable
-- M4 scope: R1 (table variables) hoisted #temp realization, and caveat C25 — table
-- variables are non-transactional natively (fact 2) but realized as a #temp table
-- (transactional), so a debuggee ROLLBACK reverts/destroys the realization's contents
-- where native preserves them. Structure is healed empty (D8's detached-edge
-- reconcile); contents are honestly lost. See tests/corpus/p10.manifest.json for the
-- §20.3.1.6 exemption citing C25 — this is the first fixture to use the manifest
-- mechanism at all (no *.manifest.json existed in the corpus before this).
CREATE OR ALTER PROCEDURE dbo.p10_table_variable
    @Seed int
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @t TABLE (id int IDENTITY(1,1) PRIMARY KEY, val int);
    INSERT INTO @t (val) VALUES (@Seed), (@Seed + 1);

    DECLARE @BeforeRollback int = (SELECT COUNT(*) FROM @t);

    -- a plain debuggee ROLLBACK crosses the table variable. Natively @t is untouched
    -- by ANY rollback (non-transactional); under the debugger the #temp realization
    -- IS transactional, so this content is lost (C25) even though the realization's
    -- STRUCTURE survives (re-created empty, D8).
    BEGIN TRANSACTION;
    INSERT INTO @t (val) VALUES (999);
    ROLLBACK;

    DECLARE @AfterRollback int = (SELECT COUNT(*) FROM @t);

    SELECT @BeforeRollback AS BeforeRollback, @AfterRollback AS AfterRollback;
END
