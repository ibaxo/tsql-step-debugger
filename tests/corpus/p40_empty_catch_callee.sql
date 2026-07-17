-- docs/DESIGN.md §20.4 corpus fixture: p40_empty_catch_callee
-- Pre-existing bug found while pinning A65/F2 (2026-07-17): a fault routed to an EMPTY CATCH that is
-- the LAST construct of a CALLEE body (depth >= 2) left the cursor completed via routing (RouteError
-- runs through END CATCH and exhausts the body) rather than via a settled advance, so frame settlement
-- never fired -> the NEXT step's step-into `cursor.Peek()` threw "Cursor is completed; nothing to peek."
-- Frame 0 dodged it (a depth-1 completed cursor makes the session IsCompleted and the run just ends).
--
-- Native semantics (verified live): the empty CATCH swallows the error and the callee RETURNS NORMALLY
-- -- OUTPUT params are copied back, the caller's @@ERROR after the EXEC is 0 (the EXEC succeeded), and
-- the caller continues past the EXEC. The debugger must therefore SETTLE the route-completed frame as a
-- COMPLETED pop (copy-back + @rc), matching native byte-for-byte. These are fidelity cases (Over == Into
-- == native), not a divergence like the F2 doom terminal.

-- Case 1 (the bug): the empty CATCH is the LAST construct of the callee body. @Result is set BEFORE the
-- fault and must survive to copy-back; the caller continues to its post-EXEC SELECT. With @Seed=5 the
-- caller emits {50, 0} (ResultOut = 5*10 copied back, ErrAfter = 0 -- a clean EXEC).
CREATE OR ALTER PROCEDURE dbo.p40_ec_callee
    @Seed int,
    @Result int OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @z int;
    SET @Result = @Seed * 10;              -- set BEFORE the fault -- must survive to OUTPUT copy-back
    BEGIN TRY
        SET @z = @Seed / 0;                -- divide-by-zero (8134, catchable, non-dooming) -> routed to CATCH
    END TRY
    BEGIN CATCH
    END CATCH                              -- EMPTY, and the LAST construct of the body (the bug trigger)
END
GO
CREATE OR ALTER PROCEDURE dbo.p40_empty_catch
    @Seed int
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @r int = -1;
    EXEC dbo.p40_ec_callee @Seed = @Seed, @Result = @r OUTPUT;
    SELECT @r AS ResultOut, @@ERROR AS ErrAfter, @@ROWCOUNT AS RcAfter;   -- {50, 0, 0}: copy-back + clean EXEC (never reached pre-fix)
END
GO

-- Case 2 (regression guard): the empty CATCH is NOT the last construct -- a statement follows it in the
-- same frame. Routing lands the continuation on that statement (this path already worked; guard it). With
-- @Seed=5 the caller emits {50, 0}.
CREATE OR ALTER PROCEDURE dbo.p40_ec_notlast_callee
    @Seed int,
    @Result int OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @z int;
    BEGIN TRY
        SET @z = @Seed / 0;                -- routed to the empty CATCH
    END TRY
    BEGIN CATCH
    END CATCH
    SET @Result = @Seed * 10;              -- a statement AFTER the empty CATCH -- continuation lands here
END
GO
CREATE OR ALTER PROCEDURE dbo.p40_empty_catch_notlast
    @Seed int
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @r int = -1;
    EXEC dbo.p40_ec_notlast_callee @Seed = @Seed, @Result = @r OUTPUT;
    SELECT @r AS ResultOut, @@ERROR AS ErrAfter, @@ROWCOUNT AS RcAfter;   -- {50, 0, 0}
END
GO

