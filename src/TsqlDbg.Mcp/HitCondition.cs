using System.Globalization;

namespace TsqlDbg.Mcp;

// DESIGN §24.6/§13: a minimal hit-count parser for the programmatic surface (v1). Supports
// the common VS Code forms: bare "N" / "==N" / "=N" (stop on the Nth hit), ">N", ">=N",
// "<N", "<=N", "%N" (every Nth). An unparseable string breaks on EVERY hit — never silently
// past a breakpoint, matching the adapter's HitCountFilter philosophy. (Logpoints and the
// adapter's full HitCountFilter reuse are a documented v2 follow-up — §24.6.)
public sealed class HitCondition
{
    private readonly char _op;   // '=', '>', 'g' (>=), '<', 'l' (<=), '%'
    private readonly int _n;
    private readonly bool _invalid;

    private HitCondition(char op, int n, bool invalid)
    {
        _op = op;
        _n = n;
        _invalid = invalid;
    }

    public static HitCondition? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var s = text.Trim();
        (char op, int prefixLen) = s switch
        {
            _ when s.StartsWith(">=", StringComparison.Ordinal) => ('g', 2),
            _ when s.StartsWith("<=", StringComparison.Ordinal) => ('l', 2),
            _ when s.StartsWith("==", StringComparison.Ordinal) => ('=', 2),
            _ when s.StartsWith(">", StringComparison.Ordinal) => ('>', 1),
            _ when s.StartsWith("<", StringComparison.Ordinal) => ('<', 1),
            _ when s.StartsWith("=", StringComparison.Ordinal) => ('=', 1),
            _ when s.StartsWith("%", StringComparison.Ordinal) => ('%', 1),
            _ => ('=', 0),   // bare number → equality
        };

        var numberText = s[prefixLen..].Trim();
        if (!int.TryParse(numberText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n <= 0)
        {
            return new HitCondition('=', 0, invalid: true);
        }

        return new HitCondition(op, n, invalid: false);
    }

    public bool ShouldStop(int hitCount)
    {
        if (_invalid)
        {
            return true;   // never silently skip
        }

        return _op switch
        {
            '=' => hitCount == _n,
            '>' => hitCount > _n,
            'g' => hitCount >= _n,
            '<' => hitCount < _n,
            'l' => hitCount <= _n,
            '%' => hitCount % _n == 0,
            _ => true,
        };
    }
}
