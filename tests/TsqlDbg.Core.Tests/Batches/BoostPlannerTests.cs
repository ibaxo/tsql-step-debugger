using TsqlDbg.Core.Batches;
using TsqlDbg.Core.Interpreter;
using TsqlDbg.Core.Tests.Interpreter;
using Xunit;

namespace TsqlDbg.Core.Tests.Batches;

// DESIGN §14 (A21) eligibility — one case per refusal reason plus the allowed set,
// and the LOCKSTEP PIN tying BoostPlanner.AllowedMemberSubKinds to the ratified A21
// text (the D5 HasArmedTry/RouteError precedent: eligibility and spec must not be
// able to drift apart silently).
public class BoostPlannerTests
{
    private static BoostPlanResult Plan(
        string script, BoostSessionGate? gate = null, Func<StatementUnit, bool>? isBlocked = null,
        IReadOnlyCollection<string>? tableTypeVariables = null)
    {
        var (body, full) = ParseTestHelper.ParseBatch(script);
        var cursor = ExecutionCursor.Create(body, full);
        var node = cursor.Index.All.First(u => u.SubKind is SuSubKind.If or SuSubKind.While);
        return BoostPlanner.TryPlan(
            node, cursor.Index, gate ?? BoostSessionGate.PlainHealthy, isBlocked ?? (_ => false), tableTypeVariables);
    }

    // ------------------------------------------------------------------ allowed set

    [Fact]
    public void CleanLoop_WithTheWholeAllowedVocabulary_IsEligible()
    {
        var result = Plan("""
            DECLARE @i int, @v int;
            WHILE @i < 10
            BEGIN
                SET @i = @i + 1;
                INSERT dbo.T VALUES (@i);
                UPDATE dbo.T SET c = @i WHERE id = @i;
                DELETE dbo.T WHERE id < 0;
                SELECT @v = c FROM dbo.T WHERE id = @i;
                PRINT 'iteration';
                IF @i = 5
                BEGIN
                    RAISERROR('halfway', 10, 1);
                    CONTINUE;
                END
                IF @i = 9 BREAK;
                IF @v IS NULL
                    THROW 50001, 'missing', 1;
                GOTO bodyend;
                bodyend:
            END
            """);

        Assert.True(result.Eligible, result.Refusal?.Detail);
        var plan = result.Plan!;
        Assert.Equal(SuSubKind.While, plan.ControlNode.SubKind);
        Assert.NotEmpty(plan.Markers);
        Assert.NotEmpty(plan.MemberUnits);
        // The line map covers the loop predicate and every executable start line.
        Assert.Contains(plan.ControlNode.Span.StartLine, plan.LineMap.Keys);
    }

    [Fact]
    public void CursorFetchLoop_WithFetchStatusPredicate_IsEligible_TheP08Shape()
    {
        // @@FETCH_STATUS/@@TRANCOUNT are LIVE truth (§7.4 never rewrites them) —
        // cursor fetch loops boost cleanly; that is p08's whole point (A21).
        var result = Plan("""
            DECLARE @v int;
            DECLARE c CURSOR LOCAL FAST_FORWARD FOR SELECT 1 AS v;
            OPEN c;
            FETCH NEXT FROM c INTO @v;
            WHILE @@FETCH_STATUS = 0
            BEGIN
                INSERT dbo.T VALUES (@v);
                FETCH NEXT FROM c INTO @v;
            END
            CLOSE c;
            """);

        Assert.True(result.Eligible, result.Refusal?.Detail);
    }

    [Fact]
    public void IfNode_WithSingleStatementBranches_IsEligible_WithNoMarkers()
    {
        // Branch statements on their OWN lines — `IF p STMT;` on one line is a
        // genuine err_line ambiguity (predicate and statement share the line) and
        // refuses; see the line-ambiguity case below.
        var result = Plan("DECLARE @x int;\nIF @x = 1\n    SET @x = 2;\nELSE\n    SET @x = 3;");
        Assert.True(result.Eligible, result.Refusal?.Detail);
        Assert.Empty(result.Plan!.Markers);
    }

    [Fact]
    public void SingleLineIfWithExecutableBranch_IsTheAmbiguityCase_Refuses()
    {
        var result = Plan("DECLARE @x int;\nIF @x = 1 SET @x = 2;");
        Assert.False(result.Eligible);
        Assert.Equal("line-ambiguity", result.Refusal!.ReasonCode);
    }

    // ---------------------------------------------------------------- session gate

