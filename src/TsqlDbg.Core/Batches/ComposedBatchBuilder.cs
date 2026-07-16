using System.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using TsqlDbg.Core.Execution;
using TsqlDbg.Core.Interpreter;
using TsqlDbg.Core.Rewrite;
using TsqlDbg.Core.State;

namespace TsqlDbg.Core.Batches;

// DESIGN §7.1: the composed batch is the text actually sent to the server for one
// step. `B` = 1-based batch line where the original statement text begins — computed
// by the builder's own line accounting (never hardcoded), per the M0->M1 gate review
// task list. `Parameters` is non-null only for doomed-mode batches (§10.4 — variable
// values ride parameters when the state table can't be read usefully or written).
public sealed record ComposedBatch(
    string Text, int FrameOrdinal, int B, IReadOnlySet<ShadowKind> RequiredShadows,
    IReadOnlyList<BatchParameter>? Parameters = null)
{
    /// <summary>A59 (§9/§7.4, fact 34h): this batch's preamble materializes a table-type
    /// variable whose type has an IDENTITY column, so the debugger's own INSERT has moved the
    /// connection's identity chain. The session must poison the A26/D1 scope chain — see
    /// <see cref="ComposedBatchBuilder.MovesIdentityChain"/>. Init-only and defaulted, so
    /// every existing positional construction (and its pins) is untouched.</summary>
    public bool MovesIdentityChain { get; init; }
}

/// <summary>
/// M3 (§10) knobs on the §7.1 shell. The M1/M2 shape is <see cref="Default"/>;
/// the session derives the rest from its watchdog/context state per batch.
/// </summary>
public sealed record BatchComposition
{
    public static readonly BatchComposition Default = new();

    /// <summary>C13 (§11.2): the debuggee's current tracked <c>SET ROWCOUNT</c> value ("0" =
    /// unlimited). The invariant is that the connection's RESTING ROWCOUNT is always 0, so every
    /// out-of-band bookkeeping op (the §9 TVP copy, the §4/§11.3 catalog queries, GO-boundary
    /// rebuilds, inspection reads, watch/condition evals) runs unlimited without needing its own
    /// wrap. When this is non-"0" the batch preamble asserts <c>SET ROWCOUNT 0</c>, re-applies this
    /// value immediately before the user statement, and resets to 0 again after the capture — so the
    /// debuggee statement sees exactly its limit and the connection is left at rest (0).</summary>
    public string RowCount { get; init; } = "0";

    /// <summary>C13: force the trailing <c>SET ROWCOUNT 0</c> reset even when <see cref="RowCount"/>
    /// is "0" — set for the debuggee's OWN <c>SET ROWCOUNT n</c> statement, whose user-statement slot
    /// sets a non-zero limit the resting invariant must immediately neutralize (the tracker keeps the
    /// value for the next statement to re-apply). No effect on any other statement.</summary>
    public bool ResetRowCountAfterStatement { get; init; }

    /// <summary>False for predicate/scalar-eval shells (side-effect-free by construction).</summary>
    public bool IncludeStateWrite { get; init; } = true;

    /// <summary>Emit the trailing __dbg_state raw-value result set (the session's binary
    /// snapshot source, §8.1/§10.4 — piggybacked on the same round trip instead of a
    /// separate SELECT *). True for batches that can change variable values.</summary>
    public bool IncludeStateSnapshot { get; init; } = true;

    /// <summary>§10.7: the active error context to re-materialize server-side so
    /// INDIRECT consumers (stepped-over modules, dynamic SQL, UDFs) see real ERROR_*()
    /// values. Direct references were already rewritten exactly by R7.</summary>
    public ErrorContextValues? Rematerialize { get; init; }

    /// <summary>The frame's tracked SET XACT_ABORT state. Load-bearing for the fact-19
    /// sandwich: the re-materialized RAISERROR is NOT exempt from XACT_ABORT and would
    /// doom a healthy transaction — the shell turns XACT_ABORT off around it and
    /// restores it (only) when this is true, right before the user statement.</summary>
    public bool XactAbortOn { get; init; }

    /// <summary>§10.4 doomed mode: seed every frame variable from these snapshot values
    /// (aligned with Frame.Variables.All) via ADO.NET parameters instead of reading the
    /// state table — the table is unwritable while doomed (3930), so its content is
    /// stale from the first post-doom step onward. Callers also set
    /// IncludeStateWrite = false.</summary>
    public IReadOnlyList<object?>? DoomedSeedValues { get; init; }

    /// <summary>§13 conditions / §12.4 watch: debugger-initiated evals are invisible to
    /// the debuggee — beyond never touching shadows (D3), a FAULTING eval must not doom
    /// the transaction under the debuggee's XACT_ABORT ON (fact 19). Wraps the batch in
    /// its own XACT_ABORT OFF sandwich, restored after END CATCH when XactAbortOn.</summary>
    public bool DebuggerInitiated { get; init; }

    /// <summary>§10.4 / engine fact 22: a doomed transaction CANNOT survive a batch
    /// boundary — the engine force-rolls it back with 3998 the moment the dooming batch
    /// ends. While the session is logically doomed, every composed batch therefore
    /// re-establishes a REAL doomed transaction up front (BEGIN TRANSACTION × this
    /// count, then a caught error under a forced XACT_ABORT ON), so that
    /// XACT_STATE()/@@TRANCOUNT — never rewritten, §7.4 — read genuine doomed values,
    /// debuggee writes genuinely fault with 3930, and the debuggee's own ROLLBACK
    /// (`IF XACT_STATE() = -1 ROLLBACK`, the §10.4 archetype) genuinely exits doom
    /// inside the batch, ending it cleanly. Null when the session is healthy.</summary>
    public int? RedoomTrancount { get; init; }

    /// <summary>M4 (§9/D8, caveat C25): guarded re-creation DDL emitted in the batch's
    /// healthy prefix — after the F5 preamble line, BEFORE the redoom block re-dooms —
    /// healing table-variable realizations the fact-22 forced rollback destroyed where
    /// native (non-transactional, fact 2) would still have the object. The connection
    /// is transactionless at batch start while doomed, so plain DDL here autocommits;
    /// each entry is a complete `IF OBJECT_ID(…) IS NULL CREATE TABLE …;` statement
    /// from the §9 registry. Null/empty when nothing needs healing.</summary>
    public IReadOnlyList<string>? HealthyPrefixDdl { get; init; }

    /// <summary>D5/A13 (§10.1, facts 23-H + 24): a stepped-over EXEC with no armed TRY
    /// in ANY frame composes WITHOUT the §10.1 oracle — the wrapper TRY would impose
    /// transfer semantics native only has when a TRY is armed. The shell drops the
    /// outer BEGIN TRY/CATCH pair (the §10.7 re-mat block, whose own TRY is consumed
    /// before the user statement runs, stays compatible), and the Ok control row gains
    /// an err_after column carrying the post-EXEC caller-scope @@ERROR (fact 24 d: an
    /// absorbed statement-level failure of the EXEC itself leaves @@ERROR non-zero on
    /// a batch that "succeeded"). Executed via ExecuteAbsorbingAsync; the session
    /// classifies a no-control-row/no-exception result from the absorbed tail
    /// (fact 24 b). Never combined with DebuggerInitiated.</summary>
    public bool OracleFree { get; init; }

