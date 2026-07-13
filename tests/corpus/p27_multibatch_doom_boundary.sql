-- DESIGN §5.4 / §10.4 (A35) / §20.4 corpus fixture: p27_multibatch_doom_boundary
-- (multi-batch GO, script mode). Proves the §8.1 GO-boundary force-rollback (Appendix C
-- fact 22 at the separator; fact 32b): a batch leaves its transaction DOOMED
-- (XACT_STATE() = -1) when it reaches the GO seam; the engine force-rolls it back there,
-- so the NEXT batch reads @@TRANCOUNT = 0 and a #temp created inside the doomed
-- transaction is GONE. #temp-only (no permanent writes) so the native sqlcmd-default
-- oracle needs no outer wrapper; deterministic. Native (continue-past-error) == debugger.
SET NOCOUNT ON;
SET XACT_ABORT ON;
GO
-- batch 2: open a transaction, create a #temp INSIDE it, then DOOM it via 1/0. Under
-- XACT_ABORT ON the divide-by-zero (8134) dooms the transaction. The debuggee TRY/CATCH
-- catches it (so the batch runs to its natural end) and, crucially, does NOT roll back --
-- so the transaction is STILL doomed at the GO separator, where fact 22 must force-roll it.
-- (Facts 5/15/22: a doom raised in TRY streams the CATCH normally, then the batch end
-- raises 3998 and rolls the transaction back.)
BEGIN TRANSACTION;
CREATE TABLE #doomed (n int);
INSERT INTO #doomed (n) VALUES (1);
BEGIN TRY
    SELECT 1 / 0 AS boom;
END TRY
BEGIN CATCH
    SELECT ERROR_NUMBER() AS caught_number;
END CATCH;
GO
-- batch 3: the proof. The doomed transaction could NOT cross GO (fact 22), so @@TRANCOUNT
-- is 0 and #doomed was destroyed by the forced rollback (native = debugger).
SELECT
    @@TRANCOUNT AS trancount_after,
    CASE WHEN OBJECT_ID('tempdb..#doomed') IS NULL THEN 1 ELSE 0 END AS doomed_temp_gone;
GO
