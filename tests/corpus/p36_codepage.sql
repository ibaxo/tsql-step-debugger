-- docs/DESIGN.md §20.4 corpus fixture: p36_codepage
-- C14 review follow-up (Fable F1). The state table and the table-variable realization both live in
-- tempdb; a database whose collation uses a DIFFERENT CODE PAGE than tempdb would transcode a
-- non-ASCII varchar on the per-step round trip. This fixture MUST run in a Cyrillic (CP1251)
-- database (tempdb here is CP1252), so a Cyrillic character survives ONLY if the tempdb column
-- carries the database collation. Native truth (probed live): ScalarCode=1103, TableVarCode=1103.
--   ScalarCode   — a plain char SCALAR variable round-trips through the §8.1 state table (F1). The
--                  value is written on the DECLARE/SET and read back on the next step; a CP1252
--                  column would return 63 ('?'). Assert UNICODE (the codepoint), not the glyph.
--   TableVarCode — a table-variable char column value round-trips through its #temp realization (C14).
-- NCHAR(1103) = U+044F CYRILLIC SMALL LETTER YA, used instead of a literal to avoid file-encoding
-- ambiguity; it implicitly converts to the database's CP1251 varchar.

CREATE OR ALTER PROCEDURE dbo.p36_codepage
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @s varchar(10) = NCHAR(1103);      -- stored under the database's CP1251
    SET @s = @s + '';                          -- a step: forces the value through the state table

    DECLARE @t TABLE (c varchar(10));
    INSERT INTO @t (c) VALUES (NCHAR(1103));

    SELECT UNICODE(@s) AS ScalarCode,
           (SELECT TOP 1 UNICODE(c) FROM @t) AS TableVarCode;
END