    /// <summary>M6 (§14/A21): non-null = this batch executes a boosted subtree; the
    /// value is the session-monotonic sequence id the boost prologue writes to
    /// #__dbg_boost (making the B7 recovery read stale-proof — a mismatched seq means
    /// "nothing of THIS batch completed"). Boost requires a plain-healthy session
    /// (B2) and the oracle (B5), so it is mutually exclusive with Rematerialize,
    /// DebuggerInitiated, OracleFree, RedoomTrancount, DoomedSeedValues, and
    /// HealthyPrefixDdl — all builder-asserted. The boosted CATCH control row
    /// additionally carries scope_identity (F2 ruling, add-only §7.3 evolution like
    /// D5's err_after): fault-time completed inserts must reach the R6 shadow, and
    /// SCOPE_IDENTITY() survives into CATCH (fact 26d).</summary>
    public int? BoostSeq { get; init; }
}

public static class ComposedBatchBuilder
{
    // DESIGN §7.1 template's DECLARE line includes "@__dbg_ok bit" but nothing in the
    // template ever assigns or reads it (ok=1/ok=0 only ever appear as SELECT literals,
    // never as a variable read) — an apparent unused leftover. Omitted here as dead
    // code; noted rather than silently "fixed" in DESIGN.md itself (CLAUDE.md rule 2:
    // that's a spec edit, not something to change unilaterally). Functionally inert
    // either way — declaring an unused local has no observable effect.

    // DESIGN §7.2/§8.2: a DECLARE's initializer becomes a synthetic `SET @v = expr`
    // executable SU. The initializer is itself a positioned AST fragment (may
    // reference @@ROWCOUNT etc. — phase0-integration-notes.md's driver-loop
    // reference), so it goes through the same rewrite pipeline as any statement.
    public static ComposedBatch BuildSyntheticAssignment(
        Frame frame, RewriteEngine rewriteEngine, RewriteContext rewriteContext,
        VariableDeclaration declaration, string fullScript, ShadowValues shadowValues,
        BatchComposition? composition = null)
    {
        var initializer = declaration.Fragment.Value
            ?? throw new InvalidOperationException($"{declaration.Name} has no initializer to build a synthetic assignment for.");
        var rewrite = rewriteEngine.Rewrite(initializer, fullScript, rewriteContext);
        var syntheticText = $"SET {declaration.Name} = {rewrite.PatchedText};";
        return Build(frame, rewriteContext, syntheticText, rewrite.RequiredShadows, shadowValues,
            composition ?? BatchComposition.Default, CollectTvpArguments(initializer, frame));
    }

    public static ComposedBatch BuildForUnit(
        Frame frame, RewriteEngine rewriteEngine, RewriteContext rewriteContext,
        StatementUnit unit, string fullScript, ShadowValues shadowValues,
        BatchComposition? composition = null)
    {
        var rewrite = rewriteEngine.Rewrite(unit.Fragment, fullScript, rewriteContext);
        return Build(frame, rewriteContext, rewrite.PatchedText, rewrite.RequiredShadows, shadowValues,
            composition ?? BatchComposition.Default, CollectTvpArguments(unit.Fragment, frame));
    }

    /// <summary>
    /// DESIGN §9 (A59): the frame's table-type variables this fragment references as a scalar
    /// <c>VariableReference</c> — i.e. passes as a TVP ARGUMENT. Not only `EXEC p @rows = @t`:
    /// `dbo.f(@t)` inside a DECLARE initializer, an IF/WHILE predicate, a RETURN expression,
    /// a watch/logpoint expression or a `FROM dbo.tvf(@t)` is the same shape, which is why
    /// EVERY composed-batch entry point runs this — a batch that references <c>@t</c> without
    /// declaring it dies with error 137 (batch-aborting: session over), where native runs fine.
    ///
    /// A reference to <c>@t</c> as a table is a <c>VariableTableReference</c>, which R1 has
    /// already rewritten to the realization and which needs no materialization at all — so
    /// the ordinary `INSERT INTO @t` / `SELECT … FROM @t` statements cost nothing here.
    /// </summary>
    public static IReadOnlyList<TableTypeVariable> CollectTvpArguments(TSqlFragment fragment, Frame frame)
    {
        if (frame.TableTypeVariables.Count == 0)
        {
            return Array.Empty<TableTypeVariable>();
        }

        var collector = new ScalarVariableCollector();
        fragment.Accept(collector);

        var arguments = new List<TableTypeVariable>();
        foreach (var name in collector.Names)
        {
            if (frame.TableTypeVariables.TryGetValue(name, out var tvp) && !arguments.Contains(tvp))
            {
                arguments.Add(tvp);
            }
        }

        return arguments;
    }

    private sealed class ScalarVariableCollector : TSqlFragmentVisitor
    {
        public List<string> Names { get; } = new();

        // A VariableTableReference (`FROM @t`) CONTAINS a VariableReference for its own name,
        // so descending into one would report every ordinary table-variable read as a TVP
        // argument — a pointless copy per statement, and a needless @@IDENTITY perturbation
        // (C26). R1 has already rewritten that reference to the realization; stop here.
        public override void ExplicitVisit(VariableTableReference node)
        {
        }

        public override void Visit(VariableReference node)
        {
            if (node.Name is { Length: > 0 } name
                && !Names.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                Names.Add(name);
            }
        }
    }

    // DESIGN §6: predicate evaluation and RETURN-value evaluation go "through the
    // normal pipeline (so it is rewritten, error-wrapped, and can itself fault)".
    // M2 decision (docs/archive/reviews/m2-cursor-design-notes-fable.md): reuse this builder's
    // §7.1 shell with a synthetic SELECT in the user-statement slot, minus the state
    // write — no T-SQL predicate/scalar expression can assign to a variable, so
    // evaluation is side-effect-free by construction (mirror of §12.3's REPL rule).
    // The driver reads column "p" of the single user result set; the control row's
    // rc/scope_identity reflect this wrapper SELECT, never native truth — after a
    // debuggee IF/WHILE predicate the faithful update is
    // ShadowValues.ObservePredicateEvaluation() (engine fact 12), and debugger-initiated
    // evaluations (breakpoint conditions, watch) must not touch shadows at all.
    // CASE maps NULL/UNKNOWN to 0 — native IF/WHILE falsy semantics.
    public static ComposedBatch BuildForPredicate(
        Frame frame, RewriteEngine rewriteEngine, RewriteContext rewriteContext,
        BooleanExpression predicate, string fullScript, ShadowValues shadowValues,
        BatchComposition? composition = null)
    {
        var rewrite = rewriteEngine.Rewrite(predicate, fullScript, rewriteContext);
        var text = $"SELECT CASE WHEN {rewrite.PatchedText} THEN 1 ELSE 0 END AS p;";
        return Build(frame, rewriteContext, text, rewrite.RequiredShadows, shadowValues,
            (composition ?? BatchComposition.Default) with { IncludeStateWrite = false, IncludeStateSnapshot = false },
            CollectTvpArguments(predicate, frame));
    }

    // §6 RETURN-value capture (and the natural shell for §12.4 watch / §13 logpoint
    // scalar evaluation when those milestones arrive). Same rules as BuildForPredicate.
    public static ComposedBatch BuildForScalarEval(
        Frame frame, RewriteEngine rewriteEngine, RewriteContext rewriteContext,
        ScalarExpression expression, string fullScript, ShadowValues shadowValues,
        BatchComposition? composition = null)
    {
        var rewrite = rewriteEngine.Rewrite(expression, fullScript, rewriteContext);
        var text = $"SELECT ({rewrite.PatchedText}) AS p;";
        return Build(frame, rewriteContext, text, rewrite.RequiredShadows, shadowValues,
            (composition ?? BatchComposition.Default) with { IncludeStateWrite = false, IncludeStateSnapshot = false },
            CollectTvpArguments(expression, frame));
    }

