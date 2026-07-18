using System.Globalization;
using System.Text;

// ── Pipeline: DIRECT-WRITE (Revit → ggifc tree → Cypher → Neo4j; no temp IFC) ──
// Lossless STEP-line source for node properties. See
// doc_process/2026-07-12-plan-stepline-entitywalker.md (方案 B).
namespace RevitGraphPlugin.Cypher;

/// <summary>
/// A single parsed parameter of a STEP (Part 21) line. The concrete subtypes cover
/// every token shape ggifc's <c>entity.ToString()</c> can emit for an attribute slot.
/// </summary>
public abstract record StepToken
{
    /// <summary><c>$</c> — an unset optional attribute.</summary>
    public sealed record Unset : StepToken;

    /// <summary><c>*</c> — a DERIVED attribute (ConMan2 stores these as "$").</summary>
    public sealed record Derived : StepToken;

    /// <summary><c>#42</c> — a reference to another entity (an edge; not a property).</summary>
    public sealed record Reference(int Id) : StepToken;

    /// <summary><c>.MODEL_VIEW.</c> — an enumeration member; the bare schema name.</summary>
    public sealed record EnumMember(string Name) : StepToken;

    /// <summary><c>.T.</c> / <c>.F.</c> — an IFC boolean.</summary>
    public sealed record Boolean(bool Value) : StepToken;

    /// <summary>An integer literal (STEP integers carry no decimal point).</summary>
    public sealed record Integer(long Value) : StepToken;

    /// <summary>A real literal (STEP reals always carry a decimal point).</summary>
    public sealed record Real(double Value) : StepToken;

    /// <summary>A string literal, already un-escaped (<c>''</c> → <c>'</c>).</summary>
    public sealed record Text(string Value) : StepToken;

    /// <summary>A parenthesised aggregate; items may be primitives, references, or nested lists.</summary>
    public sealed record List(IReadOnlyList<StepToken> Items) : StepToken;

    /// <summary>
    /// A typed value such as <c>IFCBOOLEAN(.T.)</c> or <c>IFCLABEL('x')</c> — a keyword
    /// followed by parenthesised arguments. ConMan2 records these as inline nodes, so the
    /// STEP source treats them as skipped slots (reflection emits the inline).
    /// </summary>
    public sealed record Typed(string Keyword, IReadOnlyList<StepToken> Args) : StepToken;
}

/// <summary>One parsed STEP line: its entity id, class keyword, and ordered arguments.</summary>
public sealed record StepLine(int Id, string Keyword, IReadOnlyList<StepToken> Arguments);

/// <summary>
/// Parses a ggifc STEP (Part 21) line into ordered <see cref="StepToken"/>s and maps a
/// primitive token to the exact property value ConMan2 stores (matching Python's
/// <c>str()</c> / <c>str(tuple)</c> formatting), so the direct pipeline's node properties
/// are byte-identical to the temp-IFC bridge graph.
/// </summary>
public static class StepLineParser
{
    /// <summary>
    /// Parse a full STEP line — e.g. <c>#4=IFCWALL('guid',#325,'Name',$,…);</c> — into its
    /// id, class keyword, and top-level argument tokens. Lenient about a missing <c>#id=</c>
    /// prefix or trailing <c>;</c> so a bare <c>IFCCLASS(...)</c> also parses.
    /// </summary>
    public static StepLine ParseLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            throw new FormatException("Empty STEP line.");

        var s = line.Trim();
        var i = 0;