-- Case 3 (cascade): the EXEC is the caller's LAST statement, so the callee's empty-CATCH completion pops
-- the callee AND then exhausts the caller -- the completed-pop cascade must not crash either. The callee
-- emits a captured-free result before faulting so there is observable output. With @Seed=5 -> {5}.
CREATE OR ALTER PROCEDURE dbo.p40_ec_cascade_callee
    @Seed int
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @z int;
    SELECT @Seed AS Emitted;               -- streamed before the fault
    BEGIN TRY
        SET @z = @Seed / 0;                -- routed to the empty CATCH
    END TRY
    BEGIN CATCH
    END CATCH                              -- empty, last construct
END
GO
CREATE OR ALTER PROCEDURE dbo.p40_empty_catch_cascade
    @Seed int
AS
BEGIN
    SET NOCOUNT ON;
    EXEC dbo.p40_ec_cascade_callee @Seed = @Seed;   -- last statement: callee completes -> caller completes (cascade)
END
GO

-- Case 4 (doomed sub-case): SET XACT_ABORT ON makes the divide-by-zero DOOM the transaction, yet the
-- empty CATCH still swallows it and the callee returns to a DOOMED caller. The fix routes this through
-- the same settlement (a doomed COMPLETED pop: bookkeeping + SET restores, NO copy-back). This is NOT a
-- clean fidelity case -- under the debugger the §16 safety transaction is what gets doomed (native run
-- standalone has no open transaction), so it is asserted on the debugger's OWN behavior (no crash; the
-- callee's pre-fault output survives) rather than by native comparison. It exists to pin that the
-- empty-CATCH settlement covers the doomed pop flavor too (my earlier concern was the crash, not doom).
CREATE OR ALTER PROCEDURE dbo.p40_ec_doomed_callee
    @Seed int
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;                     -- an error now DOOMS the transaction
    DECLARE @z int;
    SELECT @Seed AS Emitted;               -- streamed before the fault
    BEGIN TRY
        SET @z = @Seed / 0;                -- divide-by-zero -> DOOMED under XACT_ABORT ON
    END TRY
    BEGIN CATCH
    END CATCH                              -- empty, last construct; the callee returns doomed
END
GO
CREATE OR ALTER PROCEDURE dbo.p40_empty_catch_doomed
    @Seed int
AS
BEGIN
    SET NOCOUNT ON;
    EXEC dbo.p40_ec_doomed_callee @Seed = @Seed;   -- callee dooms + returns via empty CATCH (last statement)
END
GO

-- Case 5 (F1 guard — @@ROWCOUNT zeroing): the callee affects rows (INSERT 3) BEFORE faulting into the
-- empty-last CATCH. Native zeroes @@ROWCOUNT on the empty-CATCH transit (fact 18 + probe X4), so the
-- caller reads RcAfter = 0 -- NOT the stale 3 that the pre-re-review fix carried across the pop. With
-- @Seed=5 -> {0} (the caller reads only @@ROWCOUNT). Pins ObserveHandledCatchReturn's rc reset.
CREATE OR ALTER PROCEDURE dbo.p40_ec_rowcount_callee
    @Seed int
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @t TABLE (x int);
    DECLARE @z int;
    INSERT INTO @t (x) VALUES (1), (2), (3);   -- @@ROWCOUNT = 3 right before the fault
    BEGIN TRY
        SET @z = @Seed / 0;                    -- routed to the empty CATCH
    END TRY
    BEGIN CATCH
    END CATCH                                  -- empty, last -> @@ROWCOUNT zeroed on the transit
END
GO
CREATE OR ALTER PROCEDURE dbo.p40_empty_catch_rowcount
    @Seed int
AS
BEGIN
    SET NOCOUNT ON;
    EXEC dbo.p40_ec_rowcount_callee @Seed = @Seed;
    SELECT @@ROWCOUNT AS RcAfter;              -- 0 (native): the empty-CATCH transit zeroed it
END
GO

