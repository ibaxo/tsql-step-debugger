-- DESIGN.md §20.4 corpus fixture: p07_recursion_factorial
-- M4 scope: recursion (§11.4) -- frame ordinals monotonically increase per session, so
-- the SAME module pushed repeatedly gets a distinct frame/state-table each activation.
-- Also exercises OUTPUT copy-back cascading through several stacked completed pops
-- (fact 23) and breakpoint module-identity matching across recursive activations
-- (§13, the same source module at different stack depths).
CREATE OR ALTER PROCEDURE dbo.p07_factorial_inner
    @N int,
    @Result bigint OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    IF @N <= 1
    BEGIN
        SET @Result = 1;
    END
    ELSE
    BEGIN
        -- EXEC argument values must be literals/variables, not arbitrary expressions
        -- (verified live: `EXEC proc @p = @N - 1` is a T-SQL syntax error) -- compute
        -- into an intermediate variable first.
        DECLARE @Sub bigint;
        DECLARE @NextN int = @N - 1;
        EXEC dbo.p07_factorial_inner @N = @NextN, @Result = @Sub OUTPUT;
        SET @Result = @N * @Sub;
    END
END
GO

CREATE OR ALTER PROCEDURE dbo.p07_recursion_factorial
    @N int
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @Result bigint;
    EXEC dbo.p07_factorial_inner @N = @N, @Result = @Result OUTPUT;
    SELECT @Result AS Result;
END
GO
