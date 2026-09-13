using GeometryGym.Ifc;
using Neo4j.Driver;

// ── Pipeline: DIRECT-WRITE (Revit → ggifc tree → Cypher → Neo4j; no temp IFC) ──
namespace RevitGraphPlugin.Cypher;

/// <summary>
/// The Neo4j sink. <see cref="WriteAsync"/> writes a full snapshot (nodes, then edges,
/// then inline nodes; ConMan2 schema); <see cref="ApplyRuleAsync"/> applies one
/// incremental <see cref="GraphRule"/> and stores it in the rule chain, in one transaction.
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

        // Wipe this timestamp first: inline nodes are CREATEd (no merge key) and would
        // otherwise accumulate on re-run.
        await session.RunAsync("MATCH (n {timestamp: $ts}) DETACH DELETE n", new { ts = timestamp });

        await BulkMergeNodes(session, NodeKind.Primary,    primary);
        await BulkMergeNodes(session, NodeKind.Connection, connection);
        await BulkMergeNodes(session, NodeKind.Secondary,  secondary);
        await BulkMergeEdges(session, edges, timestamp);
        await BulkCreateInlines(session, inlines, timestamp);

        return new EmitStats(primary.Count, connection.Count, secondary.Count, inlines.Count, edges.Count);
    }

    /// <summary>
    /// Apply one <see cref="GraphRule"/> to the current-state graph and persist it, in one
    /// transaction: capture L, align L / R (<see cref="GraphletDiff"/>) and apply only the
    /// pushout when possible, otherwise delete the owned graphlet and merge the new one;
    /// then refresh / drop shared context and <see cref="RuleStore.PersistAsync"/>.
    /// Returns the rule completed with its L side and what was stored.
    /// </summary>
    public static async Task<GraphRule> ApplyRuleAsync(IDriver driver, GraphRule rule)
    {
        await using var session = driver.AsyncSession();
        return await session.ExecuteWriteAsync(async tx =>
        {
            // A shared rel the mirror holds but the graph dropped earlier (emptied, now
            // re-joined) arrives as a refresh; record it as inserted so replay can rebuild it.
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

            // Drop shared deletes of nodes already gone: the memberless ggifc rel lingers
            // and would otherwise disqualify every later Replace from the aligned path.
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
                // Capture L before anything is deleted; SharedDelete nodes are read by p21.
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

            // Aligned Replace: keep the interface, move only the pushout.
            if (rule.Op == RuleOp.Replace
                && rule.SharedDelete.Count == 0
                && applied.BeforeGraphlet is { Nodes.Count: > 0 } capturedL)
            {
                var diff = GraphletDiff.Compare(capturedL, rule.Graphlet);
                applied = applied with { Diff = diff };
                if (diff.IsAligned)
                    return await ApplyAlignedAsync(tx, applied, diff);
            }

            // Resolve portable context names while the graph still holds every node; paths
            // must not route through nodes this rule deletes or has yet to create.
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
                // Replace the outgoing edge set wholesale (list_index renumbers).
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

            // Persist in the same transaction: apply and record commit or fail together.
            return applied with { Stored = await RuleStore.PersistAsync(tx, applied) };
        });
    }

    /// <summary>
    /// Aligned apply: the interface stays, only the pushout moves. Interface nodes are
    /// addressed by their L p21 until step 5 renumbers them to the R p21s the mirror now
    /// holds (p21 is a file-local number, not an identity — Esser 2022 §3.6).
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

    /// <summary>SET the after-values of <paramref name="changes"/>, addressing nodes by their L p21 (runs before renumbering).</summary>
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
    /// Walk every STEP entity of <paramref name="db"/> into <see cref="EntityData"/>,
    /// stamping <c>revit_element_id</c> on element-owned entities when
    /// <paramref name="ownerByStepId"/> is given.
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

    /// <summary>Walk one entity; owned entities get <c>revit_element_id</c> on the node and its inline values (so graphlet deletion reaches them).</summary>
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

        // Merge key: p21_id + timestamp (unique within a snapshot).
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

    // Inline values become InlineNode:Node entities linked to their parent (ConMan2's
    // inline_patterns). CREATE, not MERGE: inline nodes have no key.
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
            // Owned inline nodes carry their parent's revit_element_id.
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