    // M6 G2 (design note §4, A23): logpoints' one-round-trip multi-expression shell —
    // `SELECT (e1) AS v0, (e2) AS v1, …` — this is the consumer BuildForScalarEval's
    // comment named. Each expression keeps its OWN source text: they were parsed
    // independently (ScriptParser.ParseScalarExpression per {expr} segment), so each
    // one's AST offsets are only valid against its own text, never a concatenation.
    public static ComposedBatch BuildForMultiScalarEval(
        Frame frame, RewriteEngine rewriteEngine, RewriteContext rewriteContext,
        IReadOnlyList<(ScalarExpression Expression, string SourceText)> expressions,
        ShadowValues shadowValues, BatchComposition? composition = null)
    {
        var requiredShadows = new HashSet<ShadowKind>();
        var columns = new List<string>(expressions.Count);
        var tvpArguments = new List<TableTypeVariable>();
        for (var i = 0; i < expressions.Count; i++)
        {
            var (expression, sourceText) = expressions[i];
            var rewrite = rewriteEngine.Rewrite(expression, sourceText, rewriteContext);
            requiredShadows.UnionWith(rewrite.RequiredShadows);
            columns.Add($"({rewrite.PatchedText}) AS v{i}");
            foreach (var tvp in CollectTvpArguments(expression, frame))
            {
                if (!tvpArguments.Contains(tvp))
                {
                    tvpArguments.Add(tvp);       // one materialization covers all N expressions
                }
            }
        }

        var text = $"SELECT {string.Join(", ", columns)};";
        return Build(frame, rewriteContext, text, requiredShadows, shadowValues,
            (composition ?? BatchComposition.Default) with { IncludeStateWrite = false, IncludeStateSnapshot = false },
            tvpArguments);
    }

    // M5 I6 (§12.3 REPL) + A45 (2026-07-12, docs/archive/reviews/repl-variable-seed-opus.md):
    // a SEPARATE shell from Build below — the REPL passes the user's OWN result set
    // through and reports faults via its own micro TRY/CATCH (marker columns), so it
    // does NOT use Build's control-row / state-write / snapshot machinery. But, like
    // every interpreted statement, it now DECLAREs and SEEDs the selected frame's
    // variables so a bare `@var` reference resolves to its live value (A45 fixed the
    // report that `SELECT @x` returned error 137 — "must declare @x" — because the
    // console never declared it). The seed is READ-ONLY: the batch never writes the
    // state table back, so an assignment to a seeded local (`SET @x = …`) dies with
    // the batch — inspection stays side-effect-free (§12.3), and setVariable remains
    // the only path that mutates a debuggee variable. Seed SOURCE mirrors Build
    // exactly: healthy/detached read the frame's state table (a plain language batch);
    // doomed seeds from the binary snapshot via parameters (the table is stale and
    // unwritable under 3930 — §10.4), the SAME redoom+param shape the debuggee doomed
    // batch already uses. The redoom-prefix (shared AppendRedoomPrefix, M6 R3) and
    // healing-DDL blocks stay as before — this method only CALLS the Session-tracked
    // state (RedoomTrancount/HealthyPrefixDdl/DoomedSeedValues), it does not change how
    // the watchdog decides anything.
    // Marker columns (never a §7.3 control row): __dbg_repl_err (CATCH path — error
    // detail) and __dbg_repl_probe (write-mode trailing SELECT @@TRANCOUNT,
    // XACT_STATE() — design note §5 item 5: "feeds the SAME edge-detection code as
    // control rows," so its shape mirrors trancount/xact_state exactly).
    public static ComposedBatch BuildForRepl(
        Frame frame, RewriteEngine rewriteEngine, RewriteContext rewriteContext,
        TSqlStatement statement, string statementText, ShadowValues shadowValues,
        BatchComposition composition, bool includeTrailingProbe, bool includeStateWriteback = false)
    {
        var rewrite = rewriteEngine.Rewrite(statement, statementText, rewriteContext);
        var nonce = rewriteContext.SessionNonce;
        var sb = new StringBuilder();

        sb.Append("SET QUOTED_IDENTIFIER ").Append(frame.SetEnv.QuotedIdentifier ? "ON" : "OFF").Append(";\n");
        sb.Append("SET ANSI_NULLS ").Append(frame.SetEnv.AnsiNulls ? "ON" : "OFF").Append(";\n");
        sb.Append("SET NOCOUNT ON;\n");
        sb.Append("SET XACT_ABORT OFF;\n"); // §12.3/fact 19: DebuggerInitiated sandwich

        if (composition.HealthyPrefixDdl is { Count: > 0 } healing)
        {
            foreach (var ddl in healing)
            {
                sb.Append(ddl).Append('\n');
            }
        }

        if (composition.RedoomTrancount is { } redoomTrancount)
        {
            // REPL site: the trailing restore is always OFF — the §12.3
            // DebuggerInitiated sandwich stays off until the batch-final restore.
            AppendRedoomPrefix(line => sb.Append(line).Append('\n'), nonce, redoomTrancount, restoreXactAbortOn: false);
        }

        // A45: declare the frame's variables alongside any R4–R7 shadow substitutes,
        // then seed the variables read-only (mirrors Build's declare+seed arms). Shadow
        // substitutes keep their inline `= literal` initializer; frame variables are
        // seeded from the state table (healthy/detached) or the snapshot (doomed) below.
        var variables = frame.Variables.All;
        var doomedSeed = composition.DoomedSeedValues;
        if (doomedSeed is not null && doomedSeed.Count != variables.Count)
        {
            throw new ArgumentException(
                $"Doomed-mode seed has {doomedSeed.Count} values for {variables.Count} variables — snapshot misaligned (§10.4).");
        }

        var declareParts = new List<string>();
        foreach (var v in variables)
        {
            declareParts.Add($"{v.Declaration.Name} {v.Declaration.DataTypeSql}");
        }

        foreach (var kind in rewrite.RequiredShadows)
        {
            declareParts.Add($"{rewriteContext.ShadowVariable(kind)} {ShadowSqlType(kind)} = {shadowValues.Literal(kind)}");
        }

        if (declareParts.Count > 0)
        {
            sb.Append("DECLARE ").Append(string.Join(", ", declareParts)).Append(";\n");
        }

        List<BatchParameter>? parameters = null;
        if (variables.Count > 0)
        {
            if (doomedSeed is null)
            {
                // Healthy/detached: read live values from the SELECTED frame's state
                // table — a plain read, no parameters (keeps this a language batch, so a
                // console-created #temp still persists on the connection, §12.3). The
                // seed populates batch-local copies only; it never writes the table.
                var reads = variables.Select(v => $"{v.Declaration.Name} = {StateTableIdentifiers.ColumnName(v.Declaration.Name)}");
                sb.Append("SELECT ").Append(string.Join(", ", reads))
                  .Append(" FROM ").Append(StateTableIdentifiers.TableName(frame.Ordinal)).Append(";\n");
            }
            else
            {
                // Doomed (§10.4): the state table went stale the moment the transaction
                // doomed (postamble writes hit 3930), so values ride parameters from the
                // session's binary snapshot instead — CONVERT to the declared type keeps
                // precision/scale/collation exact. This is the shape Build's doomed arm
                // uses; the parameters flip the batch to sp_executesql transport, exactly
                // as the debuggee doomed batch (redoom + params) already does.
                parameters = new List<BatchParameter>(variables.Count);
                var seeds = new List<string>(variables.Count);
                for (var i = 0; i < variables.Count; i++)
                {
                    var v = variables[i];
                    var parameterName = SeedParam(nonce, v.Ordinal);
                    parameters.Add(new BatchParameter(parameterName, doomedSeed[i]));
                    // A59 (§8.1): CONVERT takes the STORAGE type — the engine refuses a
                    // CONVERT to a user-defined alias type outright (fact 34b, msg 243).
                    seeds.Add($"{v.Declaration.Name} = CONVERT({v.Declaration.StorageType}, {parameterName})");
                }

                sb.Append("SELECT ").Append(string.Join(", ", seeds)).Append(";\n");
            }
        }

        // A59 (§9/§12.3): a console statement can pass a TVP argument too — same
        // materialization as the interpreted path, and it moves the identity chain the same
        // way (fact 34h), so the REPL's caller poisons the scope chain on this flag too. A
        // debugger-initiated batch must be invisible to the debuggee (D3) — that is exactly
        // what the poison buys: the shadow keeps serving the client-modeled value.
        var replTvps = CollectTvpArguments(statement, frame);
        AppendTvpMaterialization(line => sb.Append(line).Append('\n'), replTvps);

        sb.Append("BEGIN TRY\n");
        sb.Append(rewrite.PatchedText.TrimEnd()).Append(rewrite.PatchedText.TrimEnd().EndsWith(';') ? "\n" : ";\n");
        if (includeTrailingProbe)
        {
            // §12.3/C5: capture @@ROWCOUNT the instant the console statement completes —
            // the state write-back below and the trailing probe would both clobber it.
            // Filtered out of the user output by ParseReplBatchResult and surfaced only as
            // the "(N rows affected)" line for a DML write, mirroring the stepped-statement
            // note. Guarded by includeTrailingProbe (a real table/row write); a variable-only
            // `SET @x` gets no probe and needs no rowcount. Inside the TRY, so a faulted
            // statement skips it (jumps to CATCH) — no rowcount for a rolled-back write.
            sb.Append("SELECT @@ROWCOUNT AS __dbg_repl_rowcount;\n");
        }

        if (includeStateWriteback && variables.Count > 0)
        {
            // A46 (§12.3): a write-mode console statement persists its variable changes
            // exactly like an interpreted statement — write the (possibly changed) values
            // back to the state table (guarded off while doomed, fact 22) and read them
            // back in a __dbg_state set so the session refreshes Frame.Snapshot. Uses
            // batch-local variables (NO parameters), so a healthy console batch stays a
            // language batch (console-created #temps still persist). On a caught fault the
            // statement rolled back, so this success-path write-back is correctly skipped
            // (the CATCH emits no __dbg_state — the session leaves Frame.Snapshot as-is).
            var writebackTable = StateTableIdentifiers.TableName(frame.Ordinal);
            sb.Append($"IF XACT_STATE() <> -1 AND OBJECT_ID('tempdb..{writebackTable}') IS NOT NULL\n");
            sb.Append("    ").Append(BuildStateWrite(writebackTable, variables)).Append('\n');
            sb.Append(BuildStateSnapshotSelect(variables)).Append('\n');
        }

        sb.Append("END TRY\n");
        sb.Append("BEGIN CATCH\n");
        sb.Append("SELECT 1 AS __dbg_repl_err, ERROR_NUMBER() AS err_number, ERROR_SEVERITY() AS err_severity,\n")
          .Append("       ERROR_STATE() AS err_state, ERROR_LINE() AS err_line,\n")
          .Append("       ERROR_PROCEDURE() AS err_procedure, ERROR_MESSAGE() AS err_message;\n");
        sb.Append("END CATCH\n");

        if (includeTrailingProbe)
        {
            sb.Append("SELECT 1 AS __dbg_repl_probe, @@TRANCOUNT AS trancount, XACT_STATE() AS xact_state;\n");
        }

        if (composition.XactAbortOn)
        {
            sb.Append("SET XACT_ABORT ON;\n"); // restore the debuggee's setting
        }

        // B = 0: the REPL reports faults from the server's ERROR_LINE() (the
        // __dbg_repl_err marker set), never from the composed-batch line map, so the
        // ComposedBatch.B slot is unused on this path. `parameters` is non-null only for
        // the doomed snapshot seed (§10.4); healthy/detached is a plain language batch.
        return new ComposedBatch(sb.ToString(), frame.Ordinal, 0, rewrite.RequiredShadows, parameters)
        {
            MovesIdentityChain = MovesIdentityChain(replTvps),
        };
    }

