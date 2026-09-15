using System.Globalization;
using System.Text;

// ── Pipeline: DIRECT-WRITE (Revit → ggifc tree → Cypher → Neo4j; no temp IFC) ──
// Lossless STEP-line source for node properties: ggifc's property getters collapse the
// unset / derived / empty-string distinctions that its Part-21 output preserves.
namespace RevitGraphPlugin.Cypher;

/// <summary>One parsed parameter of a STEP (Part 21) line.</summary>
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

    /// <summary>A typed value such as <c>IFCLABEL('x')</c>; becomes an inline node via reflection, not a property.</summary>
    public sealed record Typed(string Keyword, IReadOnlyList<StepToken> Args) : StepToken;
}

/// <summary>One parsed STEP line: its entity id, class keyword, and ordered arguments.</summary>
public sealed record StepLine(int Id, string Keyword, IReadOnlyList<StepToken> Arguments);

/// <summary>
/// Parses a STEP (Part 21) line into <see cref="StepToken"/>s and maps primitive tokens to
/// the property values ConMan2 stores (Python <c>str()</c> formatting).
/// </summary>
public static class StepLineParser
{
    /// <summary>Parse a STEP line (<c>#4=IFCWALL('guid',#325,…);</c>) into id, class keyword and argument tokens; <c>#id=</c> and <c>;</c> are optional.</summary>
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

    // 'text' with '' as an escaped quote and the ISO 10303-21 backslash escapes decoded
    // to the characters they encode (what ifcopenshell hands ConMan2):
    //   \\       backslash          \X\HH       one ISO 8859-1 byte
    //   \S\c     c + 0x80           \X2\…\X0\   UTF-16BE, 4 hex digits per unit
    //   \N\      newline            \X4\…\X0\   UTF-32BE, 8 hex digits per code point
    //   \P?\     code page directive, ignored (Revit never emits one)
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
            {
                i = ParseEscape(s, i, sb, line);
                continue;
            }
            sb.Append(c);
            i++;
        }
        throw new FormatException($"Unterminated string in STEP line: {line}");
    }

    /// <summary>Decode one backslash escape starting at <paramref name="i"/>; returns the index after it.</summary>
    private static int ParseEscape(string s, int i, StringBuilder sb, string line)
    {
        static FormatException Bad(int at, string line) =>
            new($"Malformed STEP escape (near index {at}) in line: {line}");

        if (i + 1 >= s.Length) throw Bad(i, line);
        switch (s[i + 1])
        {
            case '\\':
                sb.Append('\\');
                return i + 2;

            case 'N' when i + 2 < s.Length && s[i + 2] == '\\':
                sb.Append('\n');
                return i + 3;

            case 'S' when i + 3 < s.Length && s[i + 2] == '\\':
                sb.Append((char)(s[i + 3] + 0x80));
                return i + 4;

            case 'P' when i + 3 < s.Length && s[i + 3] == '\\':
                return i + 4;

            case 'X' when i + 2 < s.Length && s[i + 2] == '\\':
                sb.Append((char)ParseHex(s, i + 3, 2, line));
                return i + 5;

            case 'X' when i + 3 < s.Length && s[i + 2] is '2' or '4' && s[i + 3] == '\\':
            {
                var width = s[i + 2] == '2' ? 4 : 8;
                var j = i + 4;
                var end = s.IndexOf(@"\X0\", j, StringComparison.Ordinal);
                if (end < 0 || (end - j) % width != 0) throw Bad(i, line);
                for (; j < end; j += width)
                {
                    var code = ParseHex(s, j, width, line);
                    if (width == 4) sb.Append((char)code);
                    else sb.Append(char.ConvertFromUtf32(code));
                }
                return end + 4;
            }

            default:
                throw Bad(i, line);
        }
    }

    private static int ParseHex(string s, int start, int length, string line)
    {
        if (start + length > s.Length
            || !int.TryParse(s.AsSpan(start, length), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
            throw new FormatException($"Malformed STEP hex escape (near index {start}) in line: {line}");
        return value;
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

    /// <summary>Primitive slot → the value ConMan2 stores (string / long / double / bool). False for slots that become edges or inline nodes.</summary>
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
        // Empty aggregate folds to "$".
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

    // Python str(tuple): ", "-joined, trailing comma for a single element.
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
            switch (c)
            {
                case '\\': sb.Append(@"\\"); break;
                case '\'': sb.Append(@"\'"); break;
                case '\n': sb.Append(@"\n"); break;   // a multi-line address line
                case '\r': sb.Append(@"\r"); break;
                case '\t': sb.Append(@"\t"); break;
                default: sb.Append(c); break;
            }
        }
        sb.Append('\'');
        return sb.ToString();
    }

    /// <summary>Format a double exactly as CPython's <c>repr(float)</c> (shortest round-trip digits, scientific when <c>decpt &lt;= -4 || decpt &gt; 16</c>).</summary>
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
