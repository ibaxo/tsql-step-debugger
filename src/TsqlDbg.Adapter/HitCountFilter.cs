namespace TsqlDbg.Adapter;

// DESIGN §13 says only "Hit counts: adapter-side counter" — it doesn't pin
// hitCondition's comparison semantics. Ruled at the M2->M3 gate
// (docs/archive/reviews/m2-gate-review-fable.md §4), ratified by Ivan 2026-07-05:
// operator-aware, with a bare number meaning "stop on the Nth qualifying hit only"
// (not "Nth and every hit after" — the DAP spec's own ignore-count reading, rejected
// because it silently inverts an explicit operator like "< 5" and fires forever from
// an explicit "== 3"). A "qualifying hit" is a pass where the breakpoint's own
// `condition`, if present, already evaluated true — that counting happens one layer up,
// in the adapter's ShouldBreakAsync; this type only turns a running hit count into a
// stop/no-stop decision.
public sealed class HitCountFilter
{
    private enum Op
    {
        Equal,
        GreaterThan,
        GreaterOrEqual,
        LessThan,
        LessOrEqual,
        Modulo,
    }

    // Longest-prefix-first so "==" is never shadowed by "=", ">=" never by ">", etc.
    private static readonly (string Token, Op Value)[] Operators =
    {
        (">=", Op.GreaterOrEqual),
        ("<=", Op.LessOrEqual),
        ("==", Op.Equal),
        (">", Op.GreaterThan),
        ("<", Op.LessThan),
        ("=", Op.Equal),
        ("%", Op.Modulo),
    };

    private readonly Op _op;
    private readonly int _n;

    private HitCountFilter(Op op, int n, string? invalidText)
    {
        _op = op;
        _n = n;
        InvalidText = invalidText;
    }

    /// <summary>Non-null only when parsing failed — the original hitCondition text,
    /// for the console warning. A filter in this state always stops: "an unparseable
    /// hitCondition = console warning + break on every hit" (same fail-toward-stopping
    /// philosophy as a faulting breakpoint condition, §13) — never silently past a
    /// breakpoint.</summary>
    public string? InvalidText { get; }

    /// <summary>Parses a DAP hitCondition string. Returns null for an absent/blank
    /// hitCondition — meaning no hit-count filtering at all (every qualifying hit
    /// stops), which is NOT the same as an invalid one (non-null <see cref="InvalidText"/>).</summary>
    public static HitCountFilter? Parse(string? hitCondition)
    {
        if (string.IsNullOrWhiteSpace(hitCondition))
        {
            return null;
        }

        var text = hitCondition.Trim();
        var op = Op.Equal;
        foreach (var (token, value) in Operators)
        {
            if (text.StartsWith(token, StringComparison.Ordinal))
            {
                op = value;
                text = text[token.Length..].TrimStart();
                break;
            }
        }

        if (!int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n)
            || n < 1)
        {
            return new HitCountFilter(op, 0, invalidText: hitCondition);
        }

        return new HitCountFilter(op, n, invalidText: null);
    }

    /// <summary>hitCount is the running count of qualifying hits, already incremented
    /// for this hit. Bare/`=`/`==` N: the counter is monotonic, so equality fires at
    /// most once — no separate disable flag needed. Explicit operators are honored
    /// literally even when the result reads oddly (`&lt; 2` → only the 1st hit stops,
    /// never again) — never special-cased, per the ruling.</summary>
    public bool ShouldStop(int hitCount) => InvalidText is not null
        ? true
        : _op switch
        {
            Op.Equal => hitCount == _n,
            Op.GreaterThan => hitCount > _n,
            Op.GreaterOrEqual => hitCount >= _n,
            Op.LessThan => hitCount < _n,
            Op.LessOrEqual => hitCount <= _n,
            Op.Modulo => hitCount % _n == 0,
            _ => true,
        };
}
