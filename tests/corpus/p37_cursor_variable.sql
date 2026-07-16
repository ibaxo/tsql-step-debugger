-- docs/DESIGN.md §20.4 corpus fixture: p37_cursor_variable
-- A63 scope: cursor VARIABLES (DECLARE @c CURSOR) — reified as a frame-unique GLOBAL
-- cursor (`{name}__f{frame}_cv`) created at the `SET @c = CURSOR FOR` site, so the FETCH
-- position survives the debugger's per-SU batches exactly like a named cursor (p08).
-- Exercises: DECLARE @c CURSOR (stoppable no-op), SET @c = CURSOR LOCAL FOR (reification),
-- OPEN/FETCH/CLOSE @c, a WHILE @@FETCH_STATUS loop, a RE-SET of @c to a second cursor
-- (the CURSOR_STATUS guard must deallocate the first physical before re-declaring), and
-- an explicit DEALLOCATE @c. With @Seed=10 the two loops sum to Total=363, Count=6, but
-- the harness compares against the native run — never hard-coded.
CREATE OR ALTER PROCEDURE dbo.p37_cursor_variable
    @Seed int
AS
BEGIN
    SET NOCOUNT ON;

    CREATE TABLE #p37 (id int IDENTITY(1,1) PRIMARY KEY, val int);
    INSERT INTO #p37 (val) VALUES (@Seed), (@Seed + 1), (@Seed + 2);

    DECLARE @Val int;
    DECLARE @Total int = 0;
    DECLARE @Count int = 0;
    DECLARE @c CURSOR;

    SET @c = CURSOR LOCAL FOR SELECT val FROM #p37 ORDER BY id;
    OPEN @c;
    FETCH NEXT FROM @c INTO @Val;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        SET @Total += @Val;
        SET @Count += 1;
        FETCH NEXT FROM @c INTO @Val;
    END
    CLOSE @c;

    -- Re-assign the SAME variable to a second cursor (the CURSOR_STATUS guard must
    -- deallocate the first cursor's physical before re-declaring under the same name).
    SET @c = CURSOR FOR SELECT val * 10 FROM #p37 ORDER BY id DESC;
    OPEN @c;
    FETCH NEXT FROM @c INTO @Val;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        SET @Total += @Val;
        SET @Count += 1;
        FETCH NEXT FROM @c INTO @Val;
    END
    CLOSE @c;
    DEALLOCATE @c;

    SELECT @Total AS Total, @Count AS Count;
END