    // M6 (§14/A21): the boosted-subtree composition — the §7.1 shell around the WHOLE
    // control node's original slice, span-patched with R1–R3 plus the marker
    // insertions (BoostSubtreeMarkers), executed as ONE batch under `continue`.
    // The caller (BoostPlanner, item 3) is responsible for eligibility; this method
    // asserts the structural consequences it relies on:
    //   - RequiredShadows must come back EMPTY (B3 refused every R4–R7 trigger; a
    //     shadow requirement here means an intrinsic reference slipped past the
    //     planner — session bug, throw);
    //   - the composition must be plain-healthy Default shape (B2/B5 guards in Build);
    //   - the batch must be parameter-free (F1 ruling / fact 26e: a parameter flips
    //     SqlClient to sp_executesql transport, whose child scope severs the
    //     session-sticky SCOPE_IDENTITY chain the postamble capture rides).
    public static ComposedBatch BuildForBoostedSubtree(
        Frame frame, RewriteEngine rewriteEngine, RewriteContext rewriteContext,
        StatementUnit controlNode, string fullScript, ShadowValues shadowValues,
        int seq, IReadOnlyList<BoostMarker> markers, bool xactAbortOn = false, string rowCount = "0")
    {
        if (controlNode.SubKind is not (SuSubKind.If or SuSubKind.While))
        {
            throw new ArgumentException(
                $"Boost roots are IF/WHILE units only (§14/A21); got {controlNode.SubKind}.", nameof(controlNode));
        }

        if (CollectTvpArguments(controlNode.Fragment, frame) is { Count: > 0 } tvps)
        {
            // A59 (§14/§9): the planner must have refused this subtree. Materializing a TVP
            // inside a boosted batch would prepend an INSERT into a table variable, which
            // MOVES the connection's identity chain (fact 34h) — and boost's whole B7
            // recovery rides the post-block SCOPE_IDENTITY() capture (F2 ruling, fact 26d).
            // Refusing costs only speed; getting the chain wrong costs correctness.
            throw new InvalidOperationException(
                $"Boosted slice passes table-type variable(s) {string.Join(", ", tvps.Select(t => t.Name))} as a " +
                "scalar — the planner must refuse (§14/A21, A59). Session bug.");
        }

        var table = StateTableIdentifiers.TableName(frame.Ordinal);
        var variables = frame.Variables.All;
        var collector = new SpanPatchCollector(RuleId.Boost);
        foreach (var marker in markers)
        {
            if (marker.Suppressed)
            {
                continue;                                     // A21 trailing suppression (fact 27)
            }

            collector.AddInsertionAfter(marker.Child, BuildMarkerInsertion(table, variables, marker));
        }

        var rewrite = rewriteEngine.Rewrite(controlNode.Fragment, fullScript, rewriteContext, collector.Patches);
        if (rewrite.RequiredShadows.Count > 0)
        {
            throw new InvalidOperationException(
                "Boosted slice required shadow substitutes (" + string.Join(", ", rewrite.RequiredShadows) +
                ") — the planner must refuse intrinsic-referencing subtrees (§14/A21 B3). Session bug.");
        }

        // XactAbortOn threads the frame's tracked setting through the F5 preamble
        // line AND gives faults inside the slice their native abort class (a debuggee
        // running with XACT_ABORT ON must doom on an in-slice fault exactly as
        // interpreted mode would).
        var batch = Build(frame, rewriteContext, rewrite.PatchedText, rewrite.RequiredShadows, shadowValues,
            BatchComposition.Default with { BoostSeq = seq, XactAbortOn = xactAbortOn, RowCount = rowCount });
        if (batch.Parameters is not null)
        {
            // Unreachable belt (DoomedSeedValues is already guarded off): fact 26e.
            throw new InvalidOperationException(
                "Boosted batches must be parameter-free — sp_executesql transport severs the SCOPE_IDENTITY chain (fact 26e).");
        }

        return batch;
    }

