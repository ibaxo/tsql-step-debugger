-- docs/DESIGN.md §20.4 corpus fixture: p32_tvp_step_into_refusals
-- A62 F1–F4 (independent Fable review, 2026-07-16): the step-into TVP path must fall through to a
-- FAITHFUL step-over for every shape the engine itself rejects at the call — NOT crash the session
-- (F1/F2) and NOT run a callee the engine compile-refuses (F3/F4). Each edge below is stepped OVER,
-- so the engine raises its own error, which the proc's own TRY/CATCH captures exactly as it does
-- natively. Native truth (probed live): OkCount=3, F4=206, F1=206, F2a=352, F2b=206, F3a=10700,
-- F3b=10700. Before the fix the debugger died on F1/F2 (unhandled 137/invalid-column) and ran the
-- refused write on F3/hid the clash on F4 — so the debugged row diverged from native.
--
--   F4  — actual is a table type with the SAME columns as the formal but a DIFFERENT type → 206.
--   F1  — actual is a DIFFERENT table type with DIFFERENT columns → 206 (a session-killer pre-fix).
--   F2a — dynamic params-definition omits READONLY on the table-type formal → 352 (session-killer).
--   F2b — a table-type variable passed to a SCALAR formal → 206 (session-killer pre-fix).
--   F3a — a dynamic body writes its READONLY TVP through an ALIAS (UPDATE x … FROM @r x) → 10700.
--   F3b — a dynamic body writes its READONLY TVP through OUTPUT … INTO → 10700.
-- The happy path (EXEC dbo.p32_ok @Rows = @a) still steps IN and returns COUNT = 3 (control).

IF TYPE_ID('dbo.p32_A') IS NULL CREATE TYPE dbo.p32_A AS TABLE (v int);   -- the caller's variable type
IF TYPE_ID('dbo.p32_B') IS NULL CREATE TYPE dbo.p32_B AS TABLE (v int);   -- same columns, DIFFERENT type (F4)
IF TYPE_ID('dbo.p32_C') IS NULL CREATE TYPE dbo.p32_C AS TABLE (w int);   -- different columns (F1)
GO
CREATE OR ALTER PROCEDURE dbo.p32_ok @Rows dbo.p32_A READONLY AS
BEGIN SET NOCOUNT ON; RETURN (SELECT COUNT(*) FROM @Rows); END           -- happy path: stepped INTO
GO
CREATE OR ALTER PROCEDURE dbo.p32_scalar @x int AS
BEGIN SET NOCOUNT ON; RETURN ISNULL(@x, -1); END                         -- a scalar formal (F2b)
GO
CREATE OR ALTER PROCEDURE dbo.p32_tvp_step_into_refusals
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @a dbo.p32_A;
    INSERT INTO @a (v) VALUES (10), (20), (30);

    DECLARE @ok int = -1,
            @e_f4 int = 0, @e_f1 int = 0, @e_f2a int = 0, @e_f2b int = 0, @e_f3a int = 0, @e_f3b int = 0;

    -- control: correct type, READONLY TVP formal — stepped INTO, callee sees all 3 rows
    EXEC @ok = dbo.p32_ok @Rows = @a;

    -- F4: same-shape but different table type → native operand type clash 206
    BEGIN TRY
        DECLARE @o4 int;
        EXEC sys.sp_executesql N'SELECT @c = COUNT(*) FROM @r;',
             N'@r dbo.p32_B READONLY, @c int OUTPUT', @r = @a, @c = @o4 OUTPUT;
    END TRY BEGIN CATCH SET @e_f4 = ERROR_NUMBER(); END CATCH;

    -- F1: different table type, different columns → 206
    BEGIN TRY
        DECLARE @o1 int;
        EXEC sys.sp_executesql N'SELECT @c = COUNT(*) FROM @r;',
             N'@r dbo.p32_C READONLY, @c int OUTPUT', @r = @a, @c = @o1 OUTPUT;
    END TRY BEGIN CATCH SET @e_f1 = ERROR_NUMBER(); END CATCH;

    -- F2a: table-type formal missing READONLY → 352
    BEGIN TRY
        DECLARE @o2 int;
        EXEC sys.sp_executesql N'SELECT @c = COUNT(*) FROM @r;',
             N'@r dbo.p32_A, @c int OUTPUT', @r = @a, @c = @o2 OUTPUT;
    END TRY BEGIN CATCH SET @e_f2a = ERROR_NUMBER(); END CATCH;

    -- F2b: a table-type variable handed to a scalar formal → 206
    BEGIN TRY
        DECLARE @rc int;
        EXEC @rc = dbo.p32_scalar @x = @a;
    END TRY BEGIN CATCH SET @e_f2b = ERROR_NUMBER(); END CATCH;

    -- F3a: dynamic body writes the READONLY TVP through an alias → compile error 10700
    BEGIN TRY
        EXEC sys.sp_executesql N'UPDATE x SET v = 99 FROM @r AS x;', N'@r dbo.p32_A READONLY', @r = @a;
    END TRY BEGIN CATCH SET @e_f3a = ERROR_NUMBER(); END CATCH;

    -- F3b: dynamic body writes the READONLY TVP through OUTPUT … INTO → 10700
    BEGIN TRY
        EXEC sys.sp_executesql
             N'DECLARE @tmp TABLE(v int); INSERT @tmp VALUES(1); DELETE @tmp OUTPUT deleted.v INTO @r;',
             N'@r dbo.p32_A READONLY', @r = @a;
    END TRY BEGIN CATCH SET @e_f3b = ERROR_NUMBER(); END CATCH;

    SELECT @ok AS OkCount, @e_f4 AS F4, @e_f1 AS F1, @e_f2a AS F2a, @e_f2b AS F2b, @e_f3a AS F3a, @e_f3b AS F3b;
END
