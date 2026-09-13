using Neo4j.Driver;

// ── Pipeline: DIRECT-WRITE, live incremental sync ──
// Resolve the p21s a rule references but does not own into portable ContextRefs, and
// back. IfcRoot context anchors directly on its GlobalId; shared boilerplate without a
// GlobalId (OwnerHistory, placements, contexts) is reached in a few hops from one.
namespace RevitGraphPlugin.Cypher;

public static class ContextResolver
{
    /// <summary>Max hops from an anchor to a target without a GlobalId; deeper paths are too fragile.</summary>
    public const int MaxPathDepth = 4;

    /// <summary>
    /// Name each of <paramref name="targets"/> portably. Must run before the rule mutates
    /// anything; paths never route through <paramref name="exclude"/> (the rule's own
    /// graphlet). Unresolvable targets are absent from the result.
    /// </summary>
    public static async Task<IReadOnlyDictionary<int, ContextRef>> ResolveAsync(
        IAsyncQueryRunner tx,
        string timestamp,
        IReadOnlyCollection<int> targets,
        IReadOnlySet<int> exclude)
    {
        var resolved = new Dictionary<int, ContextRef>();
        if (targets.Count == 0) return resolved;

        // Pass 1: IfcRoot targets anchor directly.
        var needsPath = new List<int>();
        var rows = await (await tx.RunAsync(
            @"UNWIND $p21s AS p
              MATCH (n:GenericNode {timestamp: $ts, p21_id: p})
              RETURN p AS p21, n.GlobalId AS gid, labels(n) AS labels",
            new { ts = timestamp, p21s = targets.Select(P21Id.Of).ToList() })).ToListAsync();

        foreach (var row in rows)
        {
            if (!P21Id.TryParse(row["p21"].As<string>(), out var p21)) continue;

            var globalId = row["gid"]?.As<string>();
            var kind = AnchorKind(row["labels"].As<List<string>>());
            if (kind is not null && !string.IsNullOrEmpty(globalId))
                resolved[p21] = ContextRef.Anchor(kind.Value, globalId!);
            else
                needsPath.Add(p21);
        }

        // Pass 2: walk in from the best anchor.
        var excluded = exclude.Select(P21Id.Of).ToList();
        foreach (var p21 in needsPath)
        {
            var contextRef = await ShortestAnchoredPathAsync(tx, timestamp, p21, excluded);
            if (contextRef is not null) resolved[p21] = contextRef;
        }

        return resolved;
    }

    /// <summary>Find the node a <see cref="ContextRef"/> names in <paramref name="timestamp"/>'s graph (ConMan2's <c>find_node_from_unique_path</c>), or null.</summary>
    public static async Task<int?> FindAsync(
        IAsyncQueryRunner tx, string timestamp, ContextRef contextRef)
    {
        var anchorLabel = contextRef.AnchorKind == ContextAnchorKind.Primary
            ? "PrimaryNode" : "ConnectionNode";

        var anchor = await (await tx.RunAsync(
            $@"MATCH (n:{anchorLabel} {{timestamp: $ts, GlobalId: $gid}})
               RETURN n.p21_id AS p21 LIMIT 1",
            new { ts = timestamp, gid = contextRef.AnchorGlobalId })).ToListAsync();
        if (anchor.Count == 0 || !P21Id.TryParse(anchor[0]["p21"].As<string>(), out var current))
            return null;

        foreach (var step in contextRef.Steps)
        {
            var hop = await (await tx.RunAsync(
                @"MATCH (a:GenericNode {timestamp: $ts, p21_id: $p21})
                        -[:rel {rel_type: $rt, list_index: $li}]->(b:GenericNode {EntityType: $et})
                  RETURN b.p21_id AS p21 LIMIT 1",
                new
                {
                    ts = timestamp, p21 = P21Id.Of(current),
                    rt = step.RelType, li = step.ListIndex, et = step.EntityType,
                })).ToListAsync();

            if (hop.Count == 0 || !P21Id.TryParse(hop[0]["p21"].As<string>(), out current))
                return null;
        }

        return current;
    }

    private static async Task<ContextRef?> ShortestAnchoredPathAsync(
        IAsyncQueryRunner tx, string timestamp, int p21, IReadOnlyList<string> excluded)
    {
        // Rank anchors by stability first, then depth. Nodes without revit_element_id
        // (project / site / building / storey, containment rels, contexts) are built once
        // per session and never re-converted, so a name anchored on them survives later
        // edits; an element-owned anchor dies when that element is re-converted.
        // ORDER BY must be a total order (a shared node like IfcOwnerHistory has dozens of
        // equally short paths) so LIMIT 1 picks the same candidate every time.
        // MaxPathDepth is inlined: Cypher rejects a parameter as a var-length bound.
        var cypher = $@"
MATCH path = allShortestPaths(
        (a:GenericNode {{timestamp: $ts}})-[:rel*1..{MaxPathDepth}]->(x:GenericNode {{timestamp: $ts, p21_id: $p21}}))
WHERE (a:PrimaryNode OR a:ConnectionNode) AND a.GlobalId IS NOT NULL
  AND NONE(n IN nodes(path) WHERE n.p21_id IN $excluded)
WITH a.GlobalId AS gid,
     CASE WHEN a.revit_element_id IS NULL THEN 0 ELSE 1 END AS stability_rank,
     CASE WHEN a:PrimaryNode THEN 0 ELSE 1 END AS kind_rank,
     labels(a) AS anchor_labels,
     [r IN relationships(path) | r.rel_type]   AS rel_types,
     [r IN relationships(path) | r.list_index] AS list_indexes,
     [n IN tail(nodes(path)) | n.EntityType]   AS entity_types,
     length(path) AS depth
ORDER BY stability_rank, depth, kind_rank, gid, rel_types, list_indexes, entity_types
LIMIT 1
RETURN gid, anchor_labels, rel_types, list_indexes, entity_types";

        var rows = await (await tx.RunAsync(
            cypher, new { ts = timestamp, p21 = P21Id.Of(p21), excluded = excluded.ToList() })).ToListAsync();
        if (rows.Count == 0) return null;

        var row = rows[0];
        var kind = AnchorKind(row["anchor_labels"].As<List<string>>());
        if (kind is null) return null;

        var relTypes    = row["rel_types"].As<List<string>>();
        var listIndexes = row["list_indexes"].As<List<int>>();
        var entityTypes = row["entity_types"].As<List<string>>();
        if (relTypes.Count != listIndexes.Count || relTypes.Count != entityTypes.Count) return null;

        var steps = new List<ContextStep>(relTypes.Count);
        for (var i = 0; i < relTypes.Count; i++)
            steps.Add(new ContextStep(relTypes[i], listIndexes[i], entityTypes[i]));

        return new ContextRef(kind.Value, row["gid"].As<string>(), steps);
    }

    private static ContextAnchorKind? AnchorKind(IEnumerable<string> labels)
    {
        foreach (var label in labels)
        {
            if (label == "PrimaryNode") return ContextAnchorKind.Primary;
            if (label == "ConnectionNode") return ContextAnchorKind.Connection;
        }
        return null;   // Secondary / Inline: no GlobalId, must be reached by a path.
    }
}
