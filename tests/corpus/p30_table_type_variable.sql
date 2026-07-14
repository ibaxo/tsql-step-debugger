-- docs/DESIGN.md §20.4 corpus fixture: p30_table_type_variable
-- A59 scope: user-defined TABLE types — §8.2's "a table type declares a table, not a
-- scalar" and §9's catalog-generated realization + TVP materialization.
--
-- `DECLARE @t dbo.p30_Rows` is syntactically indistinguishable from `DECLARE @n dbo.Alias`
-- (fact 34, rider 3), so before A59 it was registered as a SCALAR and the session died at
-- frame init. The type deliberately carries every structural feature the realization DDL
-- has to rebuild from the catalog (fact 34f) — IDENTITY(10,5), a DEFAULT, a computed
-- column, a clustered PRIMARY KEY, a UNIQUE constraint, and a CHECK — so a generated DDL
-- that drops any of them fails here rather than silently in the field:
--   * the DEFAULT is exercised by the third INSERT (which omits qty);
--   * the computed column is read back (Doubled);
--   * IDENTITY values are read back (MaxId);
--   * the CHECK and the keys are load-bearing structure, not decoration.
-- Finally the variable is passed as a TVP ARGUMENT to a procedure that is STEPPED OVER
-- (C9) — the §9 preamble materialization, which is the only way a #temp realization can
-- reach a table-valued parameter at all.
--
-- Identity values are inserted CONTIGUOUSLY here, so C28 (regenerated identities on
-- materialization) is not reachable: 10/15/20 in the realization, 10/15/20 in the TVP.
-- Zero exemptions.

IF TYPE_ID('dbo.p30_Rows') IS NULL
    CREATE TYPE dbo.p30_Rows AS TABLE (
        id      int IDENTITY(10,5) NOT NULL,
        nm      nvarchar(30) NOT NULL,
        qty     decimal(9,3) NULL DEFAULT ((1.5)),
        doubled AS (qty * 2),
        PRIMARY KEY CLUSTERED (id),
        UNIQUE (nm),
        CHECK (qty > 0)
    );
GO
CREATE OR ALTER PROCEDURE dbo.p30_consume_rows
    @Rows dbo.p30_Rows READONLY               -- a TVP formal: always READONLY
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @n int = (SELECT COUNT(*) FROM @Rows);
    DECLARE @minid int = (SELECT MIN(id) FROM @Rows);
    RETURN @n * 100 + @minid;                 -- proves the callee saw BOTH rows and ids
END
GO
CREATE OR ALTER PROCEDURE dbo.p30_table_type_variable
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @t dbo.p30_Rows;
    INSERT INTO @t (nm, qty) VALUES (N'alpha', 2.000), (N'beta', 3.500);
    INSERT INTO @t (nm) VALUES (N'gamma');    -- DEFAULT ((1.5)) supplies qty

    DECLARE @Cnt int = (SELECT COUNT(*) FROM @t);
    DECLARE @Total decimal(12,3) = (SELECT SUM(qty) FROM @t);
    DECLARE @Doubled decimal(12,3) = (SELECT SUM(doubled) FROM @t);   -- computed column
    DECLARE @MaxId int = (SELECT MAX(id) FROM @t);                    -- IDENTITY(10,5) -> 20

    DECLARE @CalleeCode int;
    EXEC @CalleeCode = dbo.p30_consume_rows @Rows = @t;               -- TVP argument (§9)

    SELECT @Cnt AS Cnt, @Total AS Total, @Doubled AS Doubled,
           @MaxId AS MaxId, @CalleeCode AS CalleeCode;
END
