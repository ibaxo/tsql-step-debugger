-- docs/DESIGN.md §20.4 corpus fixture: p29_alias_type_variable
-- A59 scope: user-defined ALIAS (scalar) types — §8.1's storage-type split.
--
-- Every variable here is declared at a type tempdb cannot see (fact 34a, msg 2715) and
-- that CONVERT refuses outright (fact 34b, msg 243). Before A59 this procedure killed the
-- session at frame init, on the `CREATE TABLE #__dbg_s0 (…)` — it never reached a step.
-- The fixture pins all four sites the declared type used to leak into:
--   * an alias-typed PARAMETER and alias-typed LOCALS (state-table columns);
--   * a re-reached SET in a loop, so the value round-trips state on every step;
--   * a table variable with an ALIAS-TYPED COLUMN — legal natively (fact 34c), and the
--     one shape whose #temp realization must base-resolve the column or raise 2715;
--   * a default-valued alias parameter (the synthetic-SET initializer path).
-- Zero exemptions: the debugger must be byte-identical to native here.

IF TYPE_ID('dbo.p29_Name') IS NULL
    CREATE TYPE dbo.p29_Name FROM nvarchar(50) NOT NULL;
GO
IF TYPE_ID('dbo.p29_Amount') IS NULL
    CREATE TYPE dbo.p29_Amount FROM decimal(9,2);
GO
CREATE OR ALTER PROCEDURE dbo.p29_alias_type_variable
    @Customer dbo.p29_Name,
    @Rate dbo.p29_Amount = 2.50           -- alias-typed parameter WITH a default
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @Greeting dbo.p29_Name = N'Hello, ';   -- alias local, with initializer
    DECLARE @Total dbo.p29_Amount;                 -- alias local, no initializer (seeds NULL)
    DECLARE @Loops int = 0;

    SET @Greeting = @Greeting + @Customer;
    SET @Total = 0;

    -- re-reached statements: @Total round-trips the state table (as its BASE type) on
    -- every single step, so a lossy storage type would show up as a wrong decimal here.
    WHILE @Loops < 3
    BEGIN
        SET @Total = @Total + @Rate;
        SET @Loops += 1;
    END

    DECLARE @Names TABLE (id int IDENTITY(1,1), nm dbo.p29_Name);
    INSERT INTO @Names (nm) VALUES (@Customer), (@Greeting);

    SELECT
        @Greeting                                                        AS Greeting,
        @Total                                                           AS Total,
        (SELECT COUNT(*) FROM @Names)                                    AS NameCount,
        (SELECT MAX(LEN(nm)) FROM @Names)                                AS LongestName,
        CONVERT(nvarchar(30),
            SQL_VARIANT_PROPERTY(CAST(@Total AS sql_variant), 'BaseType')) AS TotalBaseType;
END
