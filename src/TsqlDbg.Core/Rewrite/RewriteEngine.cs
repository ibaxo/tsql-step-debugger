// DESIGN §7.4 — rule contract + engine, and the shadow-variable NAMING CONTRACT shared
// with the §7.1 composed-batch builder. Phase-0 reference implementation (Fable).
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace TsqlDbg.Core.Rewrite;

/// <summary>
/// Shadow intrinsics the rewriter can substitute (§7.4 R4–R7). The composed-batch builder
/// declares/assigns ONLY the shadows a statement's patches actually reference (§7.4:
/// "appended to the preamble … only when the statement's spans reference them").
/// Assignment source: the adapter's last observed values — rc / scope_identity from the
/// previous control row (§7.3), err likewise; Err* fields come from the active
/// ErrorContext (§10.2, M3).
/// </summary>
public enum ShadowKind
{
    Rowcount,        // R4  @@ROWCOUNT
    Error,           // R5  @@ERROR
    ScopeIdentity,   // R6  SCOPE_IDENTITY()
    // M3 (R7): committed shape — populated from the active ErrorContext (§10.2/§10.7).
    ErrNumber, ErrSeverity, ErrState, ErrLine, ErrProcedure, ErrMessage,
}

/// <summary>
/// M4 (§9, R1–R3): the frame-chain name scope the rewrite rules resolve temp-object
/// references through. Implemented by the session over its FrameStack (innermost match
/// wins — engine name resolution, §9); rules stay stateless. A REFERENCE that resolves
/// nowhere stays unpatched — faithful by construction: the server then raises exactly
/// the not-found error class the native run would (208/1087/16916).
/// </summary>
public interface ITempNameScope
{
    /// <summary>The frame ordinal new objects are created under (physical-name minting).</summary>
    int CurrentFrameOrdinal { get; }

    /// <summary>Chain lookup for a reference; null = no live object of that kind/name.</summary>
    string? ResolveReference(string originalName, Interpreter.TempObjectKind kind);

    /// <summary>A20 (§7.4 R2, ratified 2026-07-06): true iff a LIVE user-#temp registry
    /// entry in a frame OUTER to the creating one already claims this original name. R2
    /// renames a CREATE target ONLY then (the flattened connection cannot hold two
    /// same-named session-scoped temps, where native nested proc scopes can); all other
    /// creates — every frame-0 create AND a same-frame duplicate CREATE — keep their
    /// original physical names: originals are what make a STEPPED-OVER callee's compiled
    /// body see caller temps natively (docs/archive/reviews/m5-d5-oracle-free-exec-fable.md §4),
    /// and a same-frame duplicate must keep its name so the server raises the native
    /// 2714 (M5 gate finding E1 — a live same-frame entry is NOT a rename trigger).
    /// Default false = no collision, keep the original name.</summary>
    bool HasLiveTempTable(string originalName) => false;
}

/// <summary>
/// Per-session rewrite context. Centralizes __dbg identifier naming so rules and the
/// §7.1 builder can never disagree (CLAUDE.md rule 4: all __dbg identifiers carry the
/// session nonce).
/// </summary>
public sealed record RewriteContext
{
    private static readonly Regex NoncePattern = new("^[A-Za-z0-9]{1,16}$", RegexOptions.Compiled);

    public string SessionNonce { get; }

    /// <summary>
    /// §7.4 R7's activation switch: true while the session's ErrorContextStack is
    /// non-empty (§10.2 dynamic extent — cursor inside a CATCH block). The session
    /// flips it on every context push/pop; rules read it per Rewrite call. Mutable
    /// shared state is safe under the §3 single-threaded serialization contract.
    /// </summary>
    public bool ErrorContextActive { get; set; }

    /// <summary>M4 (§9): the frame-chain scope R1–R3 resolve names through. Null keeps
    /// those rules silent (pre-M4 behavior; also the state before InitializeAsync).
    /// Same single-threaded mutability contract as <see cref="ErrorContextActive"/>.</summary>
    public ITempNameScope? TempNames { get; set; }

    // ---- §9 physical-name minting (D7) — one place so rules, registry recording, and
    // ---- re-creation DDL can never disagree. Names are raw (unbracketed); emit via
    // ---- BracketIdentifier when splicing into SQL.

    public static string TempTablePhysicalName(string originalName, int frameOrdinal)
        => $"{originalName}__f{frameOrdinal}";                       // "#work" → "#work__f2" (R2)

    public static string TableVariablePhysicalName(string originalName, int frameOrdinal)
        => $"#__dbgtv_{frameOrdinal}_{originalName.TrimStart('@')}"; // "@t" → "#__dbgtv_2_t" (R1)

    public static string CursorPhysicalName(string originalName, int frameOrdinal)
        => $"{originalName}__f{frameOrdinal}_c";                     // "cur" → "cur__f2_c" (R3)

