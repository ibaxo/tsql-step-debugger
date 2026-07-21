// DESIGN §5.1–§5.3, §13 — statement units, byte-exact source spans, breakpoint mapping,
// and DECLARE extraction (feeds §8 state-table DDL and §7.2 synthetic init).
// Phase-0 reference implementation (Fable); M2 (Fable): the index flattens into IF/WHILE
// bodies in source order and carries the frame's control-flow map (label paths).
using System;
using System.Collections.Generic;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace TsqlDbg.Core.Interpreter;

/// <summary>Byte-exact location of a fragment in the ORIGINAL parsed script (§5.3).</summary>
public sealed record SourceSpan(int StartOffset, int Length, int StartLine, int StartColumn, int EndLine)
{
    public static SourceSpan Of(TSqlFragment f)
    {
        if (f.StartOffset < 0 || f.FragmentLength <= 0)
            throw new InvalidOperationException(
                $"Fragment {f.GetType().Name} has no source position (synthesized?) — §5.3 requires original slices only.");
        int endLine = f.StartLine;
        // Last token's line gives the end line; guard for defensive robustness.
        if (f.ScriptTokenStream is { } ts && f.LastTokenIndex >= 0 && f.LastTokenIndex < ts.Count)
            endLine = Math.Max(endLine, ts[f.LastTokenIndex].Line);
        return new SourceSpan(f.StartOffset, f.FragmentLength, f.StartLine, f.StartColumn, endLine);
    }
}

/// <summary>
/// One stoppable statement unit: an Executable, Declare, or Control leaf (§5.1/§6).
/// Structural nodes (BEGIN…END) and labels never become units — the cursor passes
/// through them without a stop (labels: fact 11/13 no-op semantics).
/// </summary>
public sealed class StatementUnit
{
    public int Ordinal { get; }                 // stable id within the frame body (trace, DAP); source order
    public TSqlStatement Fragment { get; }
    public SuKind Kind { get; }
    public SuSubKind SubKind { get; }
    public SourceSpan Span { get; }
    public string Text { get; }                 // byte-exact original slice (§5.3)

    /// <summary>A72 (§13): the last line a breakpoint/Run-to-Cursor request binds INTO
    /// this unit from. Leaf units: the span's end line (any line of a multi-line
    /// statement binds to it). IF/WHILE: the PREDICATE's end line — their fragment
    /// span swallows the whole body, and §13's fall-forward behavior (body lines bind
    /// to the units inside, END lines to the unit after the block) must survive
    /// containment mapping.</summary>
    public int BindEndLine { get; }

    internal StatementUnit(int ordinal, TSqlStatement fragment, ClassificationResult c, string fullScript)
    {
        Ordinal = ordinal;
        Fragment = fragment;
        Kind = c.Kind;
        SubKind = c.SubKind;
        Span = SourceSpan.Of(fragment);
        Text = fullScript.Substring(Span.StartOffset, Span.Length);
        BindEndLine = fragment switch
        {
            IfStatement { Predicate: { } p } => PredicateEndLine(p, Span.StartLine),
            WhileStatement { Predicate: { } p } => PredicateEndLine(p, Span.StartLine),
            _ => Span.EndLine,
        };
    }

    private static int PredicateEndLine(TSqlFragment predicate, int fallback)
        => predicate.StartOffset >= 0 && predicate.FragmentLength > 0
            ? SourceSpan.Of(predicate).EndLine
            : fallback;

    public override string ToString() => $"SU#{Ordinal} {Kind}/{SubKind} @L{Span.StartLine}: {Truncate(Text)}";
    private static string Truncate(string s) => s.Length <= 60 ? s : s[..57] + "...";
}

/// <summary>
/// Flat index of every stoppable unit in a frame body, in SOURCE order (depth-first
/// through interpreter scopes — a unit inside an IF branch or WHILE body sits between
/// its neighbors exactly as written, which is what §13 breakpoint mapping needs; the
/// cursor's dynamic visit order differs, and looks units up by fragment identity).
/// Serves: §13 breakpoint mapping, --trace SU listing, upfront validation, and the
/// frame's label map (§6, via <see cref="ControlFlow"/>). Line-number ground truth:
/// line 1 = line 1 of the parsed definition (§5.2), matching ERROR_LINE and XE
/// line_number conventions.
/// </summary>
public sealed class StatementIndex
{
    private readonly List<StatementUnit> _units;
    private readonly Dictionary<TSqlStatement, StatementUnit> _byFragment;

