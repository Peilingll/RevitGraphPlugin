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
    public sealed record EmitStats(int PrimaryNodes, int ConnectionNodes, int SecondaryNodes, int Edges);

    public static async Task<EmitStats> WriteAsync(IDriver driver, DatabaseIfc db, string timestamp = "1")
    {
        // Walk every STEP entity (skip inline value wrappers with StepId == 0 for now).
        var allData = new List<EntityData>();
        foreach (var entity in db)
        {
            if (entity is null) continue;
            if (entity.StepId <= 0) continue;
            allData.Add(EntityWalker.Walk(entity, timestamp));
        }

        var primary    = allData.Where(d => d.Kind == NodeKind.Primary).ToList();
        var connection = allData.Where(d => d.Kind == NodeKind.Connection).ToList();
        var secondary  = allData.Where(d => d.Kind == NodeKind.Secondary).ToList();
        var edges      = allData.SelectMany(d => d.Edges).ToList();

        await using var session = driver.AsyncSession();

        await BulkMergeNodes(session, NodeKind.Primary,    primary);
        await BulkMergeNodes(session, NodeKind.Connection, connection);
        await BulkMergeNodes(session, NodeKind.Secondary,  secondary);
        await BulkMergeEdges(session, edges, timestamp);

        return new EmitStats(primary.Count, connection.Count, secondary.Count, edges.Count);
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
}
