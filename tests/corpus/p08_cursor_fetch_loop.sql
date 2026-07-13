-- DESIGN.md §20.4 corpus fixture: p08_cursor_fetch_loop
-- M4 scope: R3 (cursors) -- DECLARE renames to a frame-suffixed GLOBAL name
-- (`{name}__f{frame}_c`); OPEN/FETCH/CLOSE/DEALLOCATE follow via chain lookup. Fact 24
-- group B (verified live, docs/engine-facts/fact24_...) confirmed cursors do NOT
-- survive a ROLLBACK regardless of open/declared-only state, matching §9's registry
-- model -- this fixture itself stays inside the harness's single rollback-wrapped
-- run, so no rollback-mid-cursor scenario is needed here.
CREATE OR ALTER PROCEDURE dbo.p08_cursor_fetch_loop
    @Seed int
AS
BEGIN
    SET NOCOUNT ON;

    CREATE TABLE #p08 (id int IDENTITY(1,1) PRIMARY KEY, val int);
    INSERT INTO #p08 (val) VALUES (@Seed), (@Seed + 1), (@Seed + 2);

    DECLARE @Val int;
    DECLARE @Total int = 0;
    DECLARE @Count int = 0;

    DECLARE cur CURSOR LOCAL FOR SELECT val FROM #p08 ORDER BY id;
    OPEN cur;
    FETCH NEXT FROM cur INTO @Val;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        SET @Total += @Val;
        SET @Count += 1;
        FETCH NEXT FROM cur INTO @Val;
    END
    CLOSE cur;
    DEALLOCATE cur;

    SELECT @Total AS Total, @Count AS Count;
END