    public string FullScript { get; }
    public IReadOnlyList<StatementUnit> All => _units;
    public int Count => _units.Count;

    /// <summary>Label map + lexical paths (§6) — drives GOTO / Jump-to-Cursor reconstruction.</summary>
    public ControlFlowMap ControlFlow { get; }

    private StatementIndex(
        List<StatementUnit> units, Dictionary<TSqlStatement, StatementUnit> byFragment,
        string fullScript, ControlFlowMap controlFlow)
    {
        _units = units;
        _byFragment = byFragment;
        FullScript = fullScript;
        ControlFlow = controlFlow;
    }

    /// <summary>
    /// Validates then indexes <paramref name="body"/>. Validation order (M2 decision):
    /// engine-parity compile diagnostics first (<see cref="ParseTimeDiagnosticException"/>
    /// — natively this code could never start executing, facts 13/14), then milestone
    /// gates (<see cref="MilestoneNotSupportedException"/> — the code is runnable but the
    /// debugger doesn't support it yet). DESIGN §6: validate at launch, never mid-step.
    /// </summary>
    public static StatementIndex Build(
        IList<TSqlStatement> body, string fullScript,
        FrameKind frameKind = FrameKind.Procedure, IEnumerable<string>? preDeclaredVariables = null)
    {
        var controlFlow = ControlFlowMap.BuildAndValidate(
            body, frameKind, preDeclaredVariables ?? Array.Empty<string>());
        MilestoneValidator.ValidateOrThrow(body);

        var units = new List<StatementUnit>();
        var byFragment = new Dictionary<TSqlStatement, StatementUnit>(ReferenceEqualityComparer.Instance);
        void Walk(IList<TSqlStatement> statements)
        {
            foreach (var s in statements)
            {
                var c = SuClassifier.Classify(s);
                switch (c.Kind)
                {
                    case SuKind.Executable:
                    case SuKind.Declare:
                    case SuKind.Control:
                        var unit = new StatementUnit(units.Count, s, c, fullScript);
                        units.Add(unit);
                        byFragment.Add(s, unit);
                        break;
                    case SuKind.Structural:
                    case SuKind.Label:
                        break;                             // no unit; Structural descends below
                    case SuKind.Unsupported:
                        // Unreachable: ValidateOrThrow already refused. Keep a hard assert.
                        throw new InvalidOperationException($"Gated statement survived validation: {s.GetType().Name}");
                }

                foreach (var (_, children) in InterpreterScopes.ChildrenOf(s))
                    Walk(children);
            }
        }

        Walk(body);
        return new StatementIndex(units, byFragment, fullScript, controlFlow);
    }

    /// <summary>
    /// §13 mapping (A72): a requested line binds to the unit whose BINDABLE lines
    /// contain it — any line of a multi-line leaf statement binds to that statement
    /// (Run to Cursor on line 2 of a 3-line SELECT stops AT that SELECT, not at the
    /// unit after it); for IF/WHILE only the predicate's lines bind in (<see
    /// cref="StatementUnit.BindEndLine"/>), so lines inside their bodies keep binding
    /// to the units inside them, and END/label/blank lines keep binding FORWARD to the
    /// first unit whose StartLine ≥ line (the pre-A72 rule). When a line is both
    /// inside a containing unit and at-or-above a forward unit, the forward unit wins
    /// only if it starts within the containing unit's bindable span (statements
    /// sharing a line keep the forward tie). Returns false only when the line is past
    /// every unit's last bindable line (caller answers verified:false).
    /// </summary>
    public bool TryMapBreakpointLine(int requestedLine, out StatementUnit unit)
    {
        StatementUnit? containing = null;
        foreach (var u in _units)
        {
            if (u.Span.StartLine >= requestedLine)
            {
                unit = containing is not null && u.Span.StartLine > containing.BindEndLine ? containing : u;
                return true;
            }

            if (u.BindEndLine >= requestedLine)
            {
                containing = u;   // source order → the LAST match is the innermost
            }
        }

        if (containing is not null)
        {
            unit = containing;
            return true;
        }

        unit = null!;
        return false;
    }

    /// <summary>Unit lookup by AST fragment identity (cursor stops, jump targets).</summary>
    public bool TryGetUnit(TSqlStatement fragment, out StatementUnit unit)
        => _byFragment.TryGetValue(fragment, out unit!);
}

