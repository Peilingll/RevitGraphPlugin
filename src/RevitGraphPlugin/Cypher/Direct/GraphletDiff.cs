// ── Pipeline: DIRECT-WRITE, live incremental sync ──
// Align one element's L side (from the graph) with its R side (the fresh re-conversion),
// Esser 2022 §3.4: matched nodes are the interface I (kept, values SET), the rest is the
// pushout (deleted / inserted). Matching seeds on GlobalIds and propagates along
// (rel_type, list_index) edges; anything undecidable → Structural, a full Replace.
namespace RevitGraphPlugin.Cypher;

public enum GraphletDiffKind
{
    /// <summary>Identical: store nothing (a noisy Revit modify).</summary>
    NoChange,
    /// <summary>Same structure, values differ: store a Modify.</summary>
    PropertyOnly,
    /// <summary>Interface plus pushout: store a Replace that copies only the pushout.</summary>
    Partial,
    /// <summary>Could not align: store a full Replace (both graphlets copied).</summary>
    Structural,
}

/// <summary>One value difference on a matched node.</summary>
/// <param name="Node">Portable name on the L side.</param>
/// <param name="NodeAfter">Portable name on the R side; equal to <paramref name="Node"/> with StableIds, kept for older chains.</param>
/// <param name="Key">Property name, or the inline child's rel_type when <paramref name="Inline"/>.</param>
/// <param name="ListIndex">The inline child's list_index; null for a property.</param>
/// <param name="Inline">True when the change is an inline child's wrappedValue, not a node property.</param>
/// <param name="P21Before">Local id on the L side: same-database fallback when no portable name resolves.</param>
/// <param name="P21After">Local id on the R side, same fallback.</param>
public sealed record PropertyChange(
    ContextRef Node, ContextRef NodeAfter, string Key, int? ListIndex,
    object? Before, object? After, bool Inline, int P21Before = 0, int P21After = 0);

/// <summary>The diff of one element's graphlet across a re-conversion. All empty for Structural.</summary>
/// <param name="Match">The interface I: L p21 → R p21.</param>
/// <param name="PushoutL">L nodes to delete.</param>
/// <param name="PushoutR">R nodes to insert.</param>
public sealed record GraphletDiffOutcome(
    GraphletDiffKind Kind,
    IReadOnlyList<PropertyChange> Changes,
    IReadOnlyDictionary<int, int> Match,
    IReadOnlyList<int> PushoutL,
    IReadOnlyList<int> PushoutR)
{
    public static readonly GraphletDiffOutcome Structural = new(
        GraphletDiffKind.Structural, Array.Empty<PropertyChange>(),
        new Dictionary<int, int>(), Array.Empty<int>(), Array.Empty<int>());

    /// <summary>True when the apply may keep the interface in place (anything but Structural).</summary>
    public bool IsAligned => Kind != GraphletDiffKind.Structural;
}

public static class GraphletDiff
{
    /// <summary>Properties that never count as a difference: bookkeeping columns, EntityType (a match precondition), GlobalId (the match seed).</summary>
    private static readonly HashSet<string> MaskedKeys = new(StringComparer.Ordinal)
    {
        "p21_id", "timestamp", "revit_element_id", "EntityType", "GlobalId",
    };

