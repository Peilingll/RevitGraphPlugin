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
    /// Returns the rule completed with its <see cref="GraphRule.BeforeGraphlet"/> (the L
    /// side, read inside the same transaction before step 1 destroys it) — the payload
    /// rule persistence will store.
    /// </summary>
    public static async Task<GraphRule> ApplyRuleAsync(IDriver driver, GraphRule rule)
    {
        await using var session = driver.AsyncSession();
        return await session.ExecuteWriteAsync(async tx =>
        {
            // A shared node the mirror still holds but the graph does not: a containment
            // rel emptied earlier (dropped by SharedDelete) that an element now re-joins —
            // ggifc reuses the object, so it is outside the watermark and arrives as a
            // REFRESH. Refreshing would MERGE the node back silently, unrecorded; replay
            // would then find nothing to glue to (seen 2026-09-11, seq 119 of a 139-rule
            // chain). Treat it as inserted instead: it rides in the R graphlet and its copy.
            // (A rel created INSIDE this rule's watermark — the storey's first element —
            // is already in the graphlet as a rider; only a rel that is in neither the
            // graph nor the graphlet is a revival.)
            var revived = new List<EntityData>();
            foreach (var shared in rule.SharedRefresh)
                if (rule.Graphlet.All(d => d.P21 != shared.P21)
                    && !await NodeExistsAsync(tx, rule.Timestamp, shared.P21))
                    revived.Add(shared);
            if (revived.Count > 0)
                rule = rule with
                {
                    Graphlet = rule.Graphlet.Concat(revived).ToList(),
                    SharedRefresh = rule.SharedRefresh.Except(revived).ToList(),
                };

            // The mirror image of the above: a memberless containment rel whose graph
            // node is ALREADY gone (dropped by an earlier rule) keeps being reported for
            // deletion by every later rule, because the ggifc object lingers with zero
            // members. Deleting nothing is harmless, but a non-empty SharedDelete
            // disqualifies a Replace from the aligned path — so every later property
            // change stored as a full replace (seen 2026-09-11 after a roof rollback).
            // Keep only the shared deletes the graph can actually perform.
            if (rule.SharedDelete.Count > 0)
            {
                var stillThere = new List<string>();
                foreach (var p21Id in rule.SharedDelete)
                    if (P21Id.TryParse(p21Id, out var p21) && await NodeExistsAsync(tx, rule.Timestamp, p21))
                        stillThere.Add(p21Id);
                if (stillThere.Count != rule.SharedDelete.Count)
                    rule = rule with { SharedDelete = stillThere };
            }

            var applied = rule;

            if (rule.Op is RuleOp.Remove or RuleOp.Replace)
            {
                // Capture L first — after DETACH DELETE it is unrecoverable. The
                // SharedDelete nodes are L too (the rule destroys them), but they are
                // not element-owned, so they are read by p21 into their own list.
                applied = rule with
                {
                    BeforeGraphlet = (await GraphletReader.ReadOwnedAsync(
                        tx, rule.Timestamp, rule.RevitElementId)) with
                    {
                        SharedDeleted = await GraphletReader.ReadByP21Async(
                            tx, rule.Timestamp, rule.SharedDelete),
                    },
                };
            }

            // Partial replace (doc_process/2026-09-11-plan-partial-replace.md): align L
            // and R before touching the graph. Aligned → keep the interface in place,
            // delete / insert only the pushout, SET the changed values, renumber the
            // interface to the R walk's p21s. Not aligned → the legacy whole-graphlet
            // path below, unchanged.
            if (rule.Op == RuleOp.Replace
                && rule.SharedDelete.Count == 0
                && applied.BeforeGraphlet is { Nodes.Count: > 0 } capturedL)
            {
                var diff = GraphletDiff.Compare(capturedL, rule.Graphlet);
                applied = applied with { Diff = diff };
                if (diff.IsAligned)
                    return await ApplyAlignedAsync(tx, applied, diff);
            }

            // Name the rule's context portably while the graph still holds it: SharedDelete
            // nodes are gone by the end of this transaction, and paths must not route
            // through the graphlet this rule is about to delete or has yet to create.
            // The SharedDelete nodes themselves are also barred from OTHER targets' paths —
            // a name anchored on a node this rule drops (e.g. OwnerHistory reached via the
            // emptied containment rel) could never resolve at undo time. They still name
            // themselves: direct IfcRoot anchoring does not walk a path.
            var (external, own) = applied.PartitionReferences();
            var exclude = new HashSet<int>(own);
            foreach (var p21Id in rule.SharedDelete)
                if (P21Id.TryParse(p21Id, out var sharedP21))
                    exclude.Add(sharedP21);
            applied = applied with
            {
                ContextRefs = await ContextResolver.ResolveAsync(
                    tx, rule.Timestamp, external, exclude),
            };

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

            // Persist the completed rule into the :Rule chain as part of THIS transaction
            // (plan step 3): apply and persist commit or fail together — a graph that
            // changed without a record (or a record without the change) would desync the
            // chain from the current-state graph.
            return applied with { Stored = await RuleStore.PersistAsync(tx, applied) };
        });
    }

    /// <summary>
    /// The aligned apply: the interface I stays, only the pushout moves.
    /// <list type="number">
    /// <item>Resolve portable names for every node on the pushout boundary (interface
    ///   nodes the pushout edges touch, true external context, shared-refresh ends) —
    ///   keyed by the p21 the GRAPH holds now, i.e. the L p21 for interface nodes.</item>
    /// <item>Delete the L pushout (with inline children).</item>
    /// <item>Merge the R pushout: its nodes, its internal edges, its edges to interface
    ///   nodes (target translated R→L p21, the graph has not been renumbered yet), the
    ///   interface's edges INTO it (source translated), its inline children.</item>
    /// <item>SET the changed values on interface nodes, addressed by their L p21.</item>
    /// <item>Renumber the interface to the R p21s — the ggifc mirror now holds the R
    ///   objects, and every later rule (containment refresh, a hosted insert's void rel,
    ///   context resolution) will name these nodes by those ids. p21 is a local file
    ///   number, not an identity (Esser 2022 §3.6); GlobalId and paths are.</item>
    /// <item>Shared refresh as usual (its edges already carry the R p21s).</item>
    /// <item>Persist — a NoChange stores nothing but still renumbered (the mirror moved).</item>
    /// </list>
    /// </summary>
    private static async Task<GraphRule> ApplyAlignedAsync(
        IAsyncQueryRunner tx, GraphRule rule, GraphletDiffOutcome diff)
    {
        var captured = rule.BeforeGraphlet!;
        var left = captured.Nodes.ToDictionary(d => d.P21);
        var right = rule.Graphlet.ToDictionary(d => d.P21);
        var toL = diff.Match.ToDictionary(kv => kv.Value, kv => kv.Key);   // R p21 → L p21
        var pushoutL = diff.PushoutL.ToHashSet();
        var pushoutR = diff.PushoutR.ToHashSet();
        int GraphP21(int rp21) => toL.TryGetValue(rp21, out var lp21) ? lp21 : rp21;

        // 1. Boundary of the pushout, in graph (L) p21s.
        var boundary = new HashSet<int>();
        foreach (var l in pushoutL)
            foreach (var e in left[l].Edges)
                if (!pushoutL.Contains(e.TargetP21)) boundary.Add(e.TargetP21);
        foreach (var e in captured.IncomingGlue)
            if (pushoutL.Contains(e.TargetP21)) boundary.Add(e.SourceP21);
        foreach (var (l, node) in left)
            if (!pushoutL.Contains(l) && node.Edges.Any(e => pushoutL.Contains(e.TargetP21)))
                boundary.Add(l);
        foreach (var r in pushoutR)
            foreach (var e in right[r].Edges)
                if (!pushoutR.Contains(e.TargetP21)) boundary.Add(GraphP21(e.TargetP21));
        foreach (var (r, node) in right)
            if (!pushoutR.Contains(r) && node.Edges.Any(e => pushoutR.Contains(e.TargetP21)))
                boundary.Add(GraphP21(r));
        foreach (var shared in rule.SharedRefresh)
        {
            boundary.Add(shared.P21);
            foreach (var e in shared.Edges)
            {
                boundary.Add(e.SourceP21);
                if (!pushoutR.Contains(e.TargetP21)) boundary.Add(GraphP21(e.TargetP21));
            }
        }
        boundary.ExceptWith(pushoutL);
        var contextRefs = await ContextResolver.ResolveAsync(
            tx, rule.Timestamp, boundary, new HashSet<int>(pushoutL));
        var applied = rule with { ContextRefs = contextRefs };

        // 2. Delete the L pushout.
        if (pushoutL.Count > 0)
            await tx.RunAsync(@"
UNWIND $p21s AS p
MATCH (n:GenericNode {timestamp: $ts, p21_id: p})
OPTIONAL MATCH (n)-[:rel]->(i:InlineNode {timestamp: $ts})
DETACH DELETE i, n",
                new { ts = rule.Timestamp, p21s = pushoutL.Select(P21Id.Of).ToList() });

        // 3. Merge the R pushout and every edge that touches it.
        if (pushoutR.Count > 0)
        {
            var inserted = rule.Graphlet.Where(d => pushoutR.Contains(d.P21)).ToList();
            foreach (var kind in new[] { NodeKind.Primary, NodeKind.Connection, NodeKind.Secondary })
                await BulkMergeNodes(tx, kind, inserted.Where(d => d.Kind == kind).ToList());

            var edges = new List<EdgeData>();
            foreach (var d in inserted)
                foreach (var e in d.Edges)
                    edges.Add(e with { TargetP21 = pushoutR.Contains(e.TargetP21) ? e.TargetP21 : GraphP21(e.TargetP21) });
            foreach (var d in rule.Graphlet)
                if (!pushoutR.Contains(d.P21))
                    foreach (var e in d.Edges)
                        if (pushoutR.Contains(e.TargetP21))
                            edges.Add(e with { SourceP21 = GraphP21(d.P21) });
            await BulkMergeEdges(tx, edges, rule.Timestamp);
            await BulkCreateInlines(tx, inserted.SelectMany(d => d.Inlines).ToList(), rule.Timestamp);
        }

        // 4. Changed values on interface nodes (graph still holds their L p21s).
        await ApplyPropertyChangesAsync(tx, rule.Timestamp, diff.Changes);

        // 5. Renumber the interface L → R.
        await RenumberAsync(tx, rule.Timestamp,
            diff.Match.Select(kv => (P21Id.Of(kv.Key), P21Id.Of(kv.Value))).ToList());

        // 6. Shared context refresh (edge sets replaced wholesale from the fresh walk).
        foreach (var shared in rule.SharedRefresh)
        {
            await BulkMergeNodes(tx, shared.Kind, new List<EntityData> { shared });
            await tx.RunAsync(
                "MATCH (a:GenericNode {p21_id: $p21, timestamp: $ts})-[e:rel]->() DELETE e",
                new { p21 = P21Id.Of(shared.P21), ts = rule.Timestamp });
            await BulkMergeEdges(tx, shared.Edges.ToList(), rule.Timestamp);
        }

        // 7. Persist in the same transaction (apply and record commit or fail together).
        return applied with { Stored = await RuleStore.PersistAsync(tx, applied) };
    }

    /// <summary>
    /// SET the after-values of <paramref name="changes"/> on the live graph, addressing
    /// each node by its L p21 — exact here, because this runs against the very graph the
    /// change was diffed from, before any renumbering.
    /// </summary>
    internal static async Task ApplyPropertyChangesAsync(
        IAsyncQueryRunner tx, string timestamp, IReadOnlyList<PropertyChange> changes)
    {
        foreach (var c in changes)
        {
            if (c.Inline)
                await tx.RunAsync(@"
MATCH (n:GenericNode {timestamp: $ts, p21_id: $p21})
      -[e:rel {rel_type: $key, list_index: $li}]->(v:InlineNode {timestamp: $ts})
SET v.wrappedValue = $value",
                    new { ts = timestamp, p21 = P21Id.Of(c.P21Before), key = c.Key, li = c.ListIndex!.Value, value = c.After });
            else
                await tx.RunAsync(
                    "MATCH (n:GenericNode {timestamp: $ts, p21_id: $p21}) SET n += $props",
                    new { ts = timestamp, p21 = P21Id.Of(c.P21Before), props = new Dictionary<string, object?> { [c.Key] = c.After } });
        }
    }

    private static async Task<bool> NodeExistsAsync(IAsyncQueryRunner tx, string timestamp, int p21)
    {
        var rows = await (await tx.RunAsync(
            "MATCH (n:GenericNode {timestamp: $ts, p21_id: $p21}) RETURN count(n) AS c",
            new { ts = timestamp, p21 = P21Id.Of(p21) })).ToListAsync();
        return rows.Single()["c"].As<int>() > 0;
    }

    /// <summary>Rename node ids in one atomic UNWIND (from/to ranges never overlap: ggifc never reuses a StepId).</summary>
    internal static async Task RenumberAsync(
        IAsyncQueryRunner tx, string timestamp, IReadOnlyList<(string From, string To)> pairs)
    {
        if (pairs.Count == 0) return;
        await tx.RunAsync(@"
UNWIND $pairs AS pair
MATCH (n:GenericNode {timestamp: $ts, p21_id: pair.from})
SET n.p21_id = pair.to",
            new
            {
                ts = timestamp,
                pairs = pairs.Select(p => new Dictionary<string, object> { ["from"] = p.From, ["to"] = p.To }).ToList(),
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

    internal static async Task BulkMergeNodes(IAsyncQueryRunner session, NodeKind kind, List<EntityData> data)
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

    internal static async Task BulkMergeEdges(IAsyncQueryRunner session, List<EdgeData> edges, string timestamp)
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
    internal static async Task BulkCreateInlines(IAsyncQueryRunner session, List<InlineData> inlines, string timestamp)
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
