-- DESIGN.md §20.4 corpus fixture: p20_deferred_resolution
-- M3 scope: deferred name resolution (§10.1 no-control-row propagate class). A
-- reference to a table that never exists at execution time fails at batch-compile
-- time -- no control row, the preamble never ran -- exactly fact 1b's #temp-table
-- finding, generalized to an ordinary missing object. §20.3.4 expected-failure
-- comparison: BOTH sides must fail with error 208, not succeed.
CREATE OR ALTER PROCEDURE dbo.p20_deferred_resolution
    @Mode int
AS
BEGIN
    SET NOCOUNT ON;
    SELECT * FROM dbo.p20_DoesNotExist_Table;
END
