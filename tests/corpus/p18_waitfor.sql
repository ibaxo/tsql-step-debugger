-- DESIGN.md §20.4 corpus fixture: p18_waitfor
-- M2 scope: WAITFOR DELAY, default launch policy "skip" (§22 M2 accept: "WAITFOR-
-- skip"). Native genuinely waits; the debugger in skip mode intercepts and logs
-- instead of sending the statement (§6/§14 D8) -- an intentional timing divergence
-- (caveat, not a fidelity break), so this fixture's assertions are timing-independent:
-- only the values on either side of the WAITFOR must match.
CREATE OR ALTER PROCEDURE dbo.p18_waitfor
    @Seed int
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @Before int = @Seed;
    WAITFOR DELAY '00:00:01';
    DECLARE @After int = @Before * 2;

    SELECT @Before AS Before, @After AS After;
END
