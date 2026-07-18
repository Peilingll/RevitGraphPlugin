using GeometryGym.Ifc;
using Neo4j.Driver;

// ── Pipeline: DIRECT-WRITE (Revit → ggifc tree → Cypher → Neo4j; no temp IFC) ──
// Opt-in. Default sink is the temp-IFC bridge (Cypher/IfcSnippetSink.cs).
namespace RevitGraphPlugin.Cypher;

/// <summary>
/// Walk a ggifc DatabaseIfc, classify entities per ConMan2 schema, and emit
/// the corresponding Cypher to a Neo4j driver in two phases:
///   Phase 1: MERGE all nodes (grouped by kind: Primary / Connection / Secondary).
///   Phase 2: MERGE all edges as `[:rel {rel_type, list_index}]` between nodes.
/// MERGE is used everywhere so re-running on the same Revit project is idempotent.
/// </summary>
public static class CypherEmitter
{
    public sealed record EmitStats(int PrimaryNodes, int ConnectionNodes, int SecondaryNodes, int InlineNodes, int Edges);

    public static async Task<EmitStats> WriteAsync(
        IDriver driver,
        DatabaseIfc db,
        string timestamp = "1",
        IReadOnlyDictionary<int, long>? ownerByStepId = null)
    {
        var allData = WalkAll(db, timestamp, ownerByStepId);

        var primary    = allData.Where(d => d.Kind == NodeKind.Primary).ToList();
        var connection = allData.Where(d => d.Kind == NodeKind.Connection).ToList();
        var secondary  = allData.Where(d => d.Kind == NodeKind.Secondary).ToList();
        var edges      = allData.SelectMany(d => d.Edges).ToList();
        var inlines    = allData.SelectMany(d => d.Inlines).ToList();

        await using var session = driver.AsyncSession();

        // Clear this snapshot first so re-syncing is idempotent: inline nodes are
        // CREATEd (no key to MERGE on), so they would otherwise accumulate on re-run.
        // Mirrors the bridge's per-timestamp clear; scoped by timestamp, so other
        // snapshots are untouched.
        await session.RunAsync("MATCH (n {timestamp: $ts}) DETACH DELETE n", new { ts = timestamp });

        await BulkMergeNodes(session, NodeKind.Primary,    primary);
        await BulkMergeNodes(session, NodeKind.Connection, connection);
        await BulkMergeNodes(session, NodeKind.Secondary,  secondary);
        await BulkMergeEdges(session, edges, timestamp);
        await BulkCreateInlines(session, inlines, timestamp);

        return new EmitStats(primary.Count, connection.Count, secondary.Count, inlines.Count, edges.Count);
    }

    /// <summary>
    /// Walk every STEP entity of <paramref name="db"/> into its <see cref="EntityData"/>.
    /// Inline value wrappers (StepId == 0) have no node of their own — they are captured
    /// as InlineData on their parent entity instead. When <paramref name="ownerByStepId"/>
    /// is given (see <c>IfcModelContext.OwnerByStepId</c>), element-owned entities get a
    /// <c>revit_element_id</c> node property — the plugin-only ownership column that
    /// incremental sync keys graphlet removal/replacement on. Shared boilerplate entities
    /// are absent from the map and carry no such property. ConMan2's <c>graph_2_ifc</c>
    /// ignores it (not an IFC attribute), and compare_neo4j masks it.
    /// </summary>
    public static List<EntityData> WalkAll(
        DatabaseIfc db, string timestamp, IReadOnlyDictionary<int, long>? ownerByStepId = null)
    {
        var allData = new List<EntityData>();
        foreach (var entity in db)
        {
            if (entity is null) continue;
            if (entity.StepId <= 0) continue;

            var data = EntityWalker.Walk(entity, timestamp);
            if (ownerByStepId is not null && ownerByStepId.TryGetValue(entity.StepId, out var elementId))
                data.Properties["revit_element_id"] = elementId;
            allData.Add(data);
        }
        return allData;
    }

    private static async Task BulkMergeNodes(IAsyncSession session, NodeKind kind, List<EntityData> data)
    {
        if (data.Count == 0) return;

        var labels = NodeClassifier.LabelExpression(kind);
        var batch  = data.Select(d => (object)d.Properties).ToList();

        // Use p21_id + timestamp as the merge key (unique within a snapshot).
        // SET n += props upserts all attributes (incl. GlobalId for Primary/Connection).
        var cypher = $@"
UNWIND $batch AS props
MERGE (n:{labels} {{p21_id: props.p21_id, timestamp: props.timestamp}})
SET n += props";

        await session.RunAsync(cypher, new { batch });
    }

    private static async Task BulkMergeEdges(IAsyncSession session, List<EdgeData> edges, string timestamp)
    {
        if (edges.Count == 0) return;

        var batch = edges.Select(e => (object)new Dictionary<string, object>
        {
            ["source_p21_id"] = $"#{e.SourceP21}",
            ["target_p21_id"] = $"#{e.TargetP21}",
            ["rel_type"]      = e.RelType,
            ["list_index"]    = e.ListIndex,
            ["timestamp"]     = timestamp,
        }).ToList();

        const string cypher = @"
UNWIND $batch AS e
MATCH (a:GenericNode {p21_id: e.source_p21_id, timestamp: e.timestamp})
MATCH (b:GenericNode {p21_id: e.target_p21_id, timestamp: e.timestamp})
MERGE (a)-[:rel {rel_type: e.rel_type, list_index: e.list_index}]->(b)";

        await session.RunAsync(cypher, new { batch });
    }

    // Inline values (e.g. a property's NominalValue) become InlineNode:Node entities
    // CREATEd and linked to their parent — exactly ConMan2's inline_patterns query
    // (IfcGraphInterface.ifc_2_graph). CREATE (not MERGE): inline nodes have no key;
    // the per-timestamp clear in WriteAsync keeps re-runs idempotent.
    private static async Task BulkCreateInlines(IAsyncSession session, List<InlineData> inlines, string timestamp)
    {
        if (inlines.Count == 0) return;

        var batch = inlines.Select(i => (object)new Dictionary<string, object>
        {
            ["source_p21_id"] = $"#{i.SourceP21}",
            ["rel_type"]      = i.RelType,
            ["list_index"]    = i.ListIndex,
            ["entity_type"]   = i.EntityType,
            ["wrapped_value"] = i.WrappedValue,
            ["timestamp"]     = timestamp,
        }).ToList();

        const string cypher = @"
UNWIND $batch AS r
MATCH (a:GenericNode {p21_id: r.source_p21_id, timestamp: r.timestamp})
CREATE (b:InlineNode:Node {EntityType: r.entity_type, wrappedValue: r.wrapped_value, timestamp: r.timestamp})
CREATE (a)-[:rel {rel_type: r.rel_type, list_index: r.list_index}]->(b)";

        await session.RunAsync(cypher, new { batch });
    }
}