    // One marker insertion: single-line, leading semicolon (terminates the preceding
    // original statement), NO trailing semicolon (the following original text supplies
    // its own separation — §7.1's `;SELECT` trick), NO newlines (line arithmetic).
    // Guarded state write BEFORE the position update when the child assigns variables
    // — invariant P's ordering half: state persists before position advances.
    private static string BuildMarkerInsertion(string table, IReadOnlyList<VariableSlot> variables, BoostMarker marker)
    {
        var sb = new StringBuilder();
        if (marker.WritesState && variables.Count > 0)
        {
            var assigns = variables.Select(v => $"{StateTableIdentifiers.ColumnName(v.Declaration.Name)}={v.Declaration.Name}");
            sb.Append($";IF XACT_STATE() <> -1 AND OBJECT_ID('tempdb..{table}') IS NOT NULL UPDATE {table} SET {string.Join(", ", assigns)}");
        }

        sb.Append($";IF XACT_STATE() <> -1 UPDATE {StateTableIdentifiers.BoostPositionTable} SET pos = {marker.Pos}");
        return sb.ToString();
    }

    /// <summary>F1 ruling (docs/archive/reviews/m6-boost-core-fable.md §2): the ONE place
    /// #__dbg_boost is meant to be created and seeded — session initialization, when
    /// boost:true. On the fresh connection the seed INSERT's SCOPE_IDENTITY clobber
    /// (fact 26d) is a no-op (the chain is already NULL), and at trancount 0 the
    /// table is immune to every later ROLLBACK (fact 1). The per-batch prologue's
    /// CREATE/INSERT branches remain as defense-in-depth only.</summary>
    public static string BuildBoostSessionInit()
        => BoostGuardCreateLine() + "\n" + BoostGuardSeedLine(0) + "\n";

    private static string BoostGuardCreateLine()
        => $"IF OBJECT_ID('tempdb..{StateTableIdentifiers.BoostPositionTable}') IS NULL " +
           $"CREATE TABLE {StateTableIdentifiers.BoostPositionTable}(seq int NOT NULL, pos int NOT NULL);";

    private static string BoostGuardSeedLine(int seq)
        => $"IF NOT EXISTS (SELECT 1 FROM {StateTableIdentifiers.BoostPositionTable}) " +
           $"INSERT {StateTableIdentifiers.BoostPositionTable} VALUES ({seq}, -1);";