        // Optional "#id="
        var id = -1;
        if (s[i] == '#')
        {
            i++;
            var start = i;
            while (i < s.Length && char.IsDigit(s[i])) i++;
            id = int.Parse(s.AsSpan(start, i - start), provider: CultureInfo.InvariantCulture);
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == '=') i++;
        }
        SkipWs(s, ref i);

        // Class keyword up to the opening '('
        var kwStart = i;
        while (i < s.Length && s[i] != '(') i++;
        if (i >= s.Length)
            throw new FormatException($"No argument list found in STEP line: {line}");
        var keyword = s.Substring(kwStart, i - kwStart).Trim();

        i++; // consume '('
        var args = ParseTokenList(s, ref i, line);
        return new StepLine(id, keyword, args);
    }

    /// <summary>Convenience: the top-level argument tokens of <paramref name="line"/>.</summary>
    public static IReadOnlyList<StepToken> ParseArguments(string line) => ParseLine(line).Arguments;

    // ── Tokenizer ──────────────────────────────────────────────────────────────

    // Parse comma-separated tokens until the matching ')'. Consumes the closing ')'.
    private static IReadOnlyList<StepToken> ParseTokenList(string s, ref int i, string line)
    {
        var items = new List<StepToken>();
        SkipWs(s, ref i);
        if (i < s.Length && s[i] == ')') { i++; return items; } // empty ()

        while (true)
        {
            items.Add(ParseToken(s, ref i, line));
            SkipWs(s, ref i);
            if (i >= s.Length)
                throw new FormatException($"Unterminated argument list in STEP line: {line}");
            var c = s[i++];
            if (c == ')') break;
            if (c != ',')
                throw new FormatException($"Expected ',' or ')' at index {i - 1} in STEP line: {line}");
            SkipWs(s, ref i);
        }
        return items;
    }

    private static StepToken ParseToken(string s, ref int i, string line)
    {
        SkipWs(s, ref i);
        if (i >= s.Length)
            throw new FormatException($"Unexpected end of STEP line: {line}");

        var c = s[i];
        switch (c)
        {
            case '$': i++; return new StepToken.Unset();
            case '*': i++; return new StepToken.Derived();
            case '(':
                i++;
                return new StepToken.List(ParseTokenList(s, ref i, line));
            case '#':
                {
                    i++;
                    var start = i;
                    while (i < s.Length && char.IsDigit(s[i])) i++;
                    return new StepToken.Reference(
                        int.Parse(s.AsSpan(start, i - start), provider: CultureInfo.InvariantCulture));
                }
            case '.':
                return ParseEnum(s, ref i, line);
            case '\'':
                return new StepToken.Text(ParseString(s, ref i, line));
        }

        if (c == '+' || c == '-' || char.IsDigit(c))
            return ParseNumber(s, ref i);

        if (char.IsLetter(c))
            return ParseTyped(s, ref i, line);

        throw new FormatException($"Unexpected character '{c}' at index {i} in STEP line: {line}");
    }

    // ".NAME." → enum; ".T."/".F." → boolean.
    private static StepToken ParseEnum(string s, ref int i, string line)
    {
        i++; // opening '.'
        var start = i;
        while (i < s.Length && s[i] != '.') i++;
        if (i >= s.Length)
            throw new FormatException($"Unterminated enum in STEP line: {line}");
        var name = s.Substring(start, i - start);
        i++; // closing '.'
        return name switch
        {
            "T" => new StepToken.Boolean(true),
            "F" => new StepToken.Boolean(false),
            _ => new StepToken.EnumMember(name),
        };
    }

    // 'text' with '' as an escaped single quote. Backslash escapes (\X\, \X2\, \S\)
    // are not yet implemented — fail loud rather than silently corrupt (see plan risks).
    private static string ParseString(string s, ref int i, string line)
    {
        i++; // opening quote
        var sb = new StringBuilder();
        while (i < s.Length)
        {
            var c = s[i];
            if (c == '\'')
            {
                if (i + 1 < s.Length && s[i + 1] == '\'') { sb.Append('\''); i += 2; continue; }
                i++; // closing quote
                return sb.ToString();
            }
            if (c == '\\')
                throw new NotSupportedException(
                    $"STEP backslash escape not supported yet (near index {i}) in line: {line}");
            sb.Append(c);
            i++;
        }
        throw new FormatException($"Unterminated string in STEP line: {line}");
    }

    private static StepToken ParseNumber(string s, ref int i)
    {
        var start = i;
        if (s[i] == '+' || s[i] == '-') i++;
        var isReal = false;
        while (i < s.Length)
        {
            var c = s[i];
            if (char.IsDigit(c)) { i++; continue; }
            if (c == '.') { isReal = true; i++; continue; }
            if (c == 'e' || c == 'E')
            {
                isReal = true;
                i++;
                if (i < s.Length && (s[i] == '+' || s[i] == '-')) i++;
                continue;
            }
            break;
        }
        var text = s.Substring(start, i - start);
        return isReal
            ? new StepToken.Real(double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture))
            : new StepToken.Integer(long.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture));
    }

    // KEYWORD( args ) — a typed/defined value, e.g. IFCBOOLEAN(.T.), IFCLABEL('x').
    private static StepToken ParseTyped(string s, ref int i, string line)
    {
        var start = i;
        while (i < s.Length && s[i] != '(') i++;
        if (i >= s.Length)
            throw new FormatException($"Typed value missing '(' in STEP line: {line}");
        var keyword = s.Substring(start, i - start).Trim();
        i++; // consume '('
        return new StepToken.Typed(keyword, ParseTokenList(s, ref i, line));
    }

    private static void SkipWs(string s, ref int i)
    {
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
    }

    // ── Token → property value ───────────────────────────────────────────────────

    /// <summary>
    /// Converts a primitive attribute slot to the value ConMan2 stores. Returns
    /// <c>false</c> for slots the reflection layer owns instead — entity references,
    /// typed inline values, and aggregates thereof (they become edges / inline nodes,
    /// not node properties). On <c>true</c>, <paramref name="value"/> is a string,
    /// long, double, or bool matching the bridge graph exactly.
    /// </summary>
    public static bool TryToPropertyValue(StepToken token, out object value)
    {
        switch (token)
        {
            case StepToken.Unset:
            case StepToken.Derived:
                value = "$";
                return true;
            case StepToken.EnumMember e:
                value = e.Name;
                return true;
            case StepToken.Boolean b:
                value = b.Value;
                return true;
            case StepToken.Integer n:
                value = n.Value;
                return true;
            case StepToken.Real r:
                value = r.Value;
                return true;
            case StepToken.Text t:
                value = t.Value; // "" stays "" (distinct from unset "$")
                return true;
            case StepToken.List list:
                return TryListToPropertyValue(list, out value);
            default: // Reference, Typed → owned by reflection (edge / inline node)
                value = null!;
                return false;
        }
    }

    private static bool TryListToPropertyValue(StepToken.List list, out object value)
    {
        // A reference or typed leaf anywhere means this aggregate is edges / inline
        // nodes, handled by reflection — not a primitive-list property.
        if (ContainsNonPrimitive(list))
        {
            value = null!;
            return false;
        }
        // Empty present aggregate: fold to "$" (the unset/empty convention the old
        // emitter used; STEP writes unset aggregates as $, so () is not expected here).
        value = list.Items.Count == 0 ? "$" : PyReprList(list);
        return true;
    }

    private static bool ContainsNonPrimitive(StepToken.List list)
    {
        foreach (var item in list.Items)
        {
            if (item is StepToken.Reference or StepToken.Typed) return true;
            if (item is StepToken.List inner && ContainsNonPrimitive(inner)) return true;
        }
        return false;
    }

    // ── Python str()/repr() formatting ───────────────────────────────────────────

    // str(tuple) of a primitive aggregate: ", "-joined, single-element gets a trailing
    // comma, wrapped in parentheses. Matches ConMan2's ast.literal_eval round-trip source.
    private static string PyReprList(StepToken.List list)
    {
        var sb = new StringBuilder("(");
        for (var k = 0; k < list.Items.Count; k++)
        {
            if (k > 0) sb.Append(", ");
            sb.Append(PyReprToken(list.Items[k]));
        }
        if (list.Items.Count == 1) sb.Append(',');
        sb.Append(')');
        return sb.ToString();
    }

    private static string PyReprToken(StepToken token) => token switch
    {
        StepToken.Integer n => n.Value.ToString(CultureInfo.InvariantCulture),
        StepToken.Real r => PyRepr(r.Value),
        StepToken.Boolean b => b.Value ? "True" : "False",
        StepToken.Text t => PyReprStr(t.Value),
        StepToken.List l => PyReprList(l),
        _ => throw new NotSupportedException(
            $"Token {token.GetType().Name} has no primitive Python repr."),
    };

    // Python repr of a str: single-quoted, backslashes and single quotes escaped.
    private static string PyReprStr(string s)
    {
        var sb = new StringBuilder("'");
        foreach (var c in s)
        {
            if (c == '\\' || c == '\'') sb.Append('\\');
            sb.Append(c);
        }
        sb.Append('\'');
        return sb.ToString();
    }

    /// <summary>
    /// Formats a finite double exactly as CPython's <c>repr(float)</c> does: shortest
    /// round-tripping digits, a mandatory decimal point or exponent, lowercase <c>e</c>
    /// with a signed ≥2-digit exponent, and the fixed/scientific switch at
    /// <c>decpt &lt;= -4 || decpt &gt; 16</c>. This is what ConMan2 stored via Python
    /// <c>str(float)</c>, so the direct graph's list strings match the bridge byte-for-byte.
    /// </summary>
    public static string PyRepr(double value)
    {
        if (value == 0.0)
            return double.IsNegative(value) ? "-0.0" : "0.0";
        if (double.IsNaN(value)) return "nan";
        if (double.IsPositiveInfinity(value)) return "inf";
        if (double.IsNegativeInfinity(value)) return "-inf";

        var neg = value < 0;
        // "R" gives the shortest round-trippable representation on .NET Core 3.0+.
        var r = Math.Abs(value).ToString("R", CultureInfo.InvariantCulture);

        // Split shortest form into significant digits + a decimal-point position (decpt),
        // where the value is 0.<digits> shifted so the point sits after `decpt` digits.
        var exp = 0;
        var ePos = r.IndexOfAny(new[] { 'e', 'E' });
        if (ePos >= 0)
        {
            exp = int.Parse(r.AsSpan(ePos + 1), provider: CultureInfo.InvariantCulture);
            r = r.Substring(0, ePos);
        }
        var dot = r.IndexOf('.');
        string digits;
        int decpt;
        if (dot < 0) { digits = r; decpt = r.Length + exp; }
        else { digits = r.Remove(dot, 1); decpt = dot + exp; }

        // Strip leading zeros (adjusting decpt) then trailing zeros.
        var lead = 0;
        while (lead < digits.Length - 1 && digits[lead] == '0') { lead++; decpt--; }
        digits = digits.Substring(lead);
        digits = digits.TrimEnd('0');
        if (digits.Length == 0) digits = "0";

        var sb = new StringBuilder();
        if (neg) sb.Append('-');

        if (decpt <= -4 || decpt > 16)
        {
            // Scientific: d[.ddd]e±XX
            sb.Append(digits[0]);
            if (digits.Length > 1) sb.Append('.').Append(digits, 1, digits.Length - 1);
            var e = decpt - 1;
            sb.Append('e').Append(e < 0 ? '-' : '+');
            sb.Append(Math.Abs(e).ToString("D2", CultureInfo.InvariantCulture));
        }
        else if (decpt <= 0)
        {
            sb.Append("0.").Append('0', -decpt).Append(digits);
        }
        else if (decpt >= digits.Length)
        {
            sb.Append(digits).Append('0', decpt - digits.Length).Append(".0");
        }
        else
        {
            sb.Append(digits, 0, decpt).Append('.').Append(digits, decpt, digits.Length - decpt);
        }
        return sb.ToString();
    }
}
