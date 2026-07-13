-- DESIGN.md §20.4 corpus fixture: p16_set_options_isolation
-- M4 scope: D6/§11.2 runtime SET-option tracking + pop restore, per fact 9 ("proc-
-- scoped SET options revert at module exit"). The engine reverts this natively; our
-- connection PERSISTS it across batches, so the debugger must emit restoring SETs on
-- pop. The callee changes its isolation level; the caller's own level (read via
-- sys.dm_exec_sessions, not a shadowed intrinsic) must be unaffected after the call
-- returns.
CREATE OR ALTER PROCEDURE dbo.p16_callee_changes_isolation
AS
BEGIN
    SET NOCOUNT ON;
    SET TRANSACTION ISOLATION LEVEL SERIALIZABLE;
    SELECT transaction_isolation_level AS CalleeIsolation
    FROM sys.dm_exec_sessions WHERE session_id = @@SPID;
END
GO

CREATE OR ALTER PROCEDURE dbo.p16_set_options_isolation
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @BeforeIsolation int = (
        SELECT transaction_isolation_level FROM sys.dm_exec_sessions WHERE session_id = @@SPID);

    EXEC dbo.p16_callee_changes_isolation;

    DECLARE @AfterIsolation int = (
        SELECT transaction_isolation_level FROM sys.dm_exec_sessions WHERE session_id = @@SPID);

    SELECT @BeforeIsolation AS BeforeIsolation, @AfterIsolation AS AfterIsolation;
END
GO