    // `B` note: for synthetic texts (DECLARE initializers, predicate/scalar shells) B
    // locates the synthetic line inside the batch; the user-visible fault location is
    // the SU's own span (the driver knows the unit it is performing).
    private static ComposedBatch Build(
        Frame frame, RewriteContext ctx, string patchedStatementText,
        IReadOnlySet<ShadowKind> requiredShadows, ShadowValues shadowValues,
        BatchComposition composition,
        IReadOnlyList<TableTypeVariable>? tvpArguments = null)
    {
        if (composition is { Rematerialize: not null, DebuggerInitiated: true })
        {
            // Conditions/watch never re-materialize: R7 already substituted their
            // direct ERROR_*() references exactly, and a debugger eval must not raise
            // anything server-side (§12.3 side-effect-free rule).
            throw new InvalidOperationException("Re-materialization and DebuggerInitiated are mutually exclusive (§10.7/§12.3).");
        }

        if (composition is { OracleFree: true, DebuggerInitiated: true })
        {
            // D5 is a debuggee-EXEC composition rule; debugger-initiated evals keep
            // the oracle (their faults are locally caught and reported — F5/§12.3).
            throw new InvalidOperationException("OracleFree and DebuggerInitiated are mutually exclusive (D5/A13).");
        }

        if (composition is { OracleFree: true, RedoomTrancount: not null })
        {
            // D5's trigger condition includes NOT doomed (the session keeps the
            // oracle while doomed — documented A13 residual); a redoom prefix on an
            // oracle-free batch means the session composed the wrong shape.
            throw new InvalidOperationException("OracleFree requires a healthy session — doomed batches keep the oracle (D5/A13).");
        }

        if (composition.BoostSeq is not null)
        {
            // M6 (§14/A21): boost is plain-healthy-only (B2) and oracle-retained (B5).
            // Any of these knobs on a boosted composition means the session dispatched
            // boost from a state the planner must have refused.
            if (composition.OracleFree)
                throw new InvalidOperationException("Boost keeps the §7.1 oracle — OracleFree × boost is invalid (§14/A21 B5).");
            if (composition.DebuggerInitiated)
                throw new InvalidOperationException("Boost executes debuggee code — DebuggerInitiated × boost is invalid (§14/A21).");
            if (composition.Rematerialize is not null)
                throw new InvalidOperationException("Boost requires no active error context — Rematerialize × boost is invalid (§14/A21 B2).");
            if (composition.RedoomTrancount is not null || composition.DoomedSeedValues is not null || composition.HealthyPrefixDdl is { Count: > 0 })
                throw new InvalidOperationException("Boost requires a plain-healthy session — doomed/detached composition knobs × boost are invalid (§14/A21 B2).");
        }

        var nonce = ctx.SessionNonce;
        var table = StateTableIdentifiers.TableName(frame.Ordinal);
        var variables = frame.Variables.All;
        var doomedSeed = composition.DoomedSeedValues;
        if (doomedSeed is not null && doomedSeed.Count != variables.Count)
        {
            throw new ArgumentException(
                $"Doomed-mode seed has {doomedSeed.Count} values for {variables.Count} variables — snapshot misaligned (§10.4).");
        }

        var sb = new StringBuilder();
        var lineCount = 0;

        void Line(string text)
        {
            sb.Append(text).Append('\n');
            lineCount++;
        }

        void AppendMultilineThenNewline(string text)
        {
            sb.Append(text).Append('\n');
            lineCount += CountLines(text);
        }

        Line($"SET QUOTED_IDENTIFIER {(frame.SetEnv.QuotedIdentifier ? "ON" : "OFF")};");
        Line($"SET ANSI_NULLS {(frame.SetEnv.AnsiNulls ? "ON" : "OFF")};");
        Line("SET NOCOUNT ON;");
        // F5 hardening (§10 line review, ratified 2026-07-06): re-assert the frame's
        // tracked XACT_ABORT like QI/ANSI_NULLS/NOCOUNT — every batch self-heals, so a
        // re-materialization or debugger-initiated batch that died between its leading
        // OFF and its trailing restore (timeout, compile-class failure) can never
        // leave later batches running with unfaithful abort semantics. Parameterized
        // batches ride sp_executesql, whose SET scope reverts at module exit (fact 9)
        // — irrelevant precisely because every batch begins with this line.
        Line($"SET XACT_ABORT {(composition.XactAbortOn ? "ON" : "OFF")};");

        // C13 (§11.2): the debuggee's SET ROWCOUNT persists on our one connection and would truncate
        // the debugger's OWN multi-row bookkeeping in this preamble — most importantly the §9 TVP
        // materialization copy (SET ROWCOUNT limits INSERT … SELECT), but the invariant is general:
        // all bookkeeping runs UNLIMITED, and the debuggee's tracked limit is re-applied only around
        // the user statement below. Emitted only when a limit is actually in force (the fast path
        // adds nothing). The push's own out-of-band bookkeeping is protected the same way in
        // Session.PushCalleeFrameAsync; between statements the connection carries the debuggee's
        // value (composition.RowCount), which this line neutralizes for the copy.
        var applyRowCount = composition.RowCount != "0";
        if (applyRowCount)
        {
            Line("SET ROWCOUNT 0;");
        }

        if (composition.HealthyPrefixDdl is { Count: > 0 } healing)
        {
            // M4 (D8/C25): heal rollback-destroyed table-variable realizations while
            // the connection is still transactionless — before any redoom below.
            foreach (var ddl in healing)
            {
                AppendMultilineThenNewline(ddl);
            }
        }

        if (composition.RedoomTrancount is { } redoomTrancount)
        {
            AppendRedoomPrefix(Line, nonce, redoomTrancount, restoreXactAbortOn: composition.XactAbortOn);
        }

        if (composition.Rematerialize is not null || composition.DebuggerInitiated)
        {
            // Fact 19 (verified): RAISERROR is NOT exempt from XACT_ABORT — without
            // this, the §10.7 re-raise (or a faulting debugger-initiated eval) would
            // doom a healthy transaction as a debugger artifact whenever the debuggee
            // runs with XACT_ABORT ON (p05's exact configuration).
            Line("SET XACT_ABORT OFF;");
        }

        var declareParts = new List<string>();
        foreach (var v in variables)
        {
            declareParts.Add($"{v.Declaration.Name} {v.Declaration.DataTypeSql}");
        }

        declareParts.Add($"{RcVar(nonce)} int");
        declareParts.Add($"{ErrVar(nonce)} int");
        declareParts.Add($"{ScopeIdVar(nonce)} numeric(38,0)");
        foreach (var kind in requiredShadows)
        {
            declareParts.Add($"{ctx.ShadowVariable(kind)} {ShadowSqlType(kind)} = {shadowValues.Literal(kind)}");
        }

        Line("DECLARE " + string.Join(", ", declareParts) + ";");

        List<BatchParameter>? parameters = null;
        if (variables.Count > 0)
        {
            if (doomedSeed is null)
            {
                var reads = variables.Select(v => $"{v.Declaration.Name} = {StateTableIdentifiers.ColumnName(v.Declaration.Name)}");
                Line($"SELECT {string.Join(", ", reads)} FROM {table};");
            }
            else
            {
                // §10.4 doomed mode: postamble writes are impossible (3930), so the
                // table went stale the moment the transaction doomed — values ride
                // parameters from the session's binary snapshot instead. CONVERT to
                // the declared type keeps precision/scale/collation exact.
                parameters = new List<BatchParameter>(variables.Count);
                var seeds = new List<string>(variables.Count);
                for (var i = 0; i < variables.Count; i++)
                {
                    var v = variables[i];
                    var parameterName = SeedParam(nonce, v.Ordinal);
                    parameters.Add(new BatchParameter(parameterName, doomedSeed[i]));
                    // A59 (§8.1): CONVERT takes the STORAGE type — the engine refuses a
                    // CONVERT to a user-defined alias type outright (fact 34b, msg 243).
                    seeds.Add($"{v.Declaration.Name} = CONVERT({v.Declaration.StorageType}, {parameterName})");
                }

                Line($"SELECT {string.Join(", ", seeds)};");
            }
        }

        AppendTvpMaterialization(Line, tvpArguments);

        if (composition.BoostSeq is { } boostSeq)
        {
            // §14/A21 boost prologue (design note B4, verbatim), BEFORE the oracle so
            // a prologue fault surfaces as no-control-row (B7's recovery path). With
            // the F1 session-init creation the CREATE/INSERT branches are
            // defense-in-depth only (reachable iff the debuggee dropped the table —
            // the same accepted class as #__dbg_s{n} collisions); the residual of the
            // INSERT branch, when reached, is fact 26d's SCOPE_IDENTITY clobber —
            // recorded in docs/archive/reviews/m6-boost-core-fable.md §2-F1. The normal path
            // is the UPDATE reset, which is intrinsic-neutral (fact 26a).
            Line(BoostGuardCreateLine());
            Line(BoostGuardSeedLine(boostSeq));
            Line($"ELSE UPDATE {StateTableIdentifiers.BoostPositionTable} SET seq = {boostSeq}, pos = -1;");
        }

        if (!composition.OracleFree)
        {
            Line("BEGIN TRY");
        }

        int b;
        if (composition.Rematerialize is { } context)
        {
            // §10.7 re-materialization: re-raise the active context server-side and run
            // the user statement inside a REAL inner CATCH region, so ERROR_*() readers
            // the rewriter cannot reach (stepped-over modules, dynamic SQL, UDFs) see
            // native values. Residual infidelity = caveat C21 (number reads 50000,
            // procedure/line reflect the wrapper). RAISERROR over THROW: preserves
            // severity ≤ 18 and arbitrary state (fact 19: state 0 passes through; sev
            // cap 18 mandatory — 2754 otherwise; %% escaping required even for
            // variable-carried messages; >2047 truncates natively anyway).
            Line("BEGIN TRY");
            Line($"    RAISERROR({RematerializeMessageLiteral(context.Message)}, {Math.Min(context.Severity, 18)}, {context.State});");
            Line("END TRY");
            Line("BEGIN CATCH");
            if (composition.XactAbortOn)
            {
                Line("SET XACT_ABORT ON;");                    // restore BEFORE the user statement
            }

            if (applyRowCount)
            {
                Line($"SET ROWCOUNT {composition.RowCount};");   // C13: the debuggee's limit, for the user statement only
            }

            b = lineCount + 1;
            AppendMultilineThenNewline(patchedStatementText);
            Line($";SELECT {RcVar(nonce)} = @@ROWCOUNT, {ErrVar(nonce)} = @@ERROR;");
            Line($"SET {ScopeIdVar(nonce)} = SCOPE_IDENTITY();");
            Line("END CATCH");
        }
        else
        {
            if (applyRowCount)
            {
                Line($"SET ROWCOUNT {composition.RowCount};");   // C13: the debuggee's limit, for the user statement only
            }

            b = lineCount + 1;
            AppendMultilineThenNewline(patchedStatementText);
            Line($";SELECT {RcVar(nonce)} = @@ROWCOUNT, {ErrVar(nonce)} = @@ERROR;");
            Line($"SET {ScopeIdVar(nonce)} = SCOPE_IDENTITY();");
        }

        // C13 (§11.2): restore the connection to its RESTING unlimited state after capturing the
        // user statement's @@ROWCOUNT/@@ERROR/SCOPE_IDENTITY — so all bookkeeping BETWEEN debuggee
        // statements (the trailing state write here, then the next GO-boundary rebuild, push,
        // inspection read, or watch eval) runs unlimited without its own wrap. Fires when a limit
        // was applied, or when this statement is the debuggee's own SET ROWCOUNT (which set a
        // non-zero limit the tracker now holds for the next statement to re-apply).
        if (applyRowCount || composition.ResetRowCountAfterStatement)
        {
            Line("SET ROWCOUNT 0;");
        }

        if (composition.IncludeStateWrite && variables.Count > 0)
        {
            // The XACT_STATE() guard on the SUCCESS path too (not only in CATCH): a
            // read-only debuggee statement succeeds inside a doomed transaction (fact
            // 5), and without the guard the debugger's own bookkeeping write would
            // fault the batch with 3930 — an infidelity manufactured by us.
            Line($"IF XACT_STATE() <> -1 AND OBJECT_ID('tempdb..{table}') IS NOT NULL");
            Line($"    {BuildStateWrite(table, variables)}");
        }

        Line(BuildControlSelectOk(nonce, variables, includeErrAfter: composition.OracleFree));
        if (composition.IncludeStateSnapshot && variables.Count > 0)
        {
            Line(BuildStateSnapshotSelect(variables));
        }

        if (!composition.OracleFree)
        {
            Line("END TRY");
            Line("BEGIN CATCH");
            Line(BuildControlSelectCatch(variables, includeScopeIdentity: composition.BoostSeq is not null));
            if (composition.IncludeStateSnapshot && variables.Count > 0)
            {
                // CATCH-path snapshot carries the pre-statement values (assignments inside
                // the failed statement did not complete — statement-level rollback), same
                // semantics as the guarded CATCH write below.
                Line(BuildStateSnapshotSelect(variables));
            }

            if (composition.IncludeStateWrite && variables.Count > 0)
            {
                Line($"IF XACT_STATE() <> -1 AND OBJECT_ID('tempdb..{table}') IS NOT NULL");
                Line($"    {BuildStateWrite(table, variables)}");
            }

            Line("END CATCH");
        }
        if (composition.DebuggerInitiated && composition.XactAbortOn)
        {
            Line("SET XACT_ABORT ON;");                        // restore the debuggee's setting
        }

        return new ComposedBatch(sb.ToString(), frame.Ordinal, b, requiredShadows, parameters)
        {
            MovesIdentityChain = tvpArguments is not null && MovesIdentityChain(tvpArguments),
        };
    }

