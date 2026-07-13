// DESIGN §7.4 (opening paragraph) — the span-patch engine core. Phase-0 reference
// implementation (Fable); gated to Fable/Opus per CLAUDE.md. Individual rules (R5, R6, …)
// are Sonnet work ON TOP of this engine — never a bespoke patch applier or string replace.
//
// The load-bearing invariants, enforced by construction:
//   1. AST-sourced spans only: SpanPatchCollector.Add takes a TSqlFragment, never a raw
//      offset — string literals, [bracketed identifiers] and comments are safe
//      STRUCTURALLY (they parse as different node kinds / token trivia), not by string
//      matching. (§7.4 R8-negative, §20.1.)
//   2. Overlap is a hard assertion failure identifying both rules (§7.4).
//   3. Patches apply right-to-left so earlier offsets are unaffected by length changes.
//   4. Every patch must fall inside the slice being rewritten (§5.3 slices).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace TsqlDbg.Core.Rewrite;

/// <summary>Rewrite-rule identities per the DESIGN §7.4 catalog (Test = unit-test fakes;
/// Boost = §14/A21 bookkeeping insertions — not a reference-rewrite rule, but its patches
/// ride the same collector/applier so overlap with R1–R3 stays a hard assert).</summary>
public enum RuleId { R1, R2, R3, R4, R5, R6, R7, R8, Boost, Test }

/// <summary>One replacement, in FULL-SCRIPT coordinates (§5.3).</summary>
public sealed record SpanPatch(int StartOffset, int Length, string NewText, RuleId Rule, string SourceDescription)
{
    public int EndOffset => StartOffset + Length;
}

public sealed class SpanOverlapException : Exception
{
    public SpanOverlapException(SpanPatch a, SpanPatch b)
        : base("Span-patch overlap — hard assertion failure (DESIGN §7.4): " +
               $"[{a.Rule}] {a.SourceDescription} @[{a.StartOffset},{a.EndOffset}) overlaps " +
               $"[{b.Rule}] {b.SourceDescription} @[{b.StartOffset},{b.EndOffset}). " +
               "Two rules patched the same source region (or one rule fired twice).") { }
}

public sealed class SpanOutOfSliceException : Exception
{
    public SpanOutOfSliceException(SpanPatch p, int sliceStart, int sliceLength)
        : base($"[{p.Rule}] {p.SourceDescription} @[{p.StartOffset},{p.EndOffset}) lies outside the slice " +
               $"[{sliceStart},{sliceStart + sliceLength}) being rewritten — the rule visited a node " +
               "outside the statement unit (DESIGN §7.4: rules run over the SU's subtree only).") { }
}

/// <summary>
/// Collects patches for one rewrite pass. The only way to add a patch is from a real,
/// source-positioned AST node — invariant 1 above.
/// </summary>
public sealed class SpanPatchCollector
{
    private readonly List<SpanPatch> _patches = new();
    private readonly RuleId _rule;

    public SpanPatchCollector(RuleId rule) => _rule = rule;

    public IReadOnlyList<SpanPatch> Patches => _patches;

    /// <summary>Replace <paramref name="sourceNode"/>'s exact source text with <paramref name="newText"/> (empty = delete).</summary>
    public void Add(TSqlFragment sourceNode, string newText)
    {
        ArgumentNullException.ThrowIfNull(sourceNode);
        ArgumentNullException.ThrowIfNull(newText);
        if (sourceNode.StartOffset < 0 || sourceNode.FragmentLength <= 0)
            throw new InvalidOperationException(
                $"[{_rule}] tried to patch a fragment without a source position ({sourceNode.GetType().Name}, " +
                $"StartOffset={sourceNode.StartOffset}, FragmentLength={sourceNode.FragmentLength}). " +
                "Synthesized nodes cannot be span-patched — DESIGN §7.4 invariant 1.");
        _patches.Add(new SpanPatch(
            sourceNode.StartOffset, sourceNode.FragmentLength, newText, _rule,
            sourceNode.GetType().Name));
    }