/// <summary>One declarator of a DECLARE statement, with byte-exact type/initializer slices (§8).</summary>
public sealed record VariableDeclaration(
    string Name,                       // normalized to include the leading '@'
    string DataTypeSql,                // exact source slice: length/precision/scale/collation preserved (§8.1)
    string? InitializerSql,            // exact source slice of the initializer expression, or null
    DeclareVariableElement Fragment)
{
    /// <summary>
    /// DESIGN §8.1 (A59): the type the debugger STORES the variable as, when that differs
    /// from the type the user DECLARED it as. Set only for a user-defined alias type, whose
    /// declared name is illegal both as a tempdb column (fact 34a, msg 2715) and as a
    /// CONVERT target (fact 34b, msg 243) — it is resolved to the alias's BASE type, bare.
    ///
    /// Resolved at FRAME INIT, not at parse time: only the §4 step-2a catalog can tell an
    /// alias type from a table type, and neither the parser nor ScriptDom can (fact 34).
    /// </summary>
    public string? StorageTypeSql { get; init; }

    /// <summary>
    /// DESIGN §8.1 (A59): the alias base type's collation, kept SEPARATE from
    /// <see cref="StorageTypeSql"/> because the two places the storage type is emitted do not
    /// accept the same string: a column definition takes <c>COLLATE</c>, and a
    /// <c>CONVERT</c> target does NOT (`CONVERT(nvarchar(50) COLLATE …, @p)` is msg 156,
    /// "Incorrect syntax near the keyword 'COLLATE'"). Null for non-character types.
    /// </summary>
    public string? StorageCollation { get; init; }

    /// <summary>
    /// The type to emit at every <c>CONVERT(&lt;type&gt;, @p)</c> site — the doomed seed, the REPL
    /// doomed seed, the §10.4 resurrection re-seed, and §8.3's setVariable UPDATE — and for
    /// §8.3's safe-literal-form test. Bare: no <c>COLLATE</c> (see <see cref="StorageCollation"/>).
    /// The composed batch's preamble DECLARE keeps <see cref="DataTypeSql"/> instead — the
    /// debuggee's own variable is the user's type, so execution stays native.
    /// </summary>
    public string StorageType => StorageTypeSql ?? DataTypeSql;

    /// <summary>
    /// The type to emit for the state table's COLUMN (§8.1) — the storage type plus its
    /// collation, so a user database whose collation differs from tempdb's cannot transcode
    /// a <c>varchar</c> value on the round trip.
    /// </summary>
    public string StorageColumnType =>
        StorageCollation is null ? StorageType : $"{StorageType} COLLATE {StorageCollation}";

    /// <summary>
    /// DESIGN §8.2 (A70): whether this declaration is an <c>OUTPUT</c> parameter of its frame's
    /// module — true for any procedure frame's OUTPUT formals (frame 0 and stepped-into callees
    /// alike; their <see cref="Fragment"/> is a <see cref="ProcedureParameter"/>), never for an
    /// ordinary DECLARE element. A70's consumers scope it themselves: the launch-time NULL seed
    /// reads frame 0's extraction only, and the §24.8 trace summary's <c>outputParams</c>
    /// projection reads <c>Frames[0]</c> only.
    /// </summary>
    public bool IsOutputParameter => Fragment is ProcedureParameter { Modifier: ParameterModifier.Output };

    /// <summary>Extracts all declarators of a DECLARE statement. DESIGN §7.2 / §8.2.</summary>
    public static IReadOnlyList<VariableDeclaration> Extract(DeclareVariableStatement declare, string fullScript)
    {
        var result = new List<VariableDeclaration>(declare.Declarations.Count);
        foreach (var d in declare.Declarations)
        {
            string raw = d.VariableName?.Value
                ?? throw new InvalidOperationException("DECLARE element without a variable name.");
            string name = raw.StartsWith('@') ? raw : "@" + raw;

            // A63: cursor variables (DECLARE @c CURSOR) parse as DeclareVariableStatement with a
            // CURSOR data type; they ARE extracted here (typeSql "CURSOR") but RegisterFrameVariablesAsync
            // skips them from scalar state (§9 reifies them as GLOBAL cursors). A null DataType is still
            // an internal invariant break — every DECLARE element carries one.
            var typeSpan = SourceSpan.Of(d.DataType
                ?? throw new InvalidOperationException($"DECLARE {name} without a data type — the parser guarantees one."));
            string typeSql = fullScript.Substring(typeSpan.StartOffset, typeSpan.Length);

            string? initSql = null;
            if (d.Value is { } init)
            {
                var s = SourceSpan.Of(init);
                initSql = fullScript.Substring(s.StartOffset, s.Length);
            }
            result.Add(new VariableDeclaration(name, typeSql, initSql, d));
        }
        return result;
    }
}
