// DESIGN §6 (label map, GOTO validation, parse-time refusal), §8.2 (parse-time
// diagnostics), §13 (Jump to Cursor reconstruction paths) — the lexical structure of a
// frame body: interpreter-scope walk, label/statement paths, and session-start
// engine-parity compile diagnostics.
// M2 cursor pass (Fable). Every mirrored engine rule was verified live:
// docs/engine-facts.md facts 11-14 (repro: docs/engine-facts/fact11..14*.sql).
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace TsqlDbg.Core.Interpreter;

/// <summary>
/// Whether frame 0 executes a procedure body or an ad-hoc script — a few engine compile
/// rules differ (RETURN-with-value is error 178 only outside a procedure, fact 13).
/// </summary>
public enum FrameKind { Procedure, Script }

/// <summary>
/// One engine-parity compile diagnostic: code the engine itself would refuse at batch
/// compile (Level 15/16, before executing anything — facts 13/14), mirrored at session
/// start per DESIGN §6/§8.2 ("reject at session start with a diagnostic").
/// </summary>
public sealed record ParseTimeDiagnostic(int Line, string Message);

/// <summary>
/// Thrown at cursor creation (session launch), before milestone gating. Most diagnostics are
/// genuine T-SQL compile errors (natively this code could never start executing, which outranks
/// "the debugger doesn't support X yet"); a few (A63 cursor-variable aliasing / OUTPUT params) are
/// debugger limitations reported the same way — the banner wording covers both.
/// </summary>
public sealed class ParseTimeDiagnosticException : Exception
{
    public IReadOnlyList<ParseTimeDiagnostic> Diagnostics { get; }

    public ParseTimeDiagnosticException(IReadOnlyList<ParseTimeDiagnostic> diagnostics)
        : base(BuildMessage(diagnostics)) => Diagnostics = diagnostics;

    private static string BuildMessage(IReadOnlyList<ParseTimeDiagnostic> diagnostics)
    {
        var lines = new List<string>
        {
            $"This code cannot be launched for debugging ({diagnostics.Count} problem(s) — a T-SQL compile error the engine would refuse, or a construct the debugger cannot model):",
        };
        foreach (var d in diagnostics)
            lines.Add($"  line {d.Line}: {d.Message}");
        return string.Join(Environment.NewLine, lines);
    }
}

/// <summary>Container kinds along a lexical path (the cursor's entry-stack vocabulary).</summary>
internal enum PathContainerKind { Root, Block, IfThen, IfElse, WhileBody, TryBlock, CatchBlock }

/// <summary>
/// One step of a lexical path from the frame-body root down to a statement:
/// <see cref="List"/>[<see cref="Index"/>] is the statement this step locates — the next
/// step's container, or the target itself for the last step. Converts 1:1 to a cursor
/// stack entry (ExecutionCursor's GOTO / Jump-to-Cursor reconstruction, §6/§13); sound
/// because the engine's control-flow position is purely lexical (fact 11).
/// </summary>
internal readonly record struct PathStep(PathContainerKind Kind, TSqlFragment? Owner, IList<TSqlStatement> List, int Index);

/// <summary>
/// The containers the interpreter descends — and the ONLY ones (§5.1/§6). Everything
/// else is a leaf; in particular module-creating DDL in a script frame
/// (CREATE PROCEDURE/FUNCTION/TRIGGER) is sent to the server whole, and its body is a
/// separate compile/label/variable scope (fact 13: label names are unique "within a
/// query batch or stored procedure"). TRY/CATCH descent exists so labels and gated
/// constructs inside it are visible to validation even while TryCatchStatement itself
/// is milestone-gated (M3).
/// </summary>
internal static class InterpreterScopes
{
    public static IEnumerable<(PathContainerKind Kind, IList<TSqlStatement> Statements)> ChildrenOf(TSqlStatement statement)
    {
        switch (statement)
        {
            case BeginEndBlockStatement block:
                yield return (PathContainerKind.Block, block.StatementList.Statements);
                break;
            case IfStatement ifStatement:
                if (ifStatement.ThenStatement is { } thenStatement)
                    yield return (PathContainerKind.IfThen, new TSqlStatement[] { thenStatement });
                if (ifStatement.ElseStatement is { } elseStatement)
                    yield return (PathContainerKind.IfElse, new TSqlStatement[] { elseStatement });
                break;
            case WhileStatement whileStatement:
                if (whileStatement.Statement is { } body)
                    yield return (PathContainerKind.WhileBody, new TSqlStatement[] { body });
                break;
            case TryCatchStatement tryCatch:
                yield return (PathContainerKind.TryBlock, tryCatch.TryStatements.Statements);
                yield return (PathContainerKind.CatchBlock, tryCatch.CatchStatements.Statements);
                break;
        }
    }
}