    [Theory]
    [InlineData(true, false, false, false, false, "session-doomed")]
    [InlineData(false, true, false, false, false, "session-detached")]
    [InlineData(false, false, true, false, false, "session-broken")]
    [InlineData(false, false, false, true, false, "error-context-active")]
    [InlineData(false, false, false, false, true, "chain-poisoned")]   // A26/D1: the R6 scope-identity chain is out of sync (§7.4/§14)
    public void NonPlainHealthySession_Refuses(bool doomed, bool detached, bool broken, bool contextActive, bool chainPoisoned, string reason)
    {
        var result = Plan(
            "DECLARE @i int;\nWHILE @i < 2\nBEGIN\n    SET @i = @i + 1;\nEND",
            new BoostSessionGate(doomed, detached, broken, contextActive, chainPoisoned));
        Assert.False(result.Eligible);
        Assert.Equal(reason, result.Refusal!.ReasonCode);
    }

    // ------------------------------------------------------------- member refusals

    [Theory]
    [InlineData("EXEC dbo.SomeProc;", "member:Execute")]
    [InlineData("RETURN;", "member:Return")]
    [InlineData("WAITFOR DELAY '00:00:01';", "member:WaitFor")]
    [InlineData("DECLARE @z int;", "member:Declare")]
    [InlineData("DECLARE @t TABLE (id int);", "member:Declare")]
    [InlineData("DECLARE c2 CURSOR FOR SELECT 1 AS v;", "member:CursorDeclare")]
    [InlineData("CREATE TABLE #tmp (id int);", "member:TempTableDdl")]
    [InlineData("DROP TABLE dbo.T;", "member:Other")]
    [InlineData("DEALLOCATE c0;", "cursor-deallocate")]
    [InlineData("BEGIN TRANSACTION;", "member:BeginTran")]
    [InlineData("COMMIT;", "member:Commit")]
    [InlineData("ROLLBACK;", "member:Rollback")]
    [InlineData("SAVE TRANSACTION sp1;", "member:SaveTran")]
    [InlineData("SET NOCOUNT ON;", "member:SetOption")]
    [InlineData("SELECT c INTO #snap FROM dbo.T;", "select-into")]
    [InlineData("CREATE TABLE dbo.Real (id int);", "member:General/CreateTableStatement")]
    public void RefusedMemberKinds_RefuseWithTheirReasonCode(string statement, string reason)
    {
        var result = Plan($"DECLARE @i int;\nDECLARE c0 CURSOR FOR SELECT 1 AS v;\nWHILE @i < 2\nBEGIN\n    {statement}\n    SET @i = @i + 1;\nEND");
        Assert.False(result.Eligible);
        Assert.Equal(reason, result.Refusal!.ReasonCode);
    }

    [Fact]
    public void TryCatch_AnywhereInTheSubtree_Refuses()
    {
        var result = Plan("""
            DECLARE @i int;
            WHILE @i < 2
            BEGIN
                BEGIN TRY
                    SET @i = @i + 1;
                END TRY
                BEGIN CATCH
                    SET @i = 99;
                END CATCH
            END
            """);
        Assert.False(result.Eligible);
        Assert.Equal("try-catch", result.Refusal!.ReasonCode);
    }

    // --------------------------------------------------------- structural refusals

    [Theory]
    [InlineData("SET @v = @@ROWCOUNT;", "@@ROWCOUNT")]
    [InlineData("SET @v = @@ERROR;", "@@ERROR")]
    [InlineData("SET @v = SCOPE_IDENTITY();", "SCOPE_IDENTITY()")]
    [InlineData("SET @v = ERROR_NUMBER();", "ERROR_NUMBER()")]
    [InlineData("PRINT ERROR_MESSAGE();", "ERROR_MESSAGE()")]
    public void IntrinsicReferences_AnywhereInTheSubtree_Refuse(string statement, string expectedName)
    {
        var result = Plan($"DECLARE @i int, @v int;\nWHILE @i < 2\nBEGIN\n    {statement}\n    SET @i = @i + 1;\nEND");
        Assert.False(result.Eligible);
        Assert.Equal("intrinsic-reference", result.Refusal!.ReasonCode);
        Assert.Contains(expectedName, result.Refusal.Detail);
    }

    [Fact]
    public void IntrinsicInsideAStringLiteral_IsStructurallySilent_Eligible()
    {
        // §7.4 R8-negative, mirrored: N'… @@ROWCOUNT …' is a StringLiteral node.
        var result = Plan("DECLARE @i int;\nWHILE @i < 2\nBEGIN\n    PRINT N'@@ROWCOUNT and ERROR_MESSAGE() in text';\n    SET @i = @i + 1;\nEND");
        Assert.True(result.Eligible, result.Refusal?.Detail);
    }

