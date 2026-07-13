-- DESIGN.md §20.4 corpus fixture: p02_if_else_branches
-- M2 scope: IF/ELSE branching. Exercises D3 (fact 12's "IF @@ROWCOUNT = N ... <branch
-- reads @@ROWCOUNT again>" pattern -- the predicate batch's own control row must never
-- be folded into the R4 shadow) and fact 14 B (a DECLARE-with-initializer inside a
-- branch that never runs is still hoisted and visible afterward as NULL, not error 137).
CREATE OR ALTER PROCEDURE dbo.p02_if_else_branches
    @Mode int
AS
BEGIN
    SET NOCOUNT ON;

    CREATE TABLE #p02 (id int IDENTITY(1,1) PRIMARY KEY, val int);
    INSERT INTO #p02 (val) VALUES (1), (2), (3);       -- @@ROWCOUNT := 3

    -- fact-12/D3: predicate reads @@ROWCOUNT, taken branch reads it again.
    DECLARE @RowcountInBranch int;
    IF @@ROWCOUNT = 3
        SET @RowcountInBranch = @@ROWCOUNT;

    DECLARE @ThenOnly int;
    DECLARE @ElseOnly int;

    IF @Mode > 0
    BEGIN
        DECLARE @ThenDeclared int = @Mode * 100;   -- fact 14 B: only initialized on THEN
        SET @ThenOnly = @ThenDeclared;
    END
    ELSE
    BEGIN
        DECLARE @ElseDeclared int = @Mode * -100;  -- fact 14 B: only initialized on ELSE
        SET @ElseOnly = @ElseDeclared;
    END

    -- fact 14: @ThenDeclared/@ElseDeclared are hoisted for the whole module regardless
    -- of which branch ran; whichever branch did NOT execute leaves its variable NULL.
    SELECT
        @Mode AS Mode,
        @RowcountInBranch AS RowcountInBranch,
        @ThenOnly AS ThenOnly,
        @ElseOnly AS ElseOnly,
        @ThenDeclared AS ThenDeclared,
        @ElseDeclared AS ElseDeclared;
END