    public static GraphletDiffOutcome Compare(GraphletCapture before, IReadOnlyList<EntityData> after)
    {
        var left  = before.Nodes.ToDictionary(d => d.P21);
        var right = after.ToDictionary(d => d.P21);
        if (left.Count == 0 || right.Count == 0)
            return GraphletDiffOutcome.Structural;   // nothing to align: whole-graphlet replace

        // 1. Match; leftovers on either side are the pushout.
        if (!TryMatch(left, right, out var match))
            return GraphletDiffOutcome.Structural;

        var matchedR = match.Values.ToHashSet();
        var pushoutL = left.Keys.Where(p => !match.ContainsKey(p)).OrderBy(p => p).ToList();
        var pushoutR = right.Keys.Where(p => !matchedR.Contains(p)).OrderBy(p => p).ToList();

        // 2. Interface edges (to interface or external nodes) must match on both sides.
        if (!InterfaceEdgesEqual(left, right, match))
            return GraphletDiffOutcome.Structural;

        // 3. Value diff on matched pairs.
        var changes = new List<(int LeftP21, PropertyChange Change)>();
        foreach (var (lp21, rp21) in match)
        {
            var l = left[lp21];
            var r = right[rp21];

            foreach (var key in l.Properties.Keys.Union(r.Properties.Keys, StringComparer.Ordinal))
            {
                if (MaskedKeys.Contains(key)) continue;
                var lv = l.Properties.GetValueOrDefault(key);
                var rv = r.Properties.GetValueOrDefault(key);
                if (!ValuesEqual(lv, rv))
                    changes.Add((lp21, new PropertyChange(null!, null!, key, null, lv, rv, Inline: false)));
            }

            var lInl = l.Inlines.ToDictionary(i => (i.RelType, i.ListIndex));
            var rInl = r.Inlines.ToDictionary(i => (i.RelType, i.ListIndex));
            if (lInl.Count != rInl.Count) return GraphletDiffOutcome.Structural;
            foreach (var (key, li) in lInl)
            {
                if (!rInl.TryGetValue(key, out var ri)) return GraphletDiffOutcome.Structural;
                if (li.EntityType != ri.EntityType) return GraphletDiffOutcome.Structural;
                if (!ValuesEqual(li.WrappedValue, ri.WrappedValue))
                    changes.Add((lp21, new PropertyChange(
                        null!, null!, key.RelType, key.ListIndex,
                        li.WrappedValue, ri.WrappedValue, Inline: true)));
            }
        }

        var kind = pushoutL.Count == 0 && pushoutR.Count == 0
            ? (changes.Count == 0 ? GraphletDiffKind.NoChange : GraphletDiffKind.PropertyOnly)
            : GraphletDiffKind.Partial;

        if (changes.Count == 0)
            return new GraphletDiffOutcome(kind, Array.Empty<PropertyChange>(), match, pushoutL, pushoutR);

        // 4. Name every changed node portably on both sides.
        var changedL = changes.Select(c => c.LeftP21).Distinct().ToList();
        var namesL = NameNodes(left, changedL);
        var namesR = NameNodes(right, changedL.Select(lp21 => match[lp21]));
        var completed = new List<PropertyChange>();
        foreach (var (lp21, change) in changes)
        {
            if (!namesL.TryGetValue(lp21, out var refL)
                || !namesR.TryGetValue(match[lp21], out var refR))
                return GraphletDiffOutcome.Structural;   // unnameable → keep the full Replace
            completed.Add(change with
            {
                Node = refL, NodeAfter = refR,
                P21Before = lp21, P21After = match[lp21],
            });
        }

        return new GraphletDiffOutcome(
            kind,
            completed
                .OrderBy(c => c.Node.Path, StringComparer.Ordinal)
                .ThenBy(c => c.Inline)
                .ThenBy(c => c.Key, StringComparer.Ordinal)
                .ThenBy(c => c.ListIndex ?? -1)
                .ToList(),
            match, pushoutL, pushoutR);
    }

