using Neo4j.Driver;
using RevitGraphPlugin.Graph;

namespace RevitGraphPlugin.Cypher;

/// <summary>
/// Three-phase write per ConMan2 (related-work.md §2.3 / design.md §4 Stage 2):
///   phase 1: MERGE node identities (split by <see cref="MergeStrategy"/> —
///            Tag-keyed for IfcElement primaries, GlobalId-keyed for
///            IfcRelationship connections, p21_id-keyed for everything else)
///   phase 1.5: detach old outgoing edges of Tag-keyed primaries that already
///            existed in the graph (so Phase 3 can repopulate them cleanly)
///   phase 2: SET property bags
///   phase 3: MERGE [:rel {rel_type, list_index}] edges
///
/// Tag/GlobalId-keyed strategies are Stage 5's fix for the p21_id-collision
/// bug documented in <c>doc/known-issues.md</c>: stable identifiers prevent
/// MERGE from latching onto the wrong node when IFC construction order shifts
/// between exports.
///
/// Tag UNWIND runs first so primary nodes' p21_id is updated *before* any
/// p21_id-keyed UNWIND has a chance to MERGE on a stale value.
///
/// All Cypher is parameterised — no string concatenation of payload values
/// (related-work.md §3.4 item 2).
/// </summary>
public static class Neo4jGraphWriter
{
    private const string Phase1MergeByTag = @"
        UNWIND $rows AS row
        MERGE (n:GenericNode {Tag: row.tag, timestamp: row.timestamp})
        SET n.EntityType = row.entity_type,
            n.kind = row.kind_label,
            n.p21_id = row.p21_id,
            n.GlobalId = row.global_id";

    private const string Phase1MergeByGlobalId = @"
        UNWIND $rows AS row
        MERGE (n:GenericNode {GlobalId: row.global_id, timestamp: row.timestamp})
        SET n.EntityType = row.entity_type,
            n.kind = row.kind_label,
            n.p21_id = row.p21_id";

    private const string Phase1MergeByP21Id = @"
        UNWIND $rows AS row
        MERGE (n:GenericNode {p21_id: row.p21_id, timestamp: row.timestamp})
        SET n.EntityType = row.entity_type,
            n.kind = row.kind_label
        FOREACH (_ IN CASE WHEN row.global_id IS NOT NULL THEN [1] ELSE [] END |
            SET n.GlobalId = row.global_id)";

    /// <summary>
    /// Phase 1.5 — for each Tag-keyed primary that already exists in the
    /// graph, drop its outgoing <c>:rel</c> edges so Phase 3 can re-emit them
    /// without duplicating. Inbound edges (from relationship nodes) are
    /// preserved; the relationships themselves are responsible for refreshing
    /// their own outgoing edges via the cascade-delete-then-re-add path.
    /// </summary>
    private const string DetachOutgoingForTaggedPrimaries = @"
        UNWIND $tags AS tag
        MATCH (n:GenericNode {Tag: tag, timestamp: $timestamp})-[r:rel]->()
        DELETE r";

    private const string Phase2SetProperties = @"
        UNWIND $rows AS row
        MATCH (n:GenericNode {p21_id: row.p21_id, timestamp: $timestamp})
        SET n += row.props";

    private const string Phase3MergeEdges = @"
        UNWIND $rows AS row
        MATCH (a:GenericNode {p21_id: row.from_p21, timestamp: $timestamp})
        MATCH (b:GenericNode {p21_id: row.to_p21,   timestamp: $timestamp})
        MERGE (a)-[r:rel {rel_type: row.rel_type, list_index: row.list_index}]->(b)";

    /// <summary>
    /// Stage 4 — DETACH DELETE every <c>:GenericNode</c> identified by
    /// <paramref name="tag"/>. Wall scope: matches the wall only. Edges that
    /// pointed to the wall (e.g. relationships' <c>RelatingBuildingElement</c>
    /// edges) are dropped by <c>DETACH DELETE</c> but their owning relationship
    /// nodes survive — orphans cleanup is deferred.
    /// </summary>
    private const string DeleteWallByTagCypher = @"
        MATCH (w:GenericNode {Tag: $tag, timestamp: $timestamp})
        DETACH DELETE w";

