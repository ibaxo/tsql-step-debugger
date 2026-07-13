-- DESIGN §5.4 / §20.4 corpus fixture: p26_multibatch_go_script (multi-batch GO, script mode).
-- Proves (fact 32a): local-variable scope RESETS per GO; #temp PERSISTS across GO; a
-- compile-class failed batch ABORTS but the client CONTINUES to the next batch. #temp-only
-- (no permanent writes) so the native oracle needs no wrapper; deterministic.
SET NOCOUNT ON;
CREATE TABLE #acc (label nvarchar(20), n int);
DECLARE @acc int = 100;                          -- batch-1 scope
INSERT INTO #acc (label, n) VALUES (N'b1', @acc);
GO
-- batch 2: @acc from batch 1 is OUT OF SCOPE; re-DECLARE it as a different type (legal).
-- #acc (the #temp) persists and gains a row.
DECLARE @acc nvarchar(10) = N'two';
INSERT INTO #acc (label, n) VALUES (@acc, 2);
GO
-- batch 3: references an UNDECLARED @nope -> compile error 137 aborts THIS batch only
-- (non-dooming, fact 32a); #acc untouched; the client continues to batch 4.
INSERT INTO #acc (label, n) VALUES (N'b3', @nope);
GO
-- batch 4: proves execution continued past batch 3 and #acc survived every GO boundary.
SELECT label, n FROM #acc ORDER BY label;        -- projection: exactly (b1,100),(two,2)
GO