    /// <summary>Bracket-quotes an identifier for splicing into patched SQL (]]-escape).</summary>
    public static string BracketIdentifier(string rawName)
        => "[" + rawName.Replace("]", "]]") + "]";

    public RewriteContext(string sessionNonce)
    {
        if (!NoncePattern.IsMatch(sessionNonce))
            throw new ArgumentException("Session nonce must be 1-16 alphanumerics (it is spliced into T-SQL identifiers).", nameof(sessionNonce));
        SessionNonce = sessionNonce;
    }

    /// <summary>The substitute variable a rewritten reference reads (distinct from the §7.1 CAPTURE variables like @__dbg{n}_rc).</summary>
    public string ShadowVariable(ShadowKind kind) => kind switch
    {
        ShadowKind.Rowcount      => $"@__dbg{SessionNonce}_sh_rowcount",
        ShadowKind.Error         => $"@__dbg{SessionNonce}_sh_error",
        ShadowKind.ScopeIdentity => $"@__dbg{SessionNonce}_sh_scopeid",
        ShadowKind.ErrNumber     => $"@__dbg{SessionNonce}_sh_err_number",
        ShadowKind.ErrSeverity   => $"@__dbg{SessionNonce}_sh_err_severity",
        ShadowKind.ErrState      => $"@__dbg{SessionNonce}_sh_err_state",
        ShadowKind.ErrLine       => $"@__dbg{SessionNonce}_sh_err_line",
        ShadowKind.ErrProcedure  => $"@__dbg{SessionNonce}_sh_err_procedure",
        ShadowKind.ErrMessage    => $"@__dbg{SessionNonce}_sh_err_message",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}

/// <summary>The result the §7.1 builder consumes.</summary>
public sealed record RewriteResult(
    string PatchedText,
    IReadOnlySet<ShadowKind> RequiredShadows,
    IReadOnlyList<SpanPatch> Patches);

/// <summary>
/// One rewrite rule (§7.4 catalog row). Implementations visit the statement's subtree
/// with a <see cref="TSqlFragmentVisitor"/> and report patches + required shadows.
/// Rules must be stateless/reusable across statements and sessions.
/// </summary>
public interface IRewriteRule
{
    RuleId Id { get; }
    void Collect(TSqlFragment statement, RewriteContext context, SpanPatchCollector patches, ISet<ShadowKind> requiredShadows);
}

/// <summary>
/// Runs the registered rules over one statement unit's subtree and applies the combined
/// patch set to its original source slice (§5.3: original text, never generator output).
/// </summary>
public sealed class RewriteEngine
{
    private readonly IReadOnlyList<IRewriteRule> _rules;

    public RewriteEngine(IReadOnlyList<IRewriteRule> rules) => _rules = rules;

    /// <summary>Engine wired with the currently-shipped rules: R4-R6 (M1), R7 (M3;
    /// self-gating on <see cref="RewriteContext.ErrorContextActive"/>), R1-R3 (M4;
    /// self-gating on <see cref="RewriteContext.TempNames"/>).</summary>
    public static RewriteEngine CreateDefault() => new(new IRewriteRule[]
    {
        new Rules.R1TableVariableRule(),
        new Rules.R2TempTableRule(),
        new Rules.R3CursorRule(),
        new Rules.R4RowcountRule(),
        new Rules.R5ErrorRule(),
        new Rules.R6ScopeIdentityRule(),
        new Rules.R7ErrorFunctionsRule(),
    });

    /// <param name="extraPatches">M6 (§14/A21): additional pre-collected patches applied
    /// in the SAME <see cref="SpanPatcher.Apply"/> pass as the rules' — the boost marker
    /// insertions ride here, so overlap between a marker and an R1–R3 replacement stays
    /// the §7.4 hard assert instead of a silent second-pass corruption.</param>
    public RewriteResult Rewrite(
        TSqlFragment statementUnit, string fullScript, RewriteContext context,
        IReadOnlyList<SpanPatch>? extraPatches = null)
    {
        ArgumentNullException.ThrowIfNull(statementUnit);
        if (statementUnit.StartOffset < 0 || statementUnit.FragmentLength <= 0)
            throw new InvalidOperationException("Statement unit has no source position — cannot rewrite (§5.3).");

        var allPatches = new List<SpanPatch>();
        var shadows = new HashSet<ShadowKind>();
        foreach (var rule in _rules)
        {
            var collector = new SpanPatchCollector(rule.Id);
            rule.Collect(statementUnit, context, collector, shadows);
            allPatches.AddRange(collector.Patches);
        }

        if (extraPatches is not null)
        {
            allPatches.AddRange(extraPatches);
        }

        string patched = SpanPatcher.Apply(fullScript, statementUnit.StartOffset, statementUnit.FragmentLength, allPatches);
        return new RewriteResult(patched, shadows, allPatches);
    }
}
