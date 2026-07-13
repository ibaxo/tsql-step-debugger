using System.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace TsqlDbg.Core.Parsing;

// DESIGN §5.4 (A47): one GO-delimited batch of a script, for the launch-failure ORACLE
// path (§20.3). Text is the batch's original source; StartLine is its 1-based first line
// in the whole script, so a server-reported line maps back as StartLine + serverLine - 1.
public readonly record struct OracleBatchSegment(string Text, int StartLine);

// DESIGN §2: "Parser: TSql150Parser default (SQL 2019); selectable TSql160/TSql170 via
// launch config compatLevel. Instantiate with initialQuotedIdentifiers matching the
// frame (§11.2)." Verified against the installed 180.37.3 package (docs/package-versions.md):
// TSql1{50,60,70}Parser all take (bool initialQuotedIdentifiers) and expose
// Parse(TextReader, out IList<ParseError>) via the TSqlParser base class.
public static class ScriptParser
{
    public static TSqlFragment Parse(
        string sql,
        bool initialQuotedIdentifiers,
        int compatLevel,
        out IList<ParseError> errors)
    {
        var parser = CreateParser(initialQuotedIdentifiers, compatLevel);
        using var reader = new StringReader(sql);
        return parser.Parse(reader, out errors);
    }

    // DESIGN §13: conditional-breakpoint / §12.4 watch condition text is standalone —
    // not part of the frame's own source — so it is parsed (and, downstream, rewritten)
    // against itself as its own "fullScript", never spliced into the frame's real
    // source text. Verified against the installed 180.37.3 package:
    // TSqlParser.ParseBooleanExpression(TextReader, out IList<ParseError>) returns a
    // BooleanExpression whose StartOffset/FragmentLength are relative to that reader's
    // own text, exactly like Parse's TSqlScript.
    public static BooleanExpression? ParseBooleanExpression(
        string text,
        bool initialQuotedIdentifiers,
        int compatLevel,
        out IList<ParseError> errors)
    {
        var parser = CreateParser(initialQuotedIdentifiers, compatLevel);
        using var reader = new StringReader(text);
        return parser.ParseBooleanExpression(reader, out errors);
    }

    // M5 I6 (§12.3 REPL): standalone text, own coordinate space (like
    // ParseBooleanExpression) — "one statement per evaluation" is enforced by the
    // CALLER checking StatementList.Statements.Count == 1.
    public static StatementList? ParseStatementList(
        string text,
        bool initialQuotedIdentifiers,
        int compatLevel,
        out IList<ParseError> errors)
    {
        var parser = CreateParser(initialQuotedIdentifiers, compatLevel);
        using var reader = new StringReader(text);
        return parser.ParseStatementList(reader, out errors);
    }

    // M5 I8 (§8.3 setVariable): "parse the literal with ScriptDom expression parser" —
    // same standalone-text shape as ParseBooleanExpression, for a single scalar literal.
    public static ScalarExpression? ParseScalarExpression(
        string text,
        bool initialQuotedIdentifiers,
        int compatLevel,
        out IList<ParseError> errors)
    {
        var parser = CreateParser(initialQuotedIdentifiers, compatLevel);
        using var reader = new StringReader(text);
        return parser.ParseExpression(reader, out errors);
    }

