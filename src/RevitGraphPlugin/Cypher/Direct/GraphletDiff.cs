// ── Pipeline: DIRECT-WRITE, live incremental sync ──
// Rule persistence step 3a (doc_process/2026-08-02-plan-rule-persistence.md §6):
// decide whether a Replace was really a property-only modify. The L side (captured
// from the graph) and the R side (the fresh re-conversion walk) describe the same
// element, so if they are structurally identical and differ only in property / inline
// values, the STORED rule can be a small Modify (ConMan2's semantic patch) instead of
// two full graphlet copies. The applied rule is untouched either way — the current-
// state graph is still delete+rebuild (plan §6, shallow option (a)).
//
// Matching L nodes to R nodes is the part ConMan2's run_diff pays dearly for
// (equivalent_to inference over the whole model); here it is nearly free because both
// sides are the SAME element's graphlet: seed on stable GlobalIds (the product — its
// GlobalId derives from the Revit UniqueId), then propagate along edges whose
// (rel_type, list_index) keys are unique. ggifc-generated GlobalIds on rel/pset nodes
// are random per conversion, so they are masked like compare_neo4j masks them.
// Anything that does not line up 1:1 → Structural, and the caller stores a full
// Replace: Modify is a shortcut, never a requirement.
namespace RevitGraphPlugin.Cypher;

public enum GraphletDiffKind
{
    /// <summary>Semantically identical — nothing worth storing (a noisy Revit modify).</summary>
    NoChange,
    /// <summary>Same structure, only property / inline values differ: store a Modify.</summary>
    PropertyOnly,
    /// <summary>Anything else (nodes added/removed, edges differ, could not align): store the full Replace.</summary>
    Structural,
}

/// <summary>
/// One value difference on a matched node. <paramref name="Node"/> is the node's portable
/// name INSIDE the graphlet — anchored on the nearest GlobalId-bearing node of the L side
/// (the DPO precondition: replay resolves it against the graph state the preceding rules
/// produced). <paramref name="Inline"/> distinguishes an inline child's wrappedValue
/// (key = the inline's rel_type, e.g. NominalValue, at <paramref name="ListIndex"/>)
/// from a plain node property (<paramref name="ListIndex"/> null).
/// </summary>
public sealed record PropertyChange(
    ContextRef Node, string Key, int? ListIndex, object? Before, object? After, bool Inline);

public sealed record GraphletDiffOutcome(GraphletDiffKind Kind, IReadOnlyList<PropertyChange> Changes)
{
    public static readonly GraphletDiffOutcome Structural =
        new(GraphletDiffKind.Structural, Array.Empty<PropertyChange>());
}

public static class GraphletDiff
{
    /// <summary>
    /// Node properties that never count as a semantic difference: identity/bookkeeping
    /// columns (p21 renumbers on every re-conversion; revit_element_id and timestamp are
    /// plugin columns), EntityType (enforced as a match precondition instead), and
    /// GlobalId (stable ones are the match seeds; ggifc-random ones are churn — masked
    /// exactly like compare_neo4j masks them).
    /// </summary>
    private static readonly HashSet<string> MaskedKeys = new(StringComparer.Ordinal)
    {
        "p21_id", "timestamp", "revit_element_id", "EntityType", "GlobalId",
    };