    /// <summary>
    /// M6 (§14/A21): zero-length insertion immediately AFTER <paramref name="sourceNode"/>'s
    /// last source character — the boost bookkeeping-marker patch class. Same-line by
    /// contract: <paramref name="text"/> must contain no line breaks, so the slice's line
    /// arithmetic is untouched and the §10.2 mapping formula
    /// <c>real = SU.StartLine + (err_line − B)</c> holds unchanged. Still AST-sourced
    /// (invariant 1): the offset comes from a positioned fragment, never a raw number.
    /// Zero-length spans compose with R1–R3 replacements under the existing overlap
    /// assert — an insertion strictly INSIDE a replaced span still throws
    /// <see cref="SpanOverlapException"/>; boundary adjacency is legal.
    /// </summary>
    public void AddInsertionAfter(TSqlFragment sourceNode, string text)
    {
        ArgumentNullException.ThrowIfNull(sourceNode);
        ArgumentNullException.ThrowIfNull(text);
        if (sourceNode.StartOffset < 0 || sourceNode.FragmentLength <= 0)
            throw new InvalidOperationException(
                $"[{_rule}] tried to insert after a fragment without a source position ({sourceNode.GetType().Name}). " +
                "Synthesized nodes cannot anchor insertions — DESIGN §7.4 invariant 1.");
        if (text.Contains('\n') || text.Contains('\r'))
            throw new InvalidOperationException(
                $"[{_rule}] insertion text contains a line break — insertions must be line-neutral " +
                "(§14/A21: err_line arithmetic depends on the slice's line count being unchanged).");
        _patches.Add(new SpanPatch(
            sourceNode.StartOffset + sourceNode.FragmentLength, 0, text, _rule,
            sourceNode.GetType().Name + "+after"));
    }
}

/// <summary>Validates and applies a patch set to one source slice. DESIGN §7.4.</summary>
public static class SpanPatcher
{
    /// <summary>
    /// Applies <paramref name="patches"/> (full-script coordinates) to the slice
    /// [<paramref name="sliceStart"/>, <paramref name="sliceStart"/>+<paramref name="sliceLength"/>)
    /// of <paramref name="fullScript"/>, returning the patched slice text.
    /// Throws <see cref="SpanOverlapException"/> / <see cref="SpanOutOfSliceException"/>.
    /// </summary>
    public static string Apply(string fullScript, int sliceStart, int sliceLength, IReadOnlyList<SpanPatch> patches)
    {
        ArgumentNullException.ThrowIfNull(fullScript);
        if (sliceStart < 0 || sliceLength < 0 || sliceStart + sliceLength > fullScript.Length)
            throw new ArgumentOutOfRangeException(nameof(sliceStart), "Slice outside script bounds.");

        if (patches.Count == 0)
            return fullScript.Substring(sliceStart, sliceLength);

        var ordered = patches.OrderBy(p => p.StartOffset).ThenBy(p => p.Length).ToList();

        for (int i = 1; i < ordered.Count; i++)                      // invariant 2: hard overlap assert
            if (ordered[i - 1].EndOffset > ordered[i].StartOffset)
                throw new SpanOverlapException(ordered[i - 1], ordered[i]);

        foreach (var p in ordered)                                   // invariant 4: slice bounds
            if (p.StartOffset < sliceStart || p.EndOffset > sliceStart + sliceLength)
                throw new SpanOutOfSliceException(p, sliceStart, sliceLength);

        var sb = new StringBuilder(fullScript.Substring(sliceStart, sliceLength));
        for (int i = ordered.Count - 1; i >= 0; i--)                 // invariant 3: right-to-left
        {
            var p = ordered[i];
            int rel = p.StartOffset - sliceStart;
            sb.Remove(rel, p.Length);
            sb.Insert(rel, p.NewText);
        }
        return sb.ToString();
    }
}
