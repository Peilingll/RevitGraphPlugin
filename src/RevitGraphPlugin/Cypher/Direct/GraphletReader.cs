using Neo4j.Driver;

// ── Pipeline: DIRECT-WRITE, live incremental sync ──
// Rule persistence step 1 (doc_process/2026-08-02-plan-rule-persistence.md §2):
// read an element's graphlet back OUT of the current-state graph, so a Remove/Replace
// rule can record what it destroyed (the DPO L side). Reading from Neo4j — not from the
// ggifc mirror — is deliberate: by rule-build time DetachFromContainment/ForgetOwnership
// have already mutated the in-memory state, whereas the graph still holds exactly the
// nodes the rule is about to delete.
namespace RevitGraphPlugin.Cypher;

/// <summary>
/// The L side of one <see cref="GraphRule"/>: the element's own graphlet as it stood in
/// the graph immediately before the rule ran, plus the glue that attached it to context.
/// </summary>
/// <param name="Nodes">
/// The element-owned nodes (<c>revit_element_id</c>), each with its outgoing edges and
/// inline children — the same <see cref="EntityData"/> shape the insert path produces, so
/// a capture can be fed straight back through <see cref="CypherEmitter"/> to restore it.
/// </param>
/// <param name="IncomingGlue">
/// Edges from context nodes INTO the graphlet (e.g. the storey containment rel's
/// <c>RelatedElements</c> edge). <c>DETACH DELETE</c> destroys these too, and they are not
/// recoverable from <paramref name="Nodes"/> alone — without them a restored graphlet
/// would hang unattached.
/// </param>
public sealed record GraphletCapture(
    IReadOnlyList<EntityData> Nodes,
    IReadOnlyList<EdgeData> IncomingGlue);

public static class GraphletReader
{
    /// <summary>
    /// Read everything owned by <paramref name="elementId"/> in the <paramref name="timestamp"/>
    /// graph. Must run inside the rule's own transaction, BEFORE the delete — see
    /// <see cref="CypherEmitter.ApplyRuleAsync"/>. Returns an empty capture when the element
    /// owns nothing (e.g. a Replace of an element the graph never held).
    /// </summary>
    public static async Task<GraphletCapture> ReadOwnedAsync(
        IAsyncQueryRunner tx, string timestamp, long elementId)
    {
        var args = new { ts = timestamp, eid = elementId };

        // 1. The owned nodes themselves. Inline nodes are excluded (they carry the owner
        //    tag too, but have no p21_id and no identity of their own) — they come back
        //    as their parent's InlineData in step 2.
        var byP21 = new Dictionary<int, EntityData>();
        var nodeRows = await (await tx.RunAsync(
            @"MATCH (n:GenericNode {timestamp: $ts, revit_element_id: $eid})
              RETURN n AS n, labels(n) AS labels", args)).ToListAsync();

        foreach (var row in nodeRows)
        {
            var props = row["n"].As<INode>().Properties.ToDictionary(kv => kv.Key, kv => kv.Value);
            if (!TryParseP21(props.GetValueOrDefault("p21_id"), out var p21)) continue;

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

        // 2. Outgoing edges. Targets outside the graphlet (shared context: owner history,
        //    the containment rel, …) are kept — they are the rule's outgoing glue, and a
        //    restore must re-create them.
        var edgeRows = await (await tx.RunAsync(
            @"MATCH (n:GenericNode {timestamp: $ts, revit_element_id: $eid})-[e:rel]->(m)
              RETURN n.p21_id AS src, e.rel_type AS rel_type, e.list_index AS list_index,
                     m.p21_id AS tgt, m.EntityType AS tgt_type, m.wrappedValue AS wrapped,
                     'InlineNode' IN labels(m) AS inline", args)).ToListAsync();

        foreach (var row in edgeRows)
        {
            if (!TryParseP21(row["src"].As<string>(), out var src)) continue;
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
            else if (TryParseP21(row["tgt"], out var tgt))
            {
                owner.Edges.Add(new EdgeData(src, relType, listIndex, tgt));
            }
        }

        // 3. Incoming glue: edges reaching into the graphlet from anything not owned by
        //    this element. (Edges between two owned nodes already appear in step 2.)
        var glueRows = await (await tx.RunAsync(
            @"MATCH (src:GenericNode {timestamp: $ts})-[e:rel]->(n:GenericNode {timestamp: $ts, revit_element_id: $eid})
              WHERE src.revit_element_id IS NULL OR src.revit_element_id <> $eid
              RETURN src.p21_id AS src, e.rel_type AS rel_type, e.list_index AS list_index,
                     n.p21_id AS tgt", args)).ToListAsync();

        var glue = new List<EdgeData>();
        foreach (var row in glueRows)
        {
            if (!TryParseP21(row["src"], out var src)) continue;
            if (!TryParseP21(row["tgt"], out var tgt)) continue;
            glue.Add(new EdgeData(src, row["rel_type"].As<string>(), row["list_index"].As<int>(), tgt));
        }

        return new GraphletCapture(
            byP21.Values.OrderBy(d => d.P21).ToList(),
            glue);
    }

    /// <summary>Parse the graph's <c>p21_id</c> ("#123") back into its STEP id.</summary>
    private static bool TryParseP21(object? value, out int p21)
    {
        p21 = 0;
        return value is string s
            && s.Length > 1
            && s[0] == '#'
            && int.TryParse(s.AsSpan(1), out p21);
    }
}
