-- docs/DESIGN.md §20.4 corpus fixture: p42_dynamic_source_capture
-- C11 (A68, §11.7): dynamic-source capture. Stepping INTO an `INSERT <target> EXEC(@sql)` /
-- `EXEC sp_executesql @sql` pushes a DYNAMIC frame that OWNS the capture stage; stepping INTO a
-- dynamic child (`EXEC(@sql)` / `sp_executesql`) while an ancestor is capturing PROPAGATES the
-- capture into that dynamic frame. The capture machinery keys on the frame's CaptureTargetSql /
-- CaptureFlushSql, not on procedure-vs-dynamic, so a dynamic frame captures identically. Native
-- buffers a dynamic source's — and a nested dynamic child's — result stream into the target exactly
-- like a procedure (engine fact 36), so stepping Over and Into both reproduce a native EXEC
-- byte-for-byte. Refused sub-cases (unsafe dynamic body, nested INSERT…EXEC) step OVER, which native
-- still captures as one batch. Every proc takes @Seed int (the harness passes 5).

-- === outer INSERT #t EXEC(@sql): the dynamic frame OWNS the stage (fact 36a) ===
CREATE OR ALTER PROCEDURE dbo.p42_outer_exec @Seed int AS
BEGIN
    SET NOCOUNT ON;
    CREATE TABLE #t (v int);
    DECLARE @sql nvarchar(max) =
        N'SELECT ' + CONVERT(nvarchar(20), @Seed) + N' AS v; ' +
        N'SELECT ' + CONVERT(nvarchar(20), @Seed * 4) + N' AS v; ' +
        N'SELECT 500 AS v;';
    INSERT INTO #t (v) EXEC(@sql);          -- dynamic OWNER: SELECTs redirect into the stage
    SELECT v FROM #t ORDER BY v;            -- {5, 20, 500}
END
GO

-- === outer INSERT #t EXEC sp_executesql @sql (with @params) — dynamic owner (fact 36c) ===
CREATE OR ALTER PROCEDURE dbo.p42_outer_spexec @Seed int AS
BEGIN
    SET NOCOUNT ON;
    CREATE TABLE #t (v int);
    DECLARE @sql nvarchar(max) = N'SELECT @s AS v; SELECT @s * 7 AS v;';
    INSERT INTO #t (v) EXEC sp_executesql @sql, N'@s int', @s = @Seed;
    SELECT v FROM #t ORDER BY v;            -- {5, 35}
END
GO

-- === mid-stream fault in the dynamic source → target EMPTY (buffer-then-materialize, fact 36b) ===
-- The dynamic frame runs `INSERT #stage SELECT 5` then `INSERT #stage SELECT 1/0` → 8134 faults,
-- routes to the caller TRY, the dynamic frame abnormal-pops and DISCARDS the stage (I7), #t empty.
CREATE OR ALTER PROCEDURE dbo.p42_fault @Seed int AS
BEGIN
    SET NOCOUNT ON;
    CREATE TABLE #t (v int);
    DECLARE @sql nvarchar(max) = N'SELECT 5 AS v; SELECT 1 / 0 AS v; SELECT 500 AS v;';
    BEGIN TRY
        INSERT INTO #t (v) EXEC(@sql);
    END TRY
    BEGIN CATCH
        SELECT ERROR_NUMBER() AS caught;   -- 8134
    END CATCH
    SELECT COUNT(*) AS n FROM #t;          -- 0 (stage discarded on the abnormal pop)
END
GO

-- === nested DYNAMIC child inside a capture PROPAGATES (fact 36d) ===
-- mid is the capture owner; its EXEC(@sql) child inherits mid's stage and redirects into it.
CREATE OR ALTER PROCEDURE dbo.p42_nd_mid @Seed int AS
BEGIN
    SET NOCOUNT ON;
    SELECT @Seed AS v;                                  -- 5 (owner frame)
    EXEC(N'SELECT 100 AS v; SELECT 200 AS v;');         -- dynamic child → step-into PROPAGATES
    SELECT @Seed * 4 AS v;                              -- 20
END
GO
CREATE OR ALTER PROCEDURE dbo.p42_nested_dyn @Seed int AS
BEGIN
    SET NOCOUNT ON;
    CREATE TABLE #t (v int);
    INSERT INTO #t (v) EXEC dbo.p42_nd_mid @Seed = @Seed;
    SELECT v FROM #t ORDER BY v;            -- {5, 20, 100, 200}
END
GO

-- === cross-frame STREAM ORDER through a dynamic child into an IDENTITY target (C28) ===
-- ORDER BY id observes the shared stage's seq order = stream order across the dynamic boundary.
CREATE OR ALTER PROCEDURE dbo.p42_id_mid @Seed int AS
BEGIN
    SET NOCOUNT ON;
    SELECT 'mid-1' AS label;                            -- captured 1st (owner)
    EXEC(N'SELECT ''dyn-a'' AS label; SELECT ''dyn-b'' AS label;');   -- 2nd, 3rd (dynamic child)
    SELECT 'mid-2' AS label;                            -- 4th