    // A59 (§9): this statement passes a table-type variable as a TVP argument. A #temp cannot
    // BE a TVP argument and only a variable of the type can — so declare the real thing and
    // fill it from the realization, which stays the single source of truth (a TVP formal is
    // READONLY: nothing to copy back). IDENTITY/computed columns cannot be supplied to a table
    // variable (fact 34e: SET IDENTITY_INSERT is a syntax error on one), hence C28's
    // regenerated identity values.
    //
    // The ORDER BY is what makes C28's promise — "rows inserted contiguously reproduce their
    // identity values exactly" — a GUARANTEE and not an accident of the plan: identity is
    // assigned in insert order, and INSERT…SELECT only fixes that order under an explicit
    // ORDER BY. Ordering by the realization's own identity column replays the original
    // insertion order, so a gap-free source reproduces its values byte-for-byte and a gapped
    // one at least closes its gaps deterministically (C28's documented divergence) instead of
    // reshuffling the rows.
    //
    // ONE source, shared by Build and BuildForRepl — the A59 review's F1 was exactly this
    // block existing in two places and being wired into only two of seven callers.
    private static void AppendTvpMaterialization(Action<string> emitLine, IReadOnlyList<TableTypeVariable>? tvpArguments)
    {
        foreach (var tvp in tvpArguments ?? Array.Empty<TableTypeVariable>())
        {
            emitLine($"DECLARE {tvp.Name} {tvp.Type.QualifiedName};");
            if (tvp.InsertableColumns.Count == 0)
            {
                continue;                    // every column IDENTITY/computed: nothing to supply
            }

            var columns = string.Join(", ", tvp.InsertableColumns.Select(TableTypeDefinition.Bracket));
            var order = tvp.IdentityColumn is { } identity
                ? $" ORDER BY {TableTypeDefinition.Bracket(identity)}"
                : string.Empty;
            emitLine($"INSERT INTO {tvp.Name} ({columns}) SELECT {columns} FROM " +
                     $"{RewriteContext.BracketIdentifier(tvp.RealizationName)}{order};");
        }
    }

    // A62 (§11.3 step 2 / §9): seed a stepped-into frame's TVP-formal realization (#temp) from the
    // caller's table-type-variable realization (#temp). Both are the SAME table type, so the
    // insertable column list and identity column match — this is the v2 of AppendTvpMaterialization's
    // #temp → DECLARE @t copy, target-shifted to a #temp (the formal's realization). IDENTITY/computed
    // columns are excluded (InsertableColumns, fact 34e → C28) and the source rows are replayed in
    // identity order (the same load-bearing ORDER BY as A59 rider 2), so contiguous rows reproduce
    // their identity values. Returns null when every column is IDENTITY/computed (nothing to supply)
    // or the source has no insertable columns — the realization then stays empty.
    public static string? BuildTvpFormalSeed(TableTypeVariable target, string sourceRealizationName)
    {
        if (target.InsertableColumns.Count == 0)
        {
            return null;
        }

        var columns = string.Join(", ", target.InsertableColumns.Select(TableTypeDefinition.Bracket));
        var order = target.IdentityColumn is { } identity
            ? $" ORDER BY {TableTypeDefinition.Bracket(identity)}"
            : string.Empty;
        return $"INSERT INTO {RewriteContext.BracketIdentifier(target.RealizationName)} ({columns}) " +
               $"SELECT {columns} FROM {RewriteContext.BracketIdentifier(sourceRealizationName)}{order};";
    }

    /// <summary>
    /// A59 / engine fact 34h: inserting into a table variable that has an IDENTITY column moves
    /// the connection's identity chain — <c>SCOPE_IDENTITY()</c> and <c>@@IDENTITY</c> both
    /// (probed: an insert into a table variable overwrote a real table's SCOPE_IDENTITY). So a
    /// batch whose §9 preamble materializes such a TVP CANNOT have its post-statement
    /// SCOPE_IDENTITY() capture trusted: unless the user statement itself performed an identity
    /// insert (which lands last and is therefore native truth), the capture reads the
    /// debugger's own bookkeeping INSERT. The session poisons the §7.4/A26 scope chain when
    /// this is true — the same flag, and the same "shadow keeps its client-modeled value until
    /// an insert-family statement re-synchronizes" recovery, that a frame pop already uses.
    /// </summary>
    public static bool MovesIdentityChain(IReadOnlyList<TableTypeVariable> tvpArguments)
        => tvpArguments.Any(t => t.IdentityColumn is not null && t.InsertableColumns.Count > 0);