    [Fact]
    public void NextValueFor_Refuses()
    {
        var result = Plan("DECLARE @i int, @v int;\nWHILE @i < 2\nBEGIN\n    SET @v = NEXT VALUE FOR dbo.Seq;\n    SET @i = @i + 1;\nEND");
        Assert.False(result.Eligible);
        Assert.Equal("next-value-for", result.Refusal!.ReasonCode);
    }

    [Fact]
    public void LoopBody_NotABeginEndBlock_Refuses_RootAndNestedAlike()
    {
        var root = Plan("DECLARE @i int;\nWHILE @i < 2\n    SET @i = @i + 1;");
        Assert.Equal("loop-body-not-block", root.Refusal!.ReasonCode);

        var nested = Plan("DECLARE @i int, @j int;\nWHILE @i < 2\nBEGIN\n    WHILE @j < 2\n        SET @j = @j + 1;\n    SET @i = @i + 1;\nEND");
        Assert.Equal("loop-body-not-block", nested.Refusal!.ReasonCode);
    }

    [Fact]
    public void GotoTargetingOutsideTheSubtree_Refuses_InternalGotoDoesNot()
    {
        var external = Plan("DECLARE @i int;\nWHILE @i < 2\nBEGIN\n    SET @i = @i + 1;\n    GOTO afterloop;\nEND\nafterloop:");
        Assert.Equal("goto-outside-subtree", external.Refusal!.ReasonCode);

        var internalJump = Plan("DECLARE @i int;\nWHILE @i < 2\nBEGIN\n    GOTO bump;\n    bump:\n    SET @i = @i + 1;\nEND");
        Assert.True(internalJump.Eligible, internalJump.Refusal?.Detail);
    }

    [Fact]
    public void TwoFaultCapablePositions_SharingALine_Refuse()
    {
        var result = Plan("DECLARE @a int, @b int;\nWHILE @a < 2\nBEGIN\n    SET @a = @a + 1; SET @b = @a;\nEND");
        Assert.False(result.Eligible);
        Assert.Equal("line-ambiguity", result.Refusal!.ReasonCode);
    }

    [Fact]
    public void BreakpointOrLogpointOnAnyMemberLine_Refuses_IncludingTheRoot()
    {
        const string script = "DECLARE @i int;\nWHILE @i < 2\nBEGIN\n    SET @i = @i + 1;\nEND";

        var onBody = Plan(script, isBlocked: u => u.SubKind == SuSubKind.SetVariable);
        Assert.Equal("breakpoint-or-logpoint", onBody.Refusal!.ReasonCode);

        // The root is a member too: a WHILE-line breakpoint must re-fire per
        // predicate evaluation and can never be coalesced past (§13/A21).
        var onRoot = Plan(script, isBlocked: u => u.SubKind == SuSubKind.While);
        Assert.Equal("breakpoint-or-logpoint", onRoot.Refusal!.ReasonCode);
    }

    [Fact]
    public void BareRethrowMember_InsideAnEnclosingCatch_Refuses_WithTheWhitelistReasonCode()
    {
        // O5 (M7 hardening design notes §5.6): a per-case witness for SuSubKind.Rethrow
        // specifically -- the lockstep pin below already proves it's excluded from
        // AllowedMemberSubKinds, but never exercises the walk actually REACHING one as
        // a member and refusing it live. A bare THROW is only legal lexically inside a
        // CATCH block (engine 10704, ControlFlowValidation) -- the CATCH must enclose
        // the WHILE from OUTSIDE (never nested inside it, which would trip the earlier
        // "try-catch" refusal instead) for the walk to ever reach it as a plain member.
        var result = Plan("""
            DECLARE @i int;
            BEGIN TRY
                SELECT 1;
            END TRY
            BEGIN CATCH
                WHILE @i < 2
                BEGIN
                    THROW;
                    SET @i = @i + 1;
                END
            END CATCH
            """);
        Assert.False(result.Eligible);
        Assert.Equal("member:Rethrow", result.Refusal!.ReasonCode);
    }

    [Fact]
    public void NonControlRoot_Refuses()
    {
        var (body, full) = ParseTestHelper.ParseBatch("DECLARE @i int;\nSET @i = 1;");
        var cursor = ExecutionCursor.Create(body, full);
        var executable = cursor.Index.All.First(u => u.SubKind == SuSubKind.SetVariable);
        var result = BoostPlanner.TryPlan(executable, cursor.Index, BoostSessionGate.PlainHealthy, _ => false);
        Assert.Equal("not-control-node", result.Refusal!.ReasonCode);
    }

