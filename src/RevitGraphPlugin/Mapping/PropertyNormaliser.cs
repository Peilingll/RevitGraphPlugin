using System.Globalization;
using System.Text;

namespace RevitGraphPlugin.Mapping;

/// <summary>
/// ConMan2 property normalisation rules (related-work.md §2.4):
///   None         → "$"
///   list&lt;prim&gt; → "(a,b,c)" (Python-tuple-style stringification)
///   nested list  → stringified literal
///   primitive    → passed through (Neo4j driver handles native types)
/// Strings are always JSON-escaped so single quotes / Unicode survive
/// (avoids related-work.md §3.4 item 5).
/// </summary>
public static class PropertyNormaliser
{
    public static object? Normalise(object? value)
    {
        if (value is null)
            return "$";

        if (value is string s)
            return s;

        if (value is IEnumerable<object?> seq && value is not string)
            return TupleString(seq);

        // Boxed primitive collections (e.g. double[]) come through as IEnumerable
        // but with non-object element type — handle generically.
        if (value is System.Collections.IEnumerable nonGenericSeq && value is not string)
        {
            var items = new List<object?>();
            foreach (var item in nonGenericSeq) items.Add(item);
            return TupleString(items);
        }

        return value;
    }

    private static string TupleString(IEnumerable<object?> items)
    {
        var sb = new StringBuilder("(");
        var first = true;
        foreach (var item in items)
        {
            if (!first) sb.Append(',');
            sb.Append(Stringify(item));
            first = false;
        }
        sb.Append(')');
        return sb.ToString();
    }

    private static string Stringify(object? item) => item switch
    {
        null => "$",
        string s => s,
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => item.ToString() ?? "$",
    };
}