    // DESIGN §5.4 (A43): `GO N` repeat count. ScriptDom's PARSER cannot handle a repeat
    // count (parse error 46010, fact 32e), but its TOKENIZER can — `GO` is a first-class
    // `TSqlTokenType.Go` token and the count is the immediately-following `Integer` token on
    // the SAME line (verified 2026-07-12, docs/archive/reviews/go-n-repeat-count-opus.md §2). This
    // tokenizes the script, records each `GO <n>` separator as (GO-offset, count), and
    // returns a copy with each count's digit run BLANKED to equal-length spaces, so a
    // subsequent Parse() splits the file as PLAIN `GO` with every StartOffset/StartLine
    // byte-identical to the original (§5.2 line ground truth, §5.3 original-source slices).
    // The blanked characters sit on the `GO` separator line — never inside a batch's
    // executed slice — and a token scan can never match a `GO 5` inside a string/comment
    // literal (that is one string/comment token, not a `Go` token). A MALFORMED count
    // (`GO -1` → `Minus`, `GO 1.5` → `Numeric`) is not an `Integer` token, so it is left
    // unblanked and Parse() refuses it at launch, matching sqlcmd's own hard error.
    public static string BlankGoRepeatCounts(
        string sql,
        bool initialQuotedIdentifiers,
        int compatLevel,
        out IReadOnlyList<GoRepeatMarker> markers)
    {
        var parser = CreateParser(initialQuotedIdentifiers, compatLevel);
        using var reader = new StringReader(sql);
        var tokens = parser.GetTokenStream(reader, out _);

        var found = new List<GoRepeatMarker>();
        char[]? buffer = null;   // cloned lazily — only if we actually blank a count

        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].TokenType != TSqlTokenType.Go)
            {
                continue;
            }

            var go = tokens[i];
            var j = i + 1;
            while (j < tokens.Count && tokens[j].TokenType == TSqlTokenType.WhiteSpace)
            {
                j++;
            }

            // A repeat count is an Integer immediately after `GO` (whitespace aside) ON THE
            // SAME LINE (a digit on the next line is a new batch, not a count — sqlcmd parity).
            if (j >= tokens.Count
                || tokens[j].TokenType != TSqlTokenType.Integer
                || tokens[j].Line != go.Line)
            {
                continue;   // plain `GO` (count 1) — leave it for ScriptDom to split
            }

            var countToken = tokens[j];
            if (!long.TryParse(countToken.Text, out var parsed))
            {
                continue;   // overflows even long — absurd; leave unblanked, ScriptDom refuses
            }

            found.Add(new GoRepeatMarker(go.Offset, (int)Math.Min(parsed, int.MaxValue)));

            buffer ??= sql.ToCharArray();
            for (var k = 0; k < countToken.Text.Length; k++)
            {
                buffer[countToken.Offset + k] = ' ';
            }
        }

        markers = found;
        return buffer is null ? sql : new string(buffer);
    }

    // DESIGN §5.4 / §20.3 (A47): split a script into batches on `GO` the way sqlcmd/SSMS
    // do — for the launch-failure ORACLE path only. When ScriptDom REFUSES a script,
    // Session asks the live server for its own diagnostic under SET PARSEONLY ON; the
    // server has no notion of `GO` (a client separator), so each batch is sent on its own.
    // The TOKENIZER succeeds even when the PARSER fails (GetTokenStream vs Parse), so the
    // batch boundaries are reliable here, and a `GO` inside a string/comment is that one
    // string/comment token — never a `Go` token — so it never splits. Returns one segment
    // per non-empty GO-delimited region with its 1-based StartLine in the original script;
    // whitespace-only regions (a leading/trailing/`GO\nGO` gap) are dropped. A script with
    // no `GO` is the single-segment case (the whole text, StartLine 1).
    public static IReadOnlyList<OracleBatchSegment> SplitOnGoSeparators(
        string sql, bool initialQuotedIdentifiers, int compatLevel)
    {
        var parser = CreateParser(initialQuotedIdentifiers, compatLevel);
        using var reader = new StringReader(sql);
        var tokens = parser.GetTokenStream(reader, out _);

        var goLines = new HashSet<int>();
        foreach (var token in tokens)
        {
            if (token.TokenType == TSqlTokenType.Go)
            {
                goLines.Add(token.Line);
            }
        }

        // Line numbers here (1-based, split on '\n') align with ScriptDom's token .Line,
        // so a segment's line N maps to whole-script line (StartLine + N - 1). A CRLF's
        // trailing '\r' rides along in the text — the server tolerates it.
        var lines = sql.Split('\n');
        var segments = new List<OracleBatchSegment>();
        var builder = new StringBuilder();
        var segmentStartLine = 1;
        for (var line = 1; line <= lines.Length; line++)
        {
            if (goLines.Contains(line))
            {
                AddSegment(segments, builder, segmentStartLine);
                builder.Clear();
                segmentStartLine = line + 1;
                continue;
            }

            if (builder.Length == 0)
            {
                segmentStartLine = line;   // first line of this segment (exact line mapping)
            }

            builder.Append(lines[line - 1]).Append('\n');
        }

        AddSegment(segments, builder, segmentStartLine);
        return segments;

        static void AddSegment(List<OracleBatchSegment> segments, StringBuilder builder, int startLine)
        {
            var text = builder.ToString();
            if (!string.IsNullOrWhiteSpace(text))
            {
                segments.Add(new OracleBatchSegment(text, startLine));
            }
        }
    }

    private static TSqlParser CreateParser(bool initialQuotedIdentifiers, int compatLevel) => compatLevel switch
    {
        160 => new TSql160Parser(initialQuotedIdentifiers),
        170 => new TSql170Parser(initialQuotedIdentifiers),
        _ => new TSql150Parser(initialQuotedIdentifiers),
    };
}