    // -------------------------------------------------------------- lockstep pin

    [Fact]
    public void LockstepPin_AllowedSubKinds_MatchTheRatifiedA21List_Exactly()
    {
        // A21 (§14, ratified 2026-07-07): "members may be IF/WHILE/BEGIN…END,
        // internal GOTO/labels, BREAK/CONTINUE, DML/SELECT, SET @var, PRINT,
        // RAISERROR, THROW-with-args, and cursor OPEN/FETCH/CLOSE". BEGIN…END and
        // labels are Structural/Label (never member SUs); DML/SELECT is
        // SuSubKind.General (node-type-gated to the five DML statement types inside
        // the walk); cursor ops are CursorOp (DEALLOCATE-gated). Everything else —
        // including any FUTURE SuSubKind — is refused by whitelist absence:
        // conservative-closed by construction, not by enumeration.
        var expected = new[]
        {
            SuSubKind.General, SuSubKind.SetVariable, SuSubKind.Print, SuSubKind.RaiseError,
            SuSubKind.If, SuSubKind.While, SuSubKind.Goto, SuSubKind.Break, SuSubKind.Continue,
            SuSubKind.Throw, SuSubKind.CursorOp,
        };

        Assert.Equal(
            expected.OrderBy(k => (int)k).ToArray(),
            BoostPlanner.AllowedMemberSubKinds.OrderBy(k => (int)k).ToArray());

        // The closure half: every OTHER declared SuSubKind is outside the set today.
        var refused = Enum.GetValues<SuSubKind>().Where(k => !BoostPlanner.AllowedMemberSubKinds.Contains(k)).ToArray();
        Assert.Equal(
            new[]
            {
                SuSubKind.SetOption, SuSubKind.Execute, SuSubKind.TempTableDdl, SuSubKind.ModuleDdl,
                SuSubKind.Other,
                SuSubKind.Return, SuSubKind.WaitFor, SuSubKind.TryCatch, SuSubKind.Rethrow,
                SuSubKind.BeginTran, SuSubKind.Commit, SuSubKind.Rollback, SuSubKind.SaveTran,
                SuSubKind.TableVarDeclare, SuSubKind.CursorDeclare,
            }.OrderBy(k => (int)k).ToArray(),
            refused.OrderBy(k => (int)k).ToArray());
    }

    // ---------------------------------------------- A59 (§9/§14): table-type scalar reference

    [Theory]
    // A scalar UDF taking the TVP…
    [InlineData("WHILE @i < 3\nBEGIN\n    SET @n = dbo.cnt(@t);\n    SET @i = @i + 1;\nEND")]
    // …a table-valued one…
    [InlineData("WHILE @i < 3\nBEGIN\n    INSERT dbo.log SELECT * FROM dbo.tvf(@t);\n    SET @i = @i + 1;\nEND")]
    // …and the boost root's own predicate.
    [InlineData("IF dbo.cnt(@t) > 1\n    SET @n = 99;")]
    public void Refuses_ASubtreeThatPassesATableTypeVariableAsATvpArgument(string script)
    {
        // Boosting it would need the §9 materialization INSERT in the batch preamble — and an
        // INSERT into a table variable with an IDENTITY column MOVES the connection's identity
        // chain (fact 34h), which is precisely the chain boost's post-block SCOPE_IDENTITY()
        // capture rides (fact 26d). Interpreted mode materializes it and poisons the chain
        // deliberately; a boosted slice cannot, because afterwards its own in-slice inserts and
        // the preamble insert are indistinguishable. Conservative-closed: refusal costs speed.
        var result = Plan(script, tableTypeVariables: new[] { "@t" });

        Assert.False(result.Eligible);
        Assert.Equal("table-type-scalar-reference", result.Refusal!.ReasonCode);
    }

    [Fact]
    public void Allows_ASubtreeThatMerelyReadsATableTypeVariable()
    {
        // `FROM @t` is a VariableTableReference — R1 rewrites it to the realization and nothing
        // is materialized, so the chain never moves. Refusing here would kill boost for every
        // loop that so much as reads a table variable, which is the common case.
        var result = Plan(
            "WHILE @i < 3\nBEGIN\n    SET @n = (SELECT COUNT(*) FROM @t);\n    SET @i = @i + 1;\nEND",
            tableTypeVariables: new[] { "@t" });

        Assert.True(result.Eligible, result.Refusal?.Detail);
    }
}