END
GO
CREATE OR ALTER PROCEDURE dbo.p42_ident @Seed int AS
BEGIN
    SET NOCOUNT ON;
    CREATE TABLE #t (id int IDENTITY(1, 1), label varchar(20));
    INSERT INTO #t (label) EXEC dbo.p42_id_mid @Seed = @Seed;
    SELECT id, label FROM #t ORDER BY id;   -- {(1,mid-1),(2,dyn-a),(3,dyn-b),(4,mid-2)}
END
GO

-- === nested INSERT…EXEC inside a dynamic body → refused → step-over → native msg 8164 (fact 36e) ===
-- The scanner walks the parsed dynamic body, sees `INSERT @x EXEC proc`, refuses step-into → the outer
-- INSERT…EXEC(@sql) steps OVER, where native raises its own 8164 (caught by the caller TRY).
CREATE OR ALTER PROCEDURE dbo.p42_nie_inner @Seed int AS
BEGIN SET NOCOUNT ON; SELECT @Seed * 9 AS v; END
GO
CREATE OR ALTER PROCEDURE dbo.p42_nie_dyn @Seed int AS
BEGIN
    SET NOCOUNT ON;
    CREATE TABLE #t (v int);
    DECLARE @sql nvarchar(max) =
        N'DECLARE @x TABLE (v int); INSERT INTO @x EXEC dbo.p42_nie_inner @Seed = ' +
        CONVERT(nvarchar(20), @Seed) + N'; SELECT v FROM @x;';
    BEGIN TRY
        INSERT INTO #t (v) EXEC(@sql);
    END TRY
    BEGIN CATCH
        SELECT ERROR_NUMBER() AS caught;   -- 8164
    END CATCH
    SELECT COUNT(*) AS n FROM #t;          -- 0
END
GO

-- === unsafe dynamic body (CTE-headed result SELECT) → refused → transparent step-over ===
-- The scanner sees a CTE-headed result SELECT in the dynamic text (cannot be wrapped as
-- INSERT … <SELECT>, msg 156), refuses step-into → the dynamic source steps OVER, where native
-- captures it as one batch. Into == Over == native; only the ability to step in is lost.
CREATE OR ALTER PROCEDURE dbo.p42_cte_dyn @Seed int AS
BEGIN
    SET NOCOUNT ON;
    CREATE TABLE #t (v int);
    DECLARE @sql nvarchar(max) =
        N'SELECT ' + CONVERT(nvarchar(20), @Seed) + N' AS v; ' +
        N'WITH cte AS (SELECT 6 AS v UNION ALL SELECT 7) SELECT v FROM cte;';
    INSERT INTO #t (v) EXEC(@sql);
    SELECT v FROM #t ORDER BY v;            -- {5, 6, 7}
END
GO

-- === a nested sp_executesql child (not EXEC(@sql)) inside a capture PROPAGATES (fact 36f) ===
CREATE OR ALTER PROCEDURE dbo.p42_spchild_mid @Seed int AS
BEGIN
    SET NOCOUNT ON;
    SELECT @Seed AS v;                                       -- 5 (owner)
    EXEC sp_executesql N'SELECT 10 AS v; SELECT 11 AS v;';   -- sp_executesql child → PROPAGATES
    SELECT @Seed * 4 AS v;                                   -- 20
END
GO
CREATE OR ALTER PROCEDURE dbo.p42_sp_child @Seed int AS
BEGIN
    SET NOCOUNT ON;
    CREATE TABLE #t (v int);
    INSERT INTO #t (v) EXEC dbo.p42_spchild_mid @Seed = @Seed;
    SELECT v FROM #t ORDER BY v;            -- {5, 10, 11, 20}
END
GO

-- === a dynamic OWNER whose body has a dynamic CHILD (chain) — stream order via IDENTITY ===
-- Stepping into the outer EXEC(@sql) owner, then into the inner EXEC(N'…') child: the child inherits
-- the owner's stage and its rows land between the owner's, in stream order (1, 2, 3, 4).
CREATE OR ALTER PROCEDURE dbo.p42_dyn_in_dyn @Seed int AS
BEGIN
    SET NOCOUNT ON;
    CREATE TABLE #t (id int IDENTITY(1, 1), v int);
    DECLARE @sql nvarchar(max) =
        N'SELECT 1 AS v; EXEC(N''SELECT 2 AS v; SELECT 3 AS v;''); SELECT 4 AS v;';
    INSERT INTO #t (v) EXEC(@sql);
    SELECT id, v FROM #t ORDER BY id;       -- {(1,1),(2,2),(3,3),(4,4)}
END
GO

-- === explicit column list on a dynamic owner (the targetColumnListSql path) ===
CREATE OR ALTER PROCEDURE dbo.p42_collist @Seed int AS
BEGIN
    SET NOCOUNT ON;
    CREATE TABLE #t (a int, b int);
    DECLARE @sql nvarchar(max) = N'SELECT 5, 50; SELECT 6, 60;';
    INSERT INTO #t (a, b) EXEC(@sql);       -- explicit (a, b) list threaded to the stage
    SELECT a, b FROM #t ORDER BY a;         -- {(5,50),(6,60)}
END
GO