    /// <summary>
    /// Pair L with R nodes: seed on GlobalIds present on both sides, propagate forward
    /// along (rel_type, list_index) and backward where (rel_type, list_index, source
    /// EntityType) is unique. Unpaired nodes are the pushout; only a conflict fails.
    /// </summary>
    private static bool TryMatch(
        Dictionary<int, EntityData> left,
        Dictionary<int, EntityData> right,
        out Dictionary<int, int> match)
    {
        var pairs = new Dictionary<int, int>();
        match = pairs;
        var rTaken = new HashSet<int>();
        var queue = new Queue<(int L, int R)>();
        var ok = true;

        // null = conflict; false = not pairable (left unmatched); true = paired (or already so).
        bool? TryPair(int l, int r)
        {
            if (pairs.TryGetValue(l, out var existing)) return existing == r ? true : null;
            if (rTaken.Contains(r)) return null;
            var (ln, rn) = (left[l], right[r]);
            if (ln.EntityType != rn.EntityType || ln.Kind != rn.Kind) return false;
            pairs[l] = r;
            rTaken.Add(r);
            queue.Enqueue((l, r));
            return true;
        }

        // Seeds; no seed at all → nothing to align.
        var lByGid = GlobalIdIndex(left.Values);
        var rByGid = GlobalIdIndex(right.Values);
        foreach (var (gid, lp21) in lByGid)
            if (rByGid.TryGetValue(gid, out var rp21) && TryPair(lp21, rp21) is null)
                return false;
        if (queue.Count == 0) return false;

        // Reverse adjacency (internal edges only), built once per side.
        var lIncoming = IncomingIndex(left);
        var rIncoming = IncomingIndex(right);

        while (queue.Count > 0 && ok)
        {
            var (l, r) = queue.Dequeue();

            // Forward: (rel_type, list_index) → target.
            var lFwd = InternalEdgeMap(left[l], left, ref ok);
            var rFwd = InternalEdgeMap(right[r], right, ref ok);
            if (!ok) return false;
            foreach (var (key, lTarget) in lFwd)
                if (rFwd.TryGetValue(key, out var rTarget) && TryPair(lTarget, rTarget) is null)
                    return false;

            // Backward: (rel_type, list_index, source EntityType) → source, where unique.
            var lBack = UniqueIncoming(lIncoming, l, left);
            var rBack = UniqueIncoming(rIncoming, r, right);
            foreach (var (key, lSource) in lBack)
                if (rBack.TryGetValue(key, out var rSource) && TryPair(lSource, rSource) is null)
                    return false;
        }

        return ok;
    }

