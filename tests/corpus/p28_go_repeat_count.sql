-- p28 — GO N repeat count (DESIGN.md §5.4 / A43). Native oracle = sqlcmd's `GO N`:
-- run each batch its count times on one connection, catch-and-continue per iteration.
--
-- Exercises, in one file, the GO N mechanics: repeat (a GO N batch yields N result sets),
-- scope RESET per iteration (a fresh DECLARE runs cleanly every time), and #temp
-- PERSISTENCE across every iteration AND GO boundary (one session counter grows
-- monotonically: 2,3,4 through GO 3, then 5,6 through GO 2, then 6 at the end), plus GO 0
-- (the batch is skipped entirely).
--
-- Deliberately ERROR-FREE: a batch-aborting error inside a repeated iteration would doom
-- the debugger's safety transaction (the C22/C23 instrumentation-doom family — an error
-- native survives in autocommit becomes uncommittable inside the wrapping transaction),
-- which is orthogonal to GO N and which fixtures avoid (docs/archive/reviews/go-n-repeat-count-opus.md §6).

-- Batch 1 (plain GO): a session #temp counter, seeded, so the transaction is healthy.
CREATE TABLE #hits (marker varchar(20));
INSERT #hits (marker) VALUES ('b1');
GO

-- Batch 2 (GO 3): fresh scope each iteration (@who re-DECLAREd), #hits accumulates across
-- iterations, so the running count is 2, then 3, then 4.
DECLARE @who varchar(20) = 'rep';
INSERT #hits (marker) VALUES (@who);
SELECT 'rep' AS label, COUNT(*) AS n FROM #hits;
GO 3

-- Batch 3 (GO 0): skipped entirely — 'skipped' must NOT appear in the projection.
SELECT 'skipped' AS label, -1 AS n;
GO 0

-- Batch 4 (GO 2): a second repeated batch — proves repeat + persistence again, error-free.
-- The counter continues from batch 2 across the GO 0: 5, then 6.
INSERT #hits (marker) VALUES ('b4');
SELECT 'b4' AS label, COUNT(*) AS n FROM #hits;
GO 2

-- Batch 5 (plain GO): the final count of the persistent session #temp (6).
SELECT 'final' AS label, COUNT(*) AS n FROM #hits;
GO
