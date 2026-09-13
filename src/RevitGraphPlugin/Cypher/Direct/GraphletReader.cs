using Neo4j.Driver;

// ── Pipeline: DIRECT-WRITE, live incremental sync ──
// Read an element's graphlet back out of the graph (the DPO L side). The ggifc mirror
// is already mutated by rule-build time; the graph still holds what the rule deletes.
namespace RevitGraphPlugin.Cypher;

/// <summary>The L side of one <see cref="GraphRule"/>: the element's graphlet as the graph held it before the rule ran.</summary>
/// <param name="Nodes">The element-owned nodes with their outgoing edges and inline children (same shape the insert path produces).</param>
/// <param name="IncomingGlue">Edges from context into the graphlet (e.g. the containment rel's RelatedElements edge); DETACH DELETE destroys these too.</param>
/// <param name="SharedDeleted">The SharedDelete nodes, read by p21 — part of L but not element-owned.</param>
public sealed record GraphletCapture(
    IReadOnlyList<EntityData> Nodes,
    IReadOnlyList<EdgeData> IncomingGlue,
    IReadOnlyList<EntityData>? SharedDeleted = null)
{
    public IReadOnlyList<EntityData> SharedDeletedOrEmpty
        => SharedDeleted ?? Array.Empty<EntityData>();
}

