-- DESIGN.md §20.4 corpus fixture: p04_fk_violation_trycatch
-- M3 scope: TRY/CATCH routing (§10.3) with a genuine statement-level fault (547 FK
-- violation). The CATCH reads ERROR_NUMBER()/ERROR_MESSAGE() directly (R7 exactness --
-- no re-materialization needed since these are DIRECT references the rewriter can see).
-- Also exercises D3/fact 21's unhandled-continuation model (F2): an UNCAUGHT
-- statement-level fault must leave @@ERROR/@@ROWCOUNT reading the fault number/0 on the
-- very next statement -- locks the §10 line review's F2 fix into the fidelity harness.
--
-- Uses PERMANENT dbo tables, not #temp tables: verified live (sqlcmd) that SQL Server
-- silently does NOT enforce FOREIGN KEY constraints on local/global temp tables ("Skipping
-- FOREIGN KEY constraint ... FOREIGN KEY constraints are not enforced on local or global
-- temporary tables") -- an earlier #temp-table version of this fixture never actually
-- faulted, and its fidelity test passed vacuously (NULL == NULL on both sides) without
-- exercising any of the intended routing/continuation mechanics. Recreated each deploy
-- so repeated test runs start from a clean, deterministic slate.
IF OBJECT_ID('dbo.p04_child') IS NOT NULL DROP TABLE dbo.p04_child;
IF OBJECT_ID('dbo.p04_parent') IS NOT NULL DROP TABLE dbo.p04_parent;
CREATE TABLE dbo.p04_parent (id int PRIMARY KEY);
CREATE TABLE dbo.p04_child (id int PRIMARY KEY, parent_id int REFERENCES dbo.p04_parent (id));
GO

CREATE OR ALTER PROCEDURE dbo.p04_fk_violation_trycatch
    @Mode int
AS
BEGIN
    SET NOCOUNT ON;

    INSERT INTO dbo.p04_parent (id) VALUES (1);

    DECLARE @CaughtNumber int;
    DECLARE @CaughtMessage nvarchar(4000);
    BEGIN TRY
        INSERT INTO dbo.p04_child (id, parent_id) VALUES (100, 999);   -- FK violation, error 547
    END TRY
    BEGIN CATCH
        SET @CaughtNumber = ERROR_NUMBER();
        SET @CaughtMessage = ERROR_MESSAGE();
    END CATCH

    -- fact 21/F2: an uncaught statement-level fault continues natively; the next
    -- statement's @@ERROR/@@ROWCOUNT read the fault number/0.
    DECLARE @UncaughtErrorNumber int;
    DECLARE @UncaughtRowcount int;
    IF @Mode = 1
    BEGIN
        INSERT INTO dbo.p04_child (id, parent_id) VALUES (200, 888);   -- FK violation, uncaught
        SET @UncaughtErrorNumber = @@ERROR;
        SET @UncaughtRowcount = @@ROWCOUNT;
    END

    SELECT
        @CaughtNumber AS CaughtNumber,
        @CaughtMessage AS CaughtMessage,
        @UncaughtErrorNumber AS UncaughtErrorNumber,
        @UncaughtRowcount AS UncaughtRowcount;
END
