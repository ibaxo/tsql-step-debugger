-- DESIGN.md §20.4 corpus fixture: p11_dynamic_sql_blackbox
-- M7 D3 §3.1 (docs/archive/reviews/m7-hardening-design-notes-fable.md): C10 -- dynamic SQL is
-- an atomic black box. step-into REFUSES all dynamic SQL (falls back to step-over,
-- C10's own console note), so all three fidelity passes are trajectory-identical to
-- pass 1; boost also refuses every EXEC member (§14/A21 whitelist excludes EXEC of any
-- kind) -- no exemptions anywhere. The one single-line `IF ... SET ...;` below IS boost
-- eligible by node kind (If) but refuses with reason "line-ambiguity" (its own predicate
-- and its single THEN statement share one physical source line -- §14/A21, the same
-- shape the M6 BUILD lane verified) -- a genuine, asserted boost.refuse, not a fixture
-- with structurally nothing to refuse.
--
-- Deliberately AVOIDED shape (C10r rider, §21/C10): none of these dynamic batches reads
-- @@ROWCOUNT/@@ERROR before its own first resetting statement -- every dynamic batch's
-- FIRST statement is itself the resetting statement, so there is no debugger-preamble-
-- vs-caller-statement ambiguity to exercise either way.
IF OBJECT_ID('dbo.p11_id') IS NOT NULL DROP TABLE dbo.p11_id;
IF OBJECT_ID('dbo.p11_t') IS NOT NULL DROP TABLE dbo.p11_t;
CREATE TABLE dbo.p11_t (id int IDENTITY(1,1) PRIMARY KEY, val int);
CREATE TABLE dbo.p11_id (id int IDENTITY(500,1) PRIMARY KEY, tag int);
GO

CREATE OR ALTER PROCEDURE dbo.p11_dynamic_sql_blackbox
    @Seed int,
    @Out int OUTPUT
AS
BEGIN
    SET NOCOUNT ON;

    -- 1. Plain black-box cell: EXEC(@sql) DML on the (manifest-observed-shaped) table.
    DECLARE @sql nvarchar(max) = N'INSERT INTO dbo.p11_t (val) VALUES (' +
        CAST(@Seed AS nvarchar(20)) + N'), (' + CAST(@Seed + 1 AS nvarchar(20)) + N');';
    EXEC (@sql);

    -- 2. sp_executesql: typed parameter list + OUTPUT param (@r = SUM) -> @Out --
    -- native OUTPUT plumbing through a stepped-over statement.
    DECLARE @r int;
    EXEC sp_executesql
        N'SELECT @r = SUM(val) FROM dbo.p11_t;',
        N'@r int OUTPUT',
        @r = @r OUTPUT;
    SET @Out = @r;

    -- 3. #temp scoping: the child batch's own #temp dies at child-batch exit --
    -- natively @TmpGone = 1; the debugger executes the SAME child batch text, so the
    -- composed-batch model must not accidentally extend its lifetime.
    DECLARE @TmpGone int = 0;
    EXEC ('CREATE TABLE #p11_tmp (v int); INSERT INTO #p11_tmp (v) VALUES (1);');
    IF OBJECT_ID('tempdb..#p11_tmp') IS NULL SET @TmpGone = 1;

    -- 4. Scope isolation: a dynamic identity insert, then a CALLER SCOPE_IDENTITY()
    -- read -- natively unchanged by the child scope (fact 26e: EXEC/sp_executesql
    -- children are true separate scopes); under the debugger a stepped-over EXEC is a
    -- true module scope in BOTH worlds (D1 interactions checklist item 3 -- neither a
    -- poisoning nor a clearing event), so the caller's own chain (never touched by any
    -- caller-scope identity insert) stays NULL on both sides.
    EXEC ('INSERT INTO dbo.p11_id (tag) VALUES (99);');
    DECLARE @Si int = SCOPE_IDENTITY();

    SELECT @Out AS Out, @TmpGone AS TmpGone, @Si AS Si;
END
GO