public static class GraphletReader
{
    /// <summary>Read everything owned by <paramref name="elementId"/>. Must run in the rule's transaction before the delete. Empty capture if the element owns nothing.</summary>
    public static async Task<GraphletCapture> ReadOwnedAsync(
        IAsyncQueryRunner tx, string timestamp, long elementId)
    {
        var args = new { ts = timestamp, eid = elementId };

        // 1. Owned nodes (inline nodes come back as their parent's InlineData in 2).
        var byP21 = new Dictionary<int, EntityData>();
        var nodeRows = await (await tx.RunAsync(
            @"MATCH (n:GenericNode {timestamp: $ts, revit_element_id: $eid})
              RETURN n AS n, labels(n) AS labels", args)).ToListAsync();

        foreach (var row in nodeRows)
        {
            var props = row["n"].As<INode>().Properties.ToDictionary(kv => kv.Key, kv => kv.Value);
            if (!P21Id.TryParse(props.GetValueOrDefault("p21_id"), out var p21)) continue;

            byP21[p21] = new EntityData(
                P21:        p21,
                EntityType: props.GetValueOrDefault("EntityType") as string ?? string.Empty,
                GlobalId:   props.GetValueOrDefault("GlobalId") as string,
                Kind:       NodeClassifier.KindFromLabels(row["labels"].As<List<string>>()),
                Properties: props,
                Edges:      new List<EdgeData>(),
                Inlines:    new List<InlineData>());
        }

        if (byP21.Count == 0)
            return new GraphletCapture(Array.Empty<EntityData>(), Array.Empty<EdgeData>());

        // 2. Outgoing edges, including those to context (the outgoing glue).
        var edgeRows = await (await tx.RunAsync(
            @"MATCH (n:GenericNode {timestamp: $ts, revit_element_id: $eid})-[e:rel]->(m)
              RETURN n.p21_id AS src, e.rel_type AS rel_type, e.list_index AS list_index,
                     m.p21_id AS tgt, m.EntityType AS tgt_type, m.wrappedValue AS wrapped,
                     'InlineNode' IN labels(m) AS inline", args)).ToListAsync();

        foreach (var row in edgeRows)
        {
            if (!P21Id.TryParse(row["src"].As<string>(), out var src)) continue;
            if (!byP21.TryGetValue(src, out var owner)) continue;

            var relType   = row["rel_type"].As<string>();
            var listIndex = row["list_index"].As<int>();

            if (row["inline"].As<bool>())
            {
                owner.Inlines.Add(new InlineData(
                    src, relType, listIndex,
                    row["tgt_type"].As<string>() ?? string.Empty,
                    row["wrapped"]?.As<object>() ?? "$",
                    OwnerElementId: elementId));
            }
            else if (P21Id.TryParse(row["tgt"], out var tgt))
            {
                owner.Edges.Add(new EdgeData(src, relType, listIndex, tgt));
            }
        }

        // 3. Incoming glue: edges into the graphlet from nodes not owned by this element.
        var glueRows = await (await tx.RunAsync(
            @"MATCH (src:GenericNode {timestamp: $ts})-[e:rel]->(n:GenericNode {timestamp: $ts, revit_element_id: $eid})
              WHERE src.revit_element_id IS NULL OR src.revit_element_id <> $eid
              RETURN src.p21_id AS src, e.rel_type AS rel_type, e.list_index AS list_index,
                     n.p21_id AS tgt", args)).ToListAsync();

        var glue = new List<EdgeData>();
        foreach (var row in glueRows)
        {
            if (!P21Id.TryParse(row["src"], out var src)) continue;
            if (!P21Id.TryParse(row["tgt"], out var tgt)) continue;
            glue.Add(new EdgeData(src, row["rel_type"].As<string>(), row["list_index"].As<int>(), tgt));
        }

        return new GraphletCapture(
            byP21.Values.OrderBy(d => d.P21).ToList(),
            glue);
    }

    /// <summary>Read specific nodes by p21, with outgoing edges and inline children.</summary>
    public static async Task<List<EntityData>> ReadByP21Async(
        IAsyncQueryRunner tx, string timestamp, IReadOnlyCollection<string> p21Ids)
    {
        if (p21Ids.Count == 0) return new List<EntityData>();
        return await ReadSetAsync(tx,
            "MATCH (n:GenericNode {timestamp: $ts}) WHERE n.p21_id IN $p21s",
            new { ts = timestamp, p21s = p21Ids.ToList() });
    }

    /// <summary>Read every node of one timestamp namespace (a stored rule's -L / -R copies).</summary>
    public static async Task<List<EntityData>> ReadTimestampAsync(
        IAsyncQueryRunner tx, string timestamp)
    {
        return await ReadSetAsync(tx,
            "MATCH (n:GenericNode {timestamp: $ts})",
            new { ts = timestamp });
    }

    private static async Task<List<EntityData>> ReadSetAsync(
        IAsyncQueryRunner tx, string matchClause, object args)
    {
        var byP21 = new Dictionary<int, EntityData>();
        var nodeRows = await (await tx.RunAsync(
            $"{matchClause} RETURN n AS n, labels(n) AS labels", args)).ToListAsync();
        foreach (var row in nodeRows)
        {
            var props = row["n"].As<INode>().Properties.ToDictionary(kv => kv.Key, kv => kv.Value);
            if (!P21Id.TryParse(props.GetValueOrDefault("p21_id"), out var p21)) continue;
            byP21[p21] = new EntityData(
                P21:        p21,
                EntityType: props.GetValueOrDefault("EntityType") as string ?? string.Empty,
                GlobalId:   props.GetValueOrDefault("GlobalId") as string,
                Kind:       NodeClassifier.KindFromLabels(row["labels"].As<List<string>>()),
                Properties: props,
                Edges:      new List<EdgeData>(),
                Inlines:    new List<InlineData>());
        }
        if (byP21.Count == 0) return new List<EntityData>();

        var edgeRows = await (await tx.RunAsync(
            $@"{matchClause} MATCH (n)-[e:rel]->(m)
              RETURN n.p21_id AS src, e.rel_type AS rel_type, e.list_index AS list_index,
                     m.p21_id AS tgt, m.EntityType AS tgt_type, m.wrappedValue AS wrapped,
                     m.revit_element_id AS tgt_owner,
                     'InlineNode' IN labels(m) AS inline", args)).ToListAsync();
        foreach (var row in edgeRows)
        {
            if (!P21Id.TryParse(row["src"].As<string>(), out var src)) continue;
            if (!byP21.TryGetValue(src, out var owner)) continue;

            var relType   = row["rel_type"].As<string>();
            var listIndex = row["list_index"].As<int>();
            if (row["inline"].As<bool>())
            {
                owner.Inlines.Add(new InlineData(
                    src, relType, listIndex,
                    row["tgt_type"].As<string>() ?? string.Empty,
                    row["wrapped"]?.As<object>() ?? "$",
                    OwnerElementId: row["tgt_owner"]?.As<long?>()));
            }
            else if (P21Id.TryParse(row["tgt"], out var tgt))
            {
                owner.Edges.Add(new EdgeData(src, relType, listIndex, tgt));
            }
        }

        return byP21.Values.OrderBy(d => d.P21).ToList();
    }
}
