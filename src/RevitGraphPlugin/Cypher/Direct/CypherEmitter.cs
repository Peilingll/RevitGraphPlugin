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
    /// Apply one incremental <see cref="GraphRule"/> to the current-state graph in a
    /// single transaction (all-or-nothing — a half-applied change would desync the
    /// graph from the Revit document):
    /// <list type="number">
    /// <item>Remove/Replace: delete every node owned by the element
    ///   (<c>revit_element_id</c>, inline nodes included — they carry the tag too).</item>
    /// <item>Insert/Replace: MERGE the graphlet's nodes, edges and inline nodes
    ///   (same shapes as the full-snapshot path).</item>
    /// <item>Refresh shared context: MERGE each <c>SharedRefresh</c> node's properties,
    ///   then replace its outgoing edge set wholesale with the freshly walked one —
    ///   membership list_index renumbers to exactly what a fresh snapshot would hold.</item>
    /// <item>Drop <c>SharedDelete</c> nodes (e.g. a containment rel left memberless —
    ///   absent from a fresh export of the same model state).</item>
    /// </list>
    /// </summary>
    public static async Task ApplyRuleAsync(IDriver driver, GraphRule rule)
    {
        await using var session = driver.AsyncSession();
        await session.ExecuteWriteAsync(async tx =>
        {
            if (rule.Op is RuleOp.Remove or RuleOp.Replace)
            {
                await tx.RunAsync(
                    "MATCH (n {timestamp: $ts, revit_element_id: $eid}) DETACH DELETE n",
                    new { ts = rule.Timestamp, eid = rule.RevitElementId });
            }

            if (rule.Op is RuleOp.Insert or RuleOp.Replace)
            {
                foreach (var kind in new[] { NodeKind.Primary, NodeKind.Connection, NodeKind.Secondary })
                    await BulkMergeNodes(tx, kind, rule.Graphlet.Where(d => d.Kind == kind).ToList());
                await BulkMergeEdges(tx, rule.Graphlet.SelectMany(d => d.Edges).ToList(), rule.Timestamp);
                await BulkCreateInlines(tx, rule.Graphlet.SelectMany(d => d.Inlines).ToList(), rule.Timestamp);
            }

            foreach (var shared in rule.SharedRefresh)
            {
                await BulkMergeNodes(tx, shared.Kind, new List<EntityData> { shared });
                // Replace the outgoing edge set: stale member edges (removed members,
                // superseded list_index values) must go before the fresh set lands.
                await tx.RunAsync(
                    "MATCH (a:GenericNode {p21_id: $p21, timestamp: $ts})-[e:rel]->() DELETE e",
                    new { p21 = $"#{shared.P21}", ts = rule.Timestamp });
                await BulkMergeEdges(tx, shared.Edges.ToList(), rule.Timestamp);
            }

            if (rule.SharedDelete.Count > 0)
            {
                await tx.RunAsync(
                    "UNWIND $p21s AS p21 MATCH (n {p21_id: p21, timestamp: $ts}) DETACH DELETE n",
                    new { p21s = rule.SharedDelete.ToList(), ts = rule.Timestamp });
            }
        });
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
            allData.Add(WalkOwned(entity, timestamp, ownerByStepId));
        }
        return allData;
    }

    /// <summary>
    /// Walk one entity and, when the ownership map claims it, stamp the
    /// <c>revit_element_id</c> onto its node properties AND its inline values —
    /// inline nodes belong to their parent's graphlet, so graphlet removal by
    /// <c>revit_element_id</c> must reach them too or they leak as orphans.
    /// </summary>
    public static EntityData WalkOwned(
        BaseClassIfc entity, string timestamp, IReadOnlyDictionary<int, long>? ownerByStepId)
    {
        var data = EntityWalker.Walk(entity, timestamp);
        if (ownerByStepId is not null && ownerByStepId.TryGetValue(entity.StepId, out var elementId))
        {
            data.Properties["revit_element_id"] = elementId;
            for (var k = 0; k < data.Inlines.Count; k++)
                data.Inlines[k] = data.Inlines[k] with { OwnerElementId = elementId };
        }
        return data;
    }

    private static async Task BulkMergeNodes(IAsyncQueryRunner session, NodeKind kind, List<EntityData> data)
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

    private static async Task BulkMergeEdges(IAsyncQueryRunner session, List<EdgeData> edges, string timestamp)
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
    private static async Task BulkCreateInlines(IAsyncQueryRunner session, List<InlineData> inlines, string timestamp)
    {
        if (inlines.Count == 0) return;

        var batch = inlines.Select(i =>
        {
            var row = new Dictionary<string, object>
            {
                ["source_p21_id"] = $"#{i.SourceP21}",
                ["rel_type"]      = i.RelType,
                ["list_index"]    = i.ListIndex,
                ["entity_type"]   = i.EntityType,
                ["wrapped_value"] = i.WrappedValue,
                ["timestamp"]     = timestamp,
            };
            // Owned inline nodes carry their parent's revit_element_id (a missing map
            // key reads as null in Cypher, so unowned rows simply set no property).
            if (i.OwnerElementId is long owner)
                row["revit_element_id"] = owner;
            return (object)row;
        }).ToList();

        const string cypher = @"
UNWIND $batch AS r
MATCH (a:GenericNode {p21_id: r.source_p21_id, timestamp: r.timestamp})
CREATE (b:InlineNode:Node {EntityType: r.entity_type, wrappedValue: r.wrapped_value,
                           timestamp: r.timestamp, revit_element_id: r.revit_element_id})
CREATE (a)-[:rel {rel_type: r.rel_type, list_index: r.list_index}]->(b)";

        await session.RunAsync(cypher, new { batch });
    }
}