    public static GraphletDiffOutcome Compare(GraphletCapture before, IReadOnlyList<EntityData> after)
    {
        var left  = before.Nodes.ToDictionary(d => d.P21);
        var right = after.ToDictionary(d => d.P21);
        if (left.Count == 0 || left.Count != right.Count)
            return GraphletDiffOutcome.Structural;

        // ── 1. Match: seed on shared GlobalIds, propagate along unambiguous edges ──
        if (!TryMatch(left, right, out var match))
            return GraphletDiffOutcome.Structural;

        // ── 2. Structure must be identical under the match ──
        // One global multiset comparison covers internal edges in both directions and
        // outgoing glue (external targets keep their p21 across the two sides: context
        // entities are old ggifc objects whose StepIds do not move on re-conversion).
        if (!EdgesEqual(left, right, match))
            return GraphletDiffOutcome.Structural;

        // ── 3. Value diff on matched pairs ──
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
                    changes.Add((lp21, new PropertyChange(null!, key, null, lv, rv, Inline: false)));
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
                        null!, key.RelType, key.ListIndex, li.WrappedValue, ri.WrappedValue, Inline: true)));
            }
        }

        if (changes.Count == 0)
            return new GraphletDiffOutcome(GraphletDiffKind.NoChange, Array.Empty<PropertyChange>());

        // ── 4. Name every changed node portably (within-graphlet unique path, L side) ──
        var names = NameNodes(left, changes.Select(c => c.LeftP21).Distinct());
        var completed = new List<PropertyChange>();
        foreach (var (lp21, change) in changes)
        {
            if (!names.TryGetValue(lp21, out var contextRef))
                return GraphletDiffOutcome.Structural;   // unnameable → keep the full Replace
            completed.Add(change with { Node = contextRef });
        }

        return new GraphletDiffOutcome(
            GraphletDiffKind.PropertyOnly,
            completed
                .OrderBy(c => c.Node.Path, StringComparer.Ordinal)
                .ThenBy(c => c.Inline)
                .ThenBy(c => c.Key, StringComparer.Ordinal)
                .ThenBy(c => c.ListIndex ?? -1)
                .ToList());
    }

    /// <summary>
    /// Pair every L node with exactly one R node. Seeds: GlobalIds present on both sides
    /// (unique per side). Propagation: from each matched pair, follow internal edges
    /// forward — (rel_type, list_index) is unique per source node, an EXPRESS attribute —
    /// and backward where (rel_type, list_index, source EntityType) is unique per target
    /// (this reaches the IfcRelDefinesByProperties-style nodes that only POINT AT the
    /// product and are reachable no other way). Any conflict or leftover → no match.
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

        bool TryPair(int l, int r)
        {
            if (pairs.TryGetValue(l, out var existing)) return existing == r;
            if (!rTaken.Add(r)) return false;
            var (ln, rn) = (left[l], right[r]);
            if (ln.EntityType != rn.EntityType || ln.Kind != rn.Kind) return false;
            pairs[l] = r;
            queue.Enqueue((l, r));
            return true;
        }

        // Seeds. A GlobalId duplicated within one side, or present on one side only,
        // cannot seed; a stable GlobalId that CHANGED shows up as "one side only" and
        // the node must then be reached structurally or the match fails — conservative.
        var lByGid = GlobalIdIndex(left.Values);
        var rByGid = GlobalIdIndex(right.Values);
        foreach (var (gid, lp21) in lByGid)
            if (rByGid.TryGetValue(gid, out var rp21) && !TryPair(lp21, rp21))
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
            if (!ok || lFwd.Count != rFwd.Count) return false;
            foreach (var (key, lTarget) in lFwd)
            {
                if (!rFwd.TryGetValue(key, out var rTarget)) return false;
                if (!TryPair(lTarget, rTarget)) return false;
            }

            // Backward: (rel_type, list_index, source EntityType) → source, where unique.
            var lBack = UniqueIncoming(lIncoming, l, left);
            var rBack = UniqueIncoming(rIncoming, r, right);
            foreach (var (key, lSource) in lBack)
                if (rBack.TryGetValue(key, out var rSource) && !TryPair(lSource, rSource))
                    return false;
            // Keys unique on one side only, or ambiguous: not an error here — the edge
            // multiset check catches real differences, and unmatched nodes fail coverage.
        }

        return ok && pairs.Count == left.Count;
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
            if (!side.ContainsKey(e.TargetP21)) continue;   // external: checked in EdgesEqual
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

    /// <summary>
    /// All edges, translated through the match, as one multiset: internal edges become
    /// (match[src], rel, idx, match[tgt]); edges to external context keep their target
    /// p21. Equal multisets ⇔ the two graphlets wire up identically.
    /// </summary>
    private static bool EdgesEqual(
        Dictionary<int, EntityData> left, Dictionary<int, EntityData> right, Dictionary<int, int> match)
    {
        static List<(int, string, int, int)> Signature(
            Dictionary<int, EntityData> side, Func<int, int> mapNode, Func<int, bool> isInternal)
        {
            var sig = new List<(int, string, int, int)>();
            foreach (var node in side.Values)
                foreach (var e in node.Edges)
                    sig.Add((mapNode(e.SourceP21), e.RelType, e.ListIndex,
                             isInternal(e.TargetP21) ? mapNode(e.TargetP21) : -e.TargetP21));
            sig.Sort();
            return sig;
        }

        var lSig = Signature(left, p => match[p], left.ContainsKey);
        var rSig = Signature(right, p => p, right.ContainsKey);
        return lSig.SequenceEqual(rSig);
    }

    /// <summary>
    /// Values from two worlds — Neo4j read-back (L: long/double/string/bool) and the
    /// EntityWalker (R: int/double/string/bool) — so numeric types must be normalized
    /// before comparing, or every int-vs-long pair would read as a change.
    /// </summary>
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
    /// Portable within-graphlet names for the given L nodes: anchor on a GlobalId-bearing
    /// Primary/Connection node, walk outgoing internal edges. Total order over candidates
    /// (depth → anchor kind → anchor GlobalId → step list) mirrors ContextResolver, so the
    /// same graphlet always yields the same name. Every IFC entity hangs off some IfcRoot,
    /// so reachable coverage is the norm; a node no anchor reaches gets no name and the
    /// caller falls back to Structural.
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

            // BFS from this anchor; per-anchor first visit is the shortest, deterministic
            // because edges are explored in sorted order.
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
                // Total order: depth → anchor kind (Primary first) → anchor GlobalId →
                // step list; the last two collapse into one ordinal Path comparison,
                // since Path = anchor + steps.
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