-- Case 6 (F1 guard — @@ERROR zeroing across a pre-TRY statement-level error): a RAISERROR BEFORE the TRY
-- leaves @@ERROR = 50000 (unhandled statement-level continuation, fact 21); the empty-last CATCH then
-- zeroes it (probe X1/X3). The caller reads ErrAfter = 0 -- NOT the stale 50000 the pre-re-review fix
-- carried. With @Seed=5 -> {0}.
CREATE OR ALTER PROCEDURE dbo.p40_ec_preerr_callee
    @Seed int
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @z int;
    RAISERROR('pre-TRY boom', 16, 1);          -- 50000, unhandled -> statement-level continuation (@@ERROR = 50000)
    BEGIN TRY
        SET @z = @Seed / 0;                    -- routed to the empty CATCH
    END TRY
    BEGIN CATCH
    END CATCH                                  -- empty, last -> @@ERROR zeroed on the transit
END
GO
CREATE OR ALTER PROCEDURE dbo.p40_empty_catch_preerr
    @Seed int
AS
BEGIN
    SET NOCOUNT ON;
    EXEC dbo.p40_ec_preerr_callee @Seed = @Seed;
    SELECT @@ERROR AS ErrAfter;                -- 0 (native): the empty-CATCH transit zeroed it
END
GO

-- Case 7 (X6 guard — bare THROW into an empty OUTER CATCH): the inner CATCH re-raises with THROW; the
-- outer CATCH is empty and last, so the rethrow is swallowed and the callee returns. Native caller
-- @@ERROR after the EXEC is 0 (probe X6). Exercises the Rethrow -> PerformRouteAsync -> empty-CATCH-transit
-- path (the rethrow's context must be reconciled away, not leaked). With @Seed=5 -> {0}.
CREATE OR ALTER PROCEDURE dbo.p40_ec_rethrow_callee
    @Seed int
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @z int;
    BEGIN TRY
        BEGIN TRY
            SET @z = @Seed / 0;                -- 8134
        END TRY
        BEGIN CATCH
            THROW;                             -- re-raise into the outer TRY
        END CATCH
    END TRY
    BEGIN CATCH
    END CATCH                                  -- empty, last -> swallows the rethrow
END
GO
CREATE OR ALTER PROCEDURE dbo.p40_empty_catch_rethrow
    @Seed int
AS
BEGIN
    SET NOCOUNT ON;
    EXEC dbo.p40_ec_rethrow_callee @Seed = @Seed;
    SELECT @@ERROR AS ErrAfter;                -- 0 (native)
END
GO

-- Case 8 (Finding 2a guard — continuation must NOT corrupt a caller's LIVE outer CATCH): the caller is
-- INSIDE its own CATCH (context = its 50000/state-7 error) when it EXECs a callee that swallows its OWN
-- (different) 8134 via an empty NOT-last CATCH (the continuation sub-shape). The caller's CATCH must still
-- read ITS error (50000/7/'outer boom') afterward -- the pre-fix fall-through over-trimmed and replaced
-- the caller's live context with the callee's swallowed one. Fidelity: native is the oracle.
CREATE OR ALTER PROCEDURE dbo.p40_ec_swallow_callee
    @Seed int
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @z int;
    BEGIN TRY
        SET @z = @Seed / 0;                    -- 8134, swallowed by the empty CATCH
    END TRY
    BEGIN CATCH
    END CATCH
    SELECT @Seed AS CalleeEmitted;             -- a statement AFTER the empty CATCH (continuation), then return
END
GO
CREATE OR ALTER PROCEDURE dbo.p40_outer_live_catch
    @Seed int
AS
BEGIN
    SET NOCOUNT ON;
    BEGIN TRY
        RAISERROR('outer boom', 16, 7);        -- 50000, state 7 -- the caller's OWN error
    END TRY
    BEGIN CATCH
        EXEC dbo.p40_ec_swallow_callee @Seed = @Seed;   -- callee swallows its own 8134 mid-body (continuation)
        SELECT ERROR_NUMBER() AS CErrNum, ERROR_STATE() AS CErrState, ERROR_MESSAGE() AS CErrMsg;  -- must be the caller's 50000/7
    END CATCH
END
GO