    /// <summary>
    /// Stage 4 — Cascade for window deletion (design.md §4 Stage 5 step 3
    /// expectation: window + its synthesised opening + both IfcRel* go,
    /// the wall persists). Walks back through RelFills to opening, then
    /// through RelVoids reachable from the same opening.
    /// </summary>
    private const string DeleteWindowCascadeCypher = @"
        MATCH (w:GenericNode {Tag: $tag, timestamp: $timestamp})
        OPTIONAL MATCH (relFill:GenericNode)-[:rel {rel_type: 'RelatedBuildingElement'}]->(w)
        OPTIONAL MATCH (relFill)-[:rel {rel_type: 'RelatingOpeningElement'}]->(opening:GenericNode)
        OPTIONAL MATCH (relVoid:GenericNode)-[:rel {rel_type: 'RelatedOpeningElement'}]->(opening)
        DETACH DELETE w, relFill, opening, relVoid";

    public static async Task DeleteWallByTagAsync(IDriver driver, string tag, int timestamp = 0)
    {
        await using var session = driver.AsyncSession();
        await session.ExecuteWriteAsync(async tx =>
        {
            var cursor = await tx.RunAsync(DeleteWallByTagCypher, new { tag, timestamp });
            return await cursor.ConsumeAsync();
        });
    }

    public static async Task DeleteWindowCascadeAsync(IDriver driver, string tag, int timestamp = 0)
    {
        await using var session = driver.AsyncSession();
        await session.ExecuteWriteAsync(async tx =>
        {
            var cursor = await tx.RunAsync(DeleteWindowCascadeCypher, new { tag, timestamp });
            return await cursor.ConsumeAsync();
        });
    }

    public static async Task WriteAsync(IDriver driver, GraphBatch batch, int timestamp = 0)
    {
        await using var session = driver.AsyncSession();
        await session.ExecuteWriteAsync(async tx =>
        {
            await Phase1ByTagAsync(tx, batch);
            await Phase1ByGlobalIdAsync(tx, batch);
            await DetachOutgoingForTaggedPrimariesAsync(tx, batch, timestamp);
            await Phase1ByP21IdAsync(tx, batch);
            await Phase2Async(tx, batch, timestamp);
            await Phase3Async(tx, batch, timestamp);
            return 0;
        });
    }

    private static async Task Phase1ByTagAsync(IAsyncQueryRunner tx, GraphBatch batch)
    {
        var rows = batch.Nodes
            .Where(n => n.MergeStrategy == MergeStrategy.ByTag)
            .Select(n => new Dictionary<string, object?>
            {
                ["tag"] = n.Tag!,
                ["p21_id"] = n.P21Id,
                ["timestamp"] = n.Timestamp,
                ["entity_type"] = n.EntityType,
                ["kind_label"] = n.Kind.ToString(),
                ["global_id"] = n.GlobalId,
            }).ToList();
        if (rows.Count == 0) return;
        var cursor = await tx.RunAsync(Phase1MergeByTag, new { rows });
        await cursor.ConsumeAsync();
    }

    private static async Task Phase1ByGlobalIdAsync(IAsyncQueryRunner tx, GraphBatch batch)
    {
        var rows = batch.Nodes
            .Where(n => n.MergeStrategy == MergeStrategy.ByGlobalId)
            .Select(n => new Dictionary<string, object?>
            {
                ["global_id"] = n.GlobalId!,
                ["p21_id"] = n.P21Id,
                ["timestamp"] = n.Timestamp,
                ["entity_type"] = n.EntityType,
                ["kind_label"] = n.Kind.ToString(),
            }).ToList();
        if (rows.Count == 0) return;
        var cursor = await tx.RunAsync(Phase1MergeByGlobalId, new { rows });
        await cursor.ConsumeAsync();
    }

    private static async Task DetachOutgoingForTaggedPrimariesAsync(
        IAsyncQueryRunner tx, GraphBatch batch, int timestamp)
    {
        var tags = batch.Nodes
            .Where(n => n.MergeStrategy == MergeStrategy.ByTag)
            .Select(n => n.Tag!)
            .ToList();
        if (tags.Count == 0) return;
        var cursor = await tx.RunAsync(DetachOutgoingForTaggedPrimaries, new { tags, timestamp });
        await cursor.ConsumeAsync();
    }

    private static async Task Phase1ByP21IdAsync(IAsyncQueryRunner tx, GraphBatch batch)
    {
        var rows = batch.Nodes
            .Where(n => n.MergeStrategy == MergeStrategy.ByP21Id)
            .Select(n => new Dictionary<string, object?>
            {
                ["p21_id"] = n.P21Id,
                ["timestamp"] = n.Timestamp,
                ["entity_type"] = n.EntityType,
                ["kind_label"] = n.Kind.ToString(),
                ["global_id"] = n.GlobalId,
            }).ToList();
        if (rows.Count == 0) return;
        var cursor = await tx.RunAsync(Phase1MergeByP21Id, new { rows });
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