    private static int CountLines(string text) => text.Count(c => c == '\n') + 1;

    // §10.4 doom re-materialization (fact 22) — ONE source for the redoom prefix,
    // shared by Build and BuildForRepl (M6 R3 extraction; byte-identity pinned by
    // RedoomPrefixPinTests, written green against the pre-extraction duplicates).
    // The previous batch ended with the engine's forced 3998 rollback, so the
    // connection is transactionless here no matter what the debuggee observed last
    // step. Rebuild the doomed state for real: restore the logical trancount, then
    // doom via a caught error under XACT_ABORT ON (forced ON — doom can arise with
    // the frame env OFF too, e.g. a deadlock victim inside TRY), then restore per
    // call site: the debuggee shell restores the frame env; the REPL shell stays OFF
    // (its §12.3 DebuggerInitiated sandwich restores at batch end). Everything after
    // this prefix runs under authentic doomed semantics; the batch ends doomed again
    // (another absorbed 3998) unless the debuggee's own statement rolls back, which
    // is exactly the native exit.
    private static void AppendRedoomPrefix(Action<string> emitLine, string nonce, int redoomTrancount, bool restoreXactAbortOn)
    {
        if (redoomTrancount < 1)
        {
            throw new ArgumentException(
                $"RedoomTrancount = {redoomTrancount}: a doomed transaction implies trancount >= 1 (§10.4).");
        }

        emitLine("SET XACT_ABORT ON;");
        for (var i = 0; i < redoomTrancount; i++)
        {
            emitLine("BEGIN TRANSACTION;");
        }

        emitLine($"DECLARE @__dbg{nonce}_doom int;");
        emitLine($"BEGIN TRY SET @__dbg{nonce}_doom = 1/0; END TRY BEGIN CATCH END CATCH;");
        emitLine($"SET XACT_ABORT {(restoreXactAbortOn ? "ON" : "OFF")};");
    }

    private static string RcVar(string nonce) => $"@__dbg{nonce}_rc";
    private static string ErrVar(string nonce) => $"@__dbg{nonce}_err";
    private static string ScopeIdVar(string nonce) => $"@__dbg{nonce}_scopeid";
    private static string SeedParam(string nonce, int ordinal) => $"@__dbg{nonce}_p{ordinal}";

    // Fact 19 d/e: RAISERROR printf-formats its message even when carried by a
    // variable — escape % as %% client-side; truncate the RAW message at RAISERROR's
    // 2047 ceiling first (native messages cap there anyway, so nothing real is lost).
    private static string RematerializeMessageLiteral(string message)
    {
        var raw = message.Length > 2047 ? message[..2047] : message;
        return ShadowValues.SqlStringLiteral(raw.Replace("%", "%%"));
    }

    private static string ShadowSqlType(ShadowKind kind) => kind switch
    {
        ShadowKind.Rowcount => "int",
        ShadowKind.Error => "int",
        ShadowKind.ScopeIdentity => "numeric(38,0)",
        // R7 (§10.2/§7.3): mirror the engine's ERROR_*() return types.
        ShadowKind.ErrNumber or ShadowKind.ErrSeverity or ShadowKind.ErrState or ShadowKind.ErrLine => "int",
        ShadowKind.ErrProcedure => "nvarchar(128)",
        ShadowKind.ErrMessage => "nvarchar(4000)",
        _ => throw new NotSupportedException($"Unmapped shadow kind {kind}."),
    };

    private static string BuildStateWrite(string table, IReadOnlyList<VariableSlot> variables)
    {
        var assigns = variables.Select(v => $"{StateTableIdentifiers.ColumnName(v.Declaration.Name)}={v.Declaration.Name}");
        return $"UPDATE {table} SET {string.Join(", ", assigns)};";
    }

    // §8.1's binary snapshot, piggybacked (a SELECT of local variables is a plain read
    // — legal even in a doomed transaction, fact 5): the session reads these raw values
    // with the same reader as everything else, no extra round trip. Values are the
    // batch's FINAL variable values — exactly what the state write wrote (or, doomed,
    // what it would have written).
    private static string BuildStateSnapshotSelect(IReadOnlyList<VariableSlot> variables)
    {
        var parts = variables.Select(v => $"{v.Declaration.Name} AS {StateTableIdentifiers.ColumnName(v.Declaration.Name)}");
        return $"SELECT 1 AS __dbg_state, {string.Join(", ", parts)};";
    }

    private static string BuildControlSelectOk(string nonce, IReadOnlyList<VariableSlot> variables, bool includeErrAfter = false)
    {
        var display = DisplayProjection(variables);
        var displaySuffix = display.Length > 0 ? ",\n       " + display : "";
        // D5/A13: err_after (add-only, §7.3) rides only on oracle-free batches — under
        // absorption a "successful" batch can carry a non-zero post-EXEC @@ERROR
        // (fact 24 d); everywhere else the column stays absent and the parser reads null.
        var errAfterSuffix = includeErrAfter ? $", {ErrVar(nonce)} AS err_after" : "";
        return $"SELECT 1 AS __dbg_ctl, 1 AS ok,\n" +
               $"       {RcVar(nonce)} AS rc, {ScopeIdVar(nonce)} AS scope_identity{errAfterSuffix},\n" +
               $"       @@TRANCOUNT AS trancount, XACT_STATE() AS xact_state{displaySuffix};";
    }

    // M6 F2 (ruled 2026-07-07, docs/archive/reviews/m6-boost-core-fable.md §2): boosted CATCH
    // rows carry scope_identity — §7.3 add-only evolution, the err_after precedent.
    // Completed in-slice identity inserts must reach the R6 shadow at fault time
    // (interpreted mode's pre-statement shadow is native-current; a boosted batch's
    // pre-BATCH shadow is not), and SCOPE_IDENTITY() survives into CATCH (fact 26d).
    // Non-boosted batches keep the column absent — the parser reads null either way.
    private static string BuildControlSelectCatch(IReadOnlyList<VariableSlot> variables, bool includeScopeIdentity = false)
    {
        var display = DisplayProjection(variables);
        var displaySuffix = display.Length > 0 ? ",\n       " + display : "";
        var scopeIdentitySuffix = includeScopeIdentity ? ", SCOPE_IDENTITY() AS scope_identity" : "";
        return "SELECT 1 AS __dbg_ctl, 0 AS ok,\n" +
               "       ERROR_NUMBER() AS err_number, ERROR_SEVERITY() AS err_severity,\n" +
               "       ERROR_STATE() AS err_state, ERROR_LINE() AS err_line,\n" +
               "       ERROR_PROCEDURE() AS err_procedure, ERROR_MESSAGE() AS err_message,\n" +
               $"       @@TRANCOUNT AS trancount, XACT_STATE() AS xact_state{scopeIdentitySuffix}{displaySuffix};";
    }

    // DESIGN §7.5: display projection, one pair of columns per live variable.
    private static string DisplayProjection(IReadOnlyList<VariableSlot> variables)
    {
        var parts = new List<string>();
        for (var i = 0; i < variables.Count; i++)
        {
            var name = variables[i].Declaration.Name;
            parts.Add($"LEFT(CONVERT(nvarchar(4000), {name}, 121), 256) AS v_{i}, " +
                      $"CASE WHEN {name} IS NULL THEN 1 ELSE 0 END AS v_{i}_isnull");
        }

        return string.Join(",\n       ", parts);
    }
}