/// <summary>A declared label: its fragment and the lexical path to it (§6 label map).</summary>
internal sealed record LabelTarget(LabelStatement Fragment, IReadOnlyList<PathStep> Path);

/// <summary>
/// Label map + lexical paths + session-start control-flow diagnostics for one frame
/// body. Built once by <see cref="StatementIndex.Build"/>; the paths drive
/// ExecutionCursor's GOTO and Jump-to-Cursor reconstruction.
/// </summary>
public sealed class ControlFlowMap
{
    private readonly Dictionary<string, LabelTarget> _labels;
    private readonly Dictionary<TSqlStatement, IReadOnlyList<PathStep>> _paths;

    private ControlFlowMap(
        Dictionary<string, LabelTarget> labels,
        Dictionary<TSqlStatement, IReadOnlyList<PathStep>> paths)
    {
        _labels = labels;
        _paths = paths;
    }

    /// <summary>Label name → target; keys per <see cref="LabelKey"/> (case-insensitive, fact 13).</summary>
    internal IReadOnlyDictionary<string, LabelTarget> Labels => _labels;

    /// <summary>Lexical path of any statement in the walked body (reference identity).</summary>
    internal bool TryGetPath(TSqlStatement statement, out IReadOnlyList<PathStep> path)
        => _paths.TryGetValue(statement, out path!);

    /// <summary>
    /// Engine-parity label naming: <c>LabelStatement.Value</c> includes the trailing
    /// colon (verified against ScriptDom 180.37.3); <c>GoToStatement.LabelName.Value</c>
    /// does not. Matching is case-insensitive (fact 13 probe 5/9).
    /// </summary>
    internal static string LabelKey(string rawLabelText)
        => (rawLabelText.EndsWith(':') ? rawLabelText[..^1] : rawLabelText).Trim();

    /// <summary>
    /// Walks the body once (interpreter scopes only), producing the label map and every
    /// statement's lexical path, and throws <see cref="ParseTimeDiagnosticException"/>
    /// listing every engine-parity compile error (facts 13/14):
    /// duplicate label (132), GOTO to undeclared label (133), BREAK/CONTINUE outside
    /// WHILE (135/136), GOTO into a TRY/CATCH scope (1026), RETURN-with-value in a
    /// script frame (178), bare THROW outside a CATCH block (10704 — M3, verified
    /// compile-time + lexical), variable use before its declaration point (137, with
    /// declaration hoisting per fact 14), and cursor-variable aliasing (A63 — a cursor variable
    /// assigned from another cursor, the one cursor-variable shape still unsupported; C12).
    /// </summary>
    internal static ControlFlowMap BuildAndValidate(
        IList<TSqlStatement> body, FrameKind frameKind, IEnumerable<string> preDeclaredVariables)
    {
        var labels = new Dictionary<string, LabelTarget>(StringComparer.OrdinalIgnoreCase);
        var paths = new Dictionary<TSqlStatement, IReadOnlyList<PathStep>>(ReferenceEqualityComparer.Instance);
        var gotos = new List<(GoToStatement Statement, IReadOnlyList<PathStep> Path)>();
        var diagnostics = new List<ParseTimeDiagnostic>();
        // A63: cursor variables are supported (reified as GLOBAL cursors, §9). Track their names so
        // the ONE unsupported shape — assigning a cursor variable from ANOTHER cursor
        // (`SET @c = @d` / `SET @c = named`, which parses as a scalar assignment, not a
        // CursorDefinition) — is refused with an honest message rather than a runtime error 137.
        var cursorVariables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Variable-visibility model (fact 14): declarations are hoisted at compile time,
        // visible from the END of their whole DECLARE statement (probe G: sibling
        // declarators cannot see each other) to the end of the frame, regardless of the
        // execution path. Parameters are visible everywhere.
        var visibleFrom = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in preDeclaredVariables)
            visibleFrom[NormalizeVariable(name)] = 0;

