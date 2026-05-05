using Neo4j.Driver;
using RevitGraphPlugin.Graph;

namespace RevitGraphPlugin.Cypher;

/// <summary>
/// Three-phase write per ConMan2 (related-work.md §2.3 / design.md §4 Stage 2):
///   phase 1: MERGE node identities (label set by kind, key = p21_id + timestamp)
///   phase 2: SET properties on those nodes
///   phase 3: MERGE [:rel {rel_type, list_index}] edges
///
/// All Cypher is parameterised — no string concatenation of payload values
/// (related-work.md §3.4 item 2).
/// </summary>
public static class Neo4jGraphWriter
{
    /// <summary>
    /// Phase 1 — every node carries the <c>:GenericNode</c> label (covers the
    /// composite index) plus <c>EntityType</c> and <c>kind</c> as properties.
    /// Per-type / per-kind dynamic LABELS would need APOC's
    /// <c>apoc.create.addLabels</c>; deferred until Neo4j-with-APOC is a hard
    /// dependency. Property-based filtering on <c>EntityType</c> is enough for
    /// the queries in design.md §4 Stage 5.
    /// </summary>
    private const string Phase1MergeNode = @"
        UNWIND $rows AS row
        MERGE (n:GenericNode {p21_id: row.p21_id, timestamp: row.timestamp})
        SET n.EntityType = row.entity_type,
            n.kind = row.kind_label
        FOREACH (_ IN CASE WHEN row.global_id IS NOT NULL THEN [1] ELSE [] END |
            SET n.GlobalId = row.global_id)";

    private const string Phase2SetProperties = @"
        UNWIND $rows AS row
        MATCH (n:GenericNode {p21_id: row.p21_id, timestamp: $timestamp})
        SET n += row.props";

    private const string Phase3MergeEdges = @"
        UNWIND $rows AS row
        MATCH (a:GenericNode {p21_id: row.from_p21, timestamp: $timestamp})
        MATCH (b:GenericNode {p21_id: row.to_p21,   timestamp: $timestamp})
        MERGE (a)-[r:rel {rel_type: row.rel_type, list_index: row.list_index}]->(b)";

    public static async Task WriteAsync(IDriver driver, GraphBatch batch, int timestamp = 0)
    {
        await using var session = driver.AsyncSession();
        await session.ExecuteWriteAsync(async tx =>
        {
            await Phase1Async(tx, batch);
            await Phase2Async(tx, batch, timestamp);
            await Phase3Async(tx, batch, timestamp);
            return 0;
        });
    }

    private static async Task Phase1Async(IAsyncQueryRunner tx, GraphBatch batch)
    {
        var rows = batch.Nodes.Select(n => new Dictionary<string, object?>
        {
            ["p21_id"] = n.P21Id,
            ["timestamp"] = n.Timestamp,
            ["entity_type"] = n.EntityType,
            ["kind_label"] = n.Kind.ToString(),  // Primary | Secondary | Connection | Inline
            ["global_id"] = n.GlobalId,
        }).ToList();
        if (rows.Count == 0) return;
        var cursor = await tx.RunAsync(Phase1MergeNode, new { rows });
        await cursor.ConsumeAsync();
    }

    private static async Task Phase2Async(IAsyncQueryRunner tx, GraphBatch batch, int timestamp)
    {
        var rows = batch.Nodes
            .Where(n => n.Properties.Count > 0)
            .Select(n => new Dictionary<string, object?>
            {
                ["p21_id"] = n.P21Id,
                ["props"] = n.Properties.ToDictionary(kv => kv.Key, kv => kv.Value),
            }).ToList();
        if (rows.Count == 0) return;
        var cursor = await tx.RunAsync(Phase2SetProperties, new { rows, timestamp });
        await cursor.ConsumeAsync();
    }

    private static async Task Phase3Async(IAsyncQueryRunner tx, GraphBatch batch, int timestamp)
    {
        var rows = batch.Edges.Select(e => new Dictionary<string, object?>
        {
            ["from_p21"] = e.FromP21Id,
            ["to_p21"] = e.ToP21Id,
            ["rel_type"] = e.RelType,
            ["list_index"] = e.ListIndex,
        }).ToList();
        if (rows.Count == 0) return;
        var cursor = await tx.RunAsync(Phase3MergeEdges, new { rows, timestamp });
        await cursor.ConsumeAsync();
    }
}