    private static Dictionary<string, int> GlobalIdIndex(IEnumerable<EntityData> nodes)
    {
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        var dupes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var n in nodes)
        {
            if (n.GlobalId is not { Length: > 0 } gid) continue;
            if (!index.TryAdd(gid, n.P21)) dupes.Add(gid);
        }
        foreach (var gid in dupes) index.Remove(gid);
        return index;
    }

    /// <summary>Internal outgoing edges keyed (rel_type, list_index); duplicates flip <paramref name="ok"/>.</summary>
    private static Dictionary<(string, int), int> InternalEdgeMap(
        EntityData node, Dictionary<int, EntityData> side, ref bool ok)
    {
        var map = new Dictionary<(string, int), int>();
        foreach (var e in node.Edges)
        {
            if (!side.ContainsKey(e.TargetP21)) continue;   // external: checked in InterfaceEdgesEqual
            if (!map.TryAdd((e.RelType, e.ListIndex), e.TargetP21)) ok = false;
        }
        return map;
    }

    private static Dictionary<int, List<EdgeData>> IncomingIndex(Dictionary<int, EntityData> side)
    {
        var index = new Dictionary<int, List<EdgeData>>();
        foreach (var node in side.Values)
            foreach (var e in node.Edges)
                if (side.ContainsKey(e.TargetP21))
                {
                    if (!index.TryGetValue(e.TargetP21, out var list))
                        index[e.TargetP21] = list = new List<EdgeData>();
                    list.Add(e);
                }
        return index;
    }

    private static Dictionary<(string, int, string), int> UniqueIncoming(
        Dictionary<int, List<EdgeData>> incoming, int target, Dictionary<int, EntityData> side)
    {
        var map = new Dictionary<(string, int, string), int>();
        var ambiguous = new HashSet<(string, int, string)>();
        foreach (var e in incoming.GetValueOrDefault(target) ?? (IEnumerable<EdgeData>)Array.Empty<EdgeData>())
        {
            var key = (e.RelType, e.ListIndex, side[e.SourceP21].EntityType);
            if (!map.TryAdd(key, e.SourceP21)) ambiguous.Add(key);
        }
        foreach (var key in ambiguous) map.Remove(key);
        return map;
    }

    /// <summary>For every interface source node, its edges to interface / external nodes must be identical on both sides; edges touching the pushout are glue.</summary>
    private static bool InterfaceEdgesEqual(
        Dictionary<int, EntityData> left, Dictionary<int, EntityData> right, Dictionary<int, int> match)
    {
        var matchedR = match.Values.ToHashSet();

        static List<(int, string, int, int)> Signature(
            Dictionary<int, EntityData> side, IEnumerable<int> sources,
            Func<int, int> mapNode, Func<int, bool> isInterface, Func<int, bool> isInternal)
        {
            var sig = new List<(int, string, int, int)>();
            foreach (var src in sources)
                foreach (var e in side[src].Edges)
                {
                    if (isInternal(e.TargetP21) && !isInterface(e.TargetP21)) continue;  // → pushout: glue
                    sig.Add((mapNode(src), e.RelType, e.ListIndex,
                             isInternal(e.TargetP21) ? mapNode(e.TargetP21) : -e.TargetP21));
                }
            sig.Sort();
            return sig;
        }

        var lSig = Signature(left, match.Keys, p => match[p], match.ContainsKey, left.ContainsKey);
        var rSig = Signature(right, matchedR, p => p, matchedR.Contains, right.ContainsKey);
        return lSig.SequenceEqual(rSig);
    }

    /// <summary>Numeric-normalized equality: L comes from Neo4j (long), R from the walker (int).</summary>
    private static bool ValuesEqual(object? a, object? b)
    {
        if (a is null || b is null) return ReferenceEquals(a, b);
        if (IsNumeric(a) && IsNumeric(b))
            return Convert.ToDouble(a).Equals(Convert.ToDouble(b));
        return a.Equals(b);
    }

    private static bool IsNumeric(object value) => value is sbyte or byte or short or ushort
        or int or uint or long or ulong or float or double or decimal;

    /// <summary>
    /// Portable within-graphlet names for the given nodes: shortest path from a
    /// GlobalId-bearing anchor, same total order as ContextResolver. Unreachable nodes get
    /// no name (caller falls back to Structural).
    /// </summary>
    private static Dictionary<int, ContextRef> NameNodes(
        Dictionary<int, EntityData> left, IEnumerable<int> wanted)
    {
        var best = new Dictionary<int, (int Depth, int KindRank, ContextRef Ref)>();

        foreach (var anchor in left.Values
                     .Where(n => n.GlobalId is { Length: > 0 }
                                 && n.Kind is NodeKind.Primary or NodeKind.Connection))
        {
            var kind = anchor.Kind == NodeKind.Primary
                ? ContextAnchorKind.Primary : ContextAnchorKind.Connection;
            var kindRank = kind == ContextAnchorKind.Primary ? 0 : 1;

            // BFS in sorted edge order: first visit is the shortest path, deterministically.
            var visited = new Dictionary<int, List<ContextStep>> { [anchor.P21] = new() };
            var queue = new Queue<int>();
            queue.Enqueue(anchor.P21);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                foreach (var e in left[current].Edges
                             .Where(e => left.ContainsKey(e.TargetP21))
                             .OrderBy(e => e.RelType, StringComparer.Ordinal)
                             .ThenBy(e => e.ListIndex))
                {
                    if (visited.ContainsKey(e.TargetP21)) continue;
                    var steps = new List<ContextStep>(visited[current])
                    {
                        new(e.RelType, e.ListIndex, left[e.TargetP21].EntityType),
                    };
                    visited[e.TargetP21] = steps;
                    queue.Enqueue(e.TargetP21);
                }
            }

            foreach (var (p21, steps) in visited)
            {
                // Total order: depth → anchor kind → Path (anchor GlobalId + steps).
                var candidate = new ContextRef(kind, anchor.GlobalId!, steps);
                if (!best.TryGetValue(p21, out var incumbent)
                    || steps.Count < incumbent.Depth
                    || (steps.Count == incumbent.Depth
                        && (kindRank < incumbent.KindRank
                            || (kindRank == incumbent.KindRank
                                && string.CompareOrdinal(candidate.Path, incumbent.Ref.Path) < 0))))
                {
                    best[p21] = (steps.Count, kindRank, candidate);
                }
            }
        }

        return wanted
            .Where(best.ContainsKey)
            .ToDictionary(p21 => p21, p21 => best[p21].Ref);
    }
}
