// ── Pipeline: DIRECT-WRITE (Revit → ggifc tree → Cypher → Neo4j; no temp IFC) ──
// The graph's node key format, in one place. ConMan2 stores a STEP id as the string
// "#123" (p21_id); the walkers carry it as an int. Both directions are needed often
// enough — and are easy enough to get subtly wrong — to be worth naming.
namespace RevitGraphPlugin.Cypher;

public static class P21Id
{
    /// <summary>STEP id → the graph's <c>p21_id</c> property ("#123").</summary>
    public static string Of(int stepId) => $"#{stepId}";

    /// <summary>
    /// <c>p21_id</c> → STEP id. False for anything that is not a "#&lt;digits&gt;" string
    /// (notably InlineNodes, which have no p21_id at all).
    /// </summary>
    public static bool TryParse(object? value, out int stepId)
    {
        stepId = 0;
        return value is string s
            && s.Length > 1
            && s[0] == '#'
            && int.TryParse(s.AsSpan(1), out stepId);
    }
}
