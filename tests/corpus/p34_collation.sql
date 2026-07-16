-- docs/DESIGN.md §20.4 corpus fixture: p34_collation
-- C14 (collation-aware temp DDL). A table variable's char columns inherit the DATABASE's default
-- collation, but the debugger realizes the variable as a #temp — whose char columns would default
-- to TEMPDB's collation. When the two differ, an un-COLLATE'd column silently changes behavior.
-- This fixture MUST be deployed to a case-sensitive database (Latin1_General_CS_AS) so the gap is
-- observable (tempdb here is case-insensitive). Native truth (probed live in that DB):
-- DefaultColMatch=0, ExplicitColMatch=0.
--   DefaultColMatch  — an un-COLLATE'd column: native uses the DB's CS collation, so 'a' <> 'A' → 0.
--                      Pre-fix the debugger's #temp realization was tempdb-CI → 'a' = 'A' → 1 (the bug).
--   ExplicitColMatch — a column with an explicit COLLATE (CS): the byte-exact source slice already
--                      preserved it, so this was 0 before and after — it guards against a regression.

CREATE OR ALTER PROCEDURE dbo.p34_collation
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @t TABLE (
        c   varchar(10),                                   -- un-COLLATE'd → inherits the DB collation (CS)
        tag varchar(10) COLLATE Latin1_General_CI_AI);     -- explicit COLLATE that DIFFERS from the DB default (CI)

    INSERT INTO @t (c, tag) VALUES ('a', 'x');

    DECLARE @DefaultColMatch  int = (SELECT COUNT(*) FROM @t WHERE c   = 'A');   -- 0: un-COLLATE'd col is CS ('a' <> 'A')
    DECLARE @ExplicitColMatch int = (SELECT COUNT(*) FROM @t WHERE tag = 'X');   -- 1: explicit CI col ('x' = 'X'), NOT overwritten by the DB's CS default

    SELECT @DefaultColMatch AS DefaultColMatch, @ExplicitColMatch AS ExplicitColMatch;
END