        void Walk(IList<TSqlStatement> list, PathContainerKind kind, TSqlFragment? owner,
                  IReadOnlyList<PathStep> prefix, int loopDepth)
        {
            for (var i = 0; i < list.Count; i++)
            {
                var s = list[i];
                var path = new List<PathStep>(prefix.Count + 1);
                path.AddRange(prefix);
                path.Add(new PathStep(kind, owner, list, i));
                paths[s] = path;

                switch (s)
                {
                    case LabelStatement label:
                        var key = LabelKey(label.Value);
                        if (!labels.TryAdd(key, new LabelTarget(label, path)))
                            diagnostics.Add(new(label.StartLine,
                                $"The label '{key}' has already been declared. Label names must be unique " +
                                "within a query batch or stored procedure (engine error 132)."));
                        break;

                    case GoToStatement gotoStatement:
                        gotos.Add((gotoStatement, path));
                        break;

                    case BreakStatement when loopDepth == 0:
                        diagnostics.Add(new(s.StartLine,
                            "Cannot use a BREAK statement outside the scope of a WHILE statement (engine error 135)."));
                        break;

                    case ContinueStatement when loopDepth == 0:
                        diagnostics.Add(new(s.StartLine,
                            "Cannot use a CONTINUE statement outside the scope of a WHILE statement (engine error 136)."));
                        break;

                    case ReturnStatement { Expression: not null } when frameKind == FrameKind.Script:
                        diagnostics.Add(new(s.StartLine,
                            "A RETURN statement with a return value cannot be used in this context " +
                            "(engine error 178 — legal only inside a procedure)."));
                        break;

                    // M3 (§10.2): bare ;THROW is a compile-time refusal outside a CATCH
                    // block — verified live: the batch never starts (engine error 10704),
                    // and the rule is lexical (a THROW inside a TRY nested in a CATCH is
                    // legal). Any CatchBlock step along the path satisfies it.
                    case ThrowStatement { ErrorNumber: null } when !path.Any(p => p.Kind == PathContainerKind.CatchBlock):
                        diagnostics.Add(new(s.StartLine,
                            "To rethrow an error, a THROW statement must be used inside a CATCH block " +
                            "(engine error 10704). Insert the THROW statement inside a CATCH block, or " +
                            "add error parameters to the THROW statement."));
                        break;

                    case DeclareVariableStatement declare:
                        var declarationPoint = declare.StartOffset + declare.FragmentLength;
                        foreach (var element in declare.Declarations)
                        {
                            var name = NormalizeVariable(element.VariableName?.Value);
                            // A63: cursor variables are reified as GLOBAL cursors (§9), not scalar state.
                            // Record the name so a later cursor-aliasing SET can be refused; still add it
                            // to visibleFrom (a use before its DECLARE is engine error 137 either way).
                            if (element.DataType is SqlDataTypeReference { SqlDataTypeOption: SqlDataTypeOption.Cursor }
                                && name.Length > 0)
                                cursorVariables.Add(name);
                            if (name.Length > 0 && !visibleFrom.ContainsKey(name))
                                visibleFrom[name] = declarationPoint;
                        }
                        break;

                    // A63: `SET @c = <cursor>` (aliasing an existing cursor into a cursor variable)
                    // parses as a scalar SetVariableStatement with an Expression, not a CursorDefinition.
                    // The debugger reifies a cursor variable only at its `SET @c = CURSOR FOR …` creation
                    // site; aliasing (which would need to share one physical cursor between two variables)
                    // is unsupported — refuse it up front rather than fault at runtime.
                    case SetVariableStatement { CursorDefinition: null, Expression: not null, Variable.Name: { } setTarget }
                            when cursorVariables.Contains(NormalizeVariable(setTarget)):
                        diagnostics.Add(new(s.StartLine,
                            $"Assigning a cursor variable ({setTarget}) from another cursor is not supported " +
                            "by the debugger (caveat C12). Declare and open the cursor directly with " +
                            $"SET {setTarget} = CURSOR FOR …."));
                        break;

                    // A63 (F4): passing a cursor variable to a procedure is necessarily a cursor
                    // OUTPUT parameter (T-SQL has no input cursor parameters) — the callee allocates
                    // a cursor INTO the variable. The debugger cannot model that: the reified cursor
                    // would live only inside the callee's per-SU batch and not survive back to the
                    // caller. Refuse up front with a clear message instead of the misleading 137/16950
                    // that would otherwise surface mid-session.
                    case ExecuteStatement { ExecuteSpecification.ExecutableEntity: ExecutableProcedureReference { Parameters: { } execParams } }
                            when execParams.FirstOrDefault(p =>
                                p.ParameterValue is VariableReference { Name: { } actual }
                                && cursorVariables.Contains(NormalizeVariable(actual))) is { } cursorParam:
                        diagnostics.Add(new(s.StartLine,
                            $"Passing a cursor variable ({((VariableReference)cursorParam.ParameterValue).Name}) to a " +
                            "procedure (a cursor OUTPUT parameter) is not supported by the debugger (caveat C12)."));
                        break;

                    case DeclareTableVariableStatement tableVariable:
                        var tableVarName = NormalizeVariable(tableVariable.Body?.VariableName?.Value);
                        if (tableVarName.Length > 0 && !visibleFrom.ContainsKey(tableVarName))
                            visibleFrom[tableVarName] = tableVariable.StartOffset + tableVariable.FragmentLength;
                        break;
                }

                foreach (var (childKind, children) in InterpreterScopes.ChildrenOf(s))
                    Walk(children, childKind, s, path,
                         loopDepth + (childKind == PathContainerKind.WhileBody ? 1 : 0));
            }
        }

        Walk(body, PathContainerKind.Root, null, Array.Empty<PathStep>(), 0);

        foreach (var (gotoStatement, gotoPath) in gotos)
        {
            var name = LabelKey(gotoStatement.LabelName.Value);
            if (!labels.TryGetValue(name, out var target))
            {
                diagnostics.Add(new(gotoStatement.StartLine,
                    $"A GOTO statement references the label '{name}' but the label has not been declared (engine error 133)."));
                continue;
            }

            // Engine rule (fact 13 probe 6, error 1026): a jump may leave TRY/CATCH
            // scopes freely but may not ENTER one — every TRY/CATCH side enclosing the
            // label must also enclose the GOTO (same TryCatchStatement, same side).
            foreach (var step in target.Path)
            {
                if (step.Kind is not (PathContainerKind.TryBlock or PathContainerKind.CatchBlock))
                    continue;
                var jumperInsideSameScope = gotoPath.Any(g => g.Kind == step.Kind && ReferenceEquals(g.Owner, step.Owner));
                if (!jumperInsideSameScope)
                {
                    diagnostics.Add(new(gotoStatement.StartLine,
                        "GOTO cannot be used to jump into a TRY or CATCH scope (engine error 1026)."));
                    break;
                }
            }
        }

        if (frameKind == FrameKind.Script)
            ValidateVariableReferences(body, visibleFrom, diagnostics);

        if (diagnostics.Count > 0)
            throw new ParseTimeDiagnosticException(diagnostics.OrderBy(d => d.Line).ToList());

        return new ControlFlowMap(labels, paths);
    }

    private static string NormalizeVariable(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;
        return raw.StartsWith('@') ? raw : "@" + raw;
    }

    // Fact 14 probes E/G: referencing a variable textually before the end of its DECLARE
    // statement (or with no declaration at all) is engine compile error 137 — the batch
    // never runs. Mirroring it keeps declaration hoisting honest: without this check the
    // hoisted state table would happily execute code native refuses. SCRIPT frames only:
    // there the debugger is the compiler-of-record (the script never compiled
    // server-side). For procedure frames the server already refused any such body at
    // CREATE time, and the frame's parameters — which this walker cannot see — are the
    // dominant legitimate reference source.
    private static void ValidateVariableReferences(
        IList<TSqlStatement> body, Dictionary<string, int> visibleFrom, List<ParseTimeDiagnostic> diagnostics)
    {
        var collector = new VariableReferenceCollector();
        foreach (var statement in body)
            statement.Accept(collector);

        foreach (var reference in collector.References)
        {
            var name = NormalizeVariable(reference.Name);
            if (!visibleFrom.TryGetValue(name, out var from))
                diagnostics.Add(new(reference.StartLine,
                    $"Must declare the scalar variable \"{reference.Name}\" (engine error 137 — no declaration in this frame)."));
            else if (reference.StartOffset < from)
                diagnostics.Add(new(reference.StartLine,
                    $"Must declare the scalar variable \"{reference.Name}\" (engine error 137 — referenced before its " +
                    "declaration point; T-SQL declarations take effect at the end of their DECLARE statement, fact 14)."));
        }
    }

    /// <summary>
    /// Collects variable references that resolve against THIS frame's scope. Prunes:
    /// module-creating DDL bodies (their own scope — the server compiles them), and the
    /// name position of EXEC named arguments (<c>EXEC p @calleeParam = @value</c> — only
    /// the value side reads a caller variable; verified shape, ExecuteParameter.Variable).
    /// </summary>
    private sealed class VariableReferenceCollector : TSqlFragmentVisitor
    {
        public List<VariableReference> References { get; } = new();

        public override void Visit(VariableReference node) => References.Add(node);

        public override void ExplicitVisit(ExecuteParameter node)
        {
            node.ParameterValue?.Accept(this);
        }

        public override void ExplicitVisit(CreateProcedureStatement node) { }
        public override void ExplicitVisit(AlterProcedureStatement node) { }
        public override void ExplicitVisit(CreateOrAlterProcedureStatement node) { }
        public override void ExplicitVisit(CreateFunctionStatement node) { }
        public override void ExplicitVisit(AlterFunctionStatement node) { }
        public override void ExplicitVisit(CreateOrAlterFunctionStatement node) { }
        public override void ExplicitVisit(CreateTriggerStatement node) { }
        public override void ExplicitVisit(AlterTriggerStatement node) { }
        public override void ExplicitVisit(CreateOrAlterTriggerStatement node) { }
    }
}
