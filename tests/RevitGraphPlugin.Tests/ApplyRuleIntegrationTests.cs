using GeometryGym.Ifc;
using Neo4j.Driver;
using RevitGraphPlugin.Cypher;
using RevitGraphPlugin.Ifc;
using Xunit;
using Xunit.Abstractions;

namespace RevitGraphPlugin.Tests;

/// <summary>
/// Integration tests for <see cref="CypherEmitter.ApplyRuleAsync"/> against a local
/// Neo4j (same NEO4J_LOCAL_* convention as the plugin). Each test runs the full
/// incremental lifecycle on a real ggifc storey+walls model under a dedicated test
/// timestamp and asserts the core invariant: the incrementally maintained graph equals a
/// fresh full snapshot of the same ggifc state. Tests no-op silently when Neo4j is
/// not reachable (they log a warning) — CI without a database still passes.
/// </summary>
public sealed class ApplyRuleIntegrationTests : IDisposable
{
    private const string TsLive = "test-incr-live";
    private const string TsRef  = "test-incr-ref";

    private readonly IDriver? _driver;
    private readonly ITestOutputHelper _output;

    public ApplyRuleIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
        var password = Environment.GetEnvironmentVariable("NEO4J_LOCAL_PASSWORD") ?? "password";
        try
        {
            var d = GraphDatabase.Driver("bolt://127.0.0.1:7687", AuthTokens.Basic("neo4j", password));
            d.VerifyConnectivityAsync().GetAwaiter().GetResult();
            _driver = d;
        }
        catch (Exception ex)
        {
            _output.WriteLine($"Neo4j unreachable — skipping integration assertions: {ex.Message}");
            _driver = null;
        }
    }

    public void Dispose()
    {
        if (_driver is null) return;
        Cleanup().GetAwaiter().GetResult();
        _driver.Dispose();
    }

    private async Task Cleanup()
    {
        await using var session = _driver!.AsyncSession();
        // STARTS WITH: ApplyRuleAsync now persists a :Rule chain in "<ts>-rule-<seq>*"
        // namespaces alongside the graph — test cleanup must reach those too.
        foreach (var ts in new[] { TsLive, TsRef })
            await session.RunAsync(
                "MATCH (n) WHERE n.timestamp STARTS WITH $ts DETACH DELETE n", new { ts });
    }

    // ── graph signature: node types + edge triples, the compare_neo4j invariant in C# ──

    private async Task<(Dictionary<string, int> nodes, Dictionary<string, int> edges)> Signature(string ts)
    {
        await using var session = _driver!.AsyncSession();
        var nodes = new Dictionary<string, int>();
        foreach (var r in await (await session.RunAsync(
            "MATCH (n {timestamp: $ts}) RETURN n.EntityType AS t, count(*) AS c", new { ts })).ToListAsync())
            nodes[r["t"].As<string>()] = r["c"].As<int>();

        var edges = new Dictionary<string, int>();
        foreach (var r in await (await session.RunAsync(
            @"MATCH (a {timestamp: $ts})-[e:rel]->(b {timestamp: $ts})
              RETURN a.EntityType + '|' + e.rel_type + '|' + toString(e.list_index) + '|' + b.EntityType AS k,
                     count(*) AS c", new { ts })).ToListAsync())
            edges[r["k"].As<string>()] = r["c"].As<int>();

        return (nodes, edges);
    }

    private static void AssertSameSignature(
        (Dictionary<string, int> nodes, Dictionary<string, int> edges) live,
        (Dictionary<string, int> nodes, Dictionary<string, int> edges) reference)
    {
        Assert.Equal(
            reference.nodes.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"),
            live.nodes.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"));
        Assert.Equal(
            reference.edges.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"),
            live.edges.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"));
    }

    // ── the lifecycle ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Insert_then_remove_keeps_live_graph_equal_to_fresh_snapshot()
    {
        if (_driver is null) return;   // Neo4j unavailable — logged in ctor
        await Cleanup();

        // Baseline model: storey + wall1 (id 101), full snapshot to TsLive.
        var db = new DatabaseIfc(false, ReleaseVersion.IFC4);
        var building = new IfcBuilding(db, "B");
        var storey = new IfcBuildingStorey(building, "S", 0);
        var owner = new Dictionary<int, long>();

        var w0 = StepIdWatermark.Current(db);
        var wall1 = new IfcWall(storey, null, null);
        var w1 = StepIdWatermark.Current(db);
        TagRange(db, w0, w1, 101, owner);

        await CypherEmitter.WriteAsync(_driver, db, TsLive, owner);

        // ── INSERT wall2 (id 102) incrementally ──
        var wall2 = new IfcWall(storey, null, null);
        var w2 = StepIdWatermark.Current(db);
        TagRange(db, w1, w2, 102, owner);

        var walked = CypherEmitter.WalkAll(db, TsLive, owner);
        var (insertRefresh, insertDelete) = GraphletExtractor.StoreyContainmentChanges(new[] { storey }, TsLive);
        var rule = new GraphRule(
            RuleOp.Insert, 102, TsLive,
            Graphlet: GraphletExtractor.NewEntities(walked, w1, w2),
            SharedRefresh: insertRefresh,
            SharedDelete: insertDelete);
        await CypherEmitter.ApplyRuleAsync(_driver, rule);

        // Invariant: live graph ≡ fresh full snapshot of the same ggifc state.
        await CypherEmitter.WriteAsync(_driver, db, TsRef, owner);
        AssertSameSignature(await Signature(TsLive), await Signature(TsRef));

        // ── REMOVE wall1 incrementally (ggifc detach + graph rule) ──
        var rel = storey.ContainsElements.Single();
        rel.RelatedElements.Remove(wall1);

        var (removeRefresh, removeDelete) = GraphletExtractor.StoreyContainmentChanges(new[] { storey }, TsLive);
        var removeRule = new GraphRule(
            RuleOp.Remove, 101, TsLive,
            Graphlet: Array.Empty<EntityData>(),
            SharedRefresh: removeRefresh,
            SharedDelete: removeDelete);
        await CypherEmitter.ApplyRuleAsync(_driver, removeRule);

        // wall1 gone, wall2's containment edge renumbered to list_index 0.
        await using (var session = _driver.AsyncSession())
        {
            var walls = (await (await session.RunAsync(
                "MATCH (n:PrimaryNode {timestamp: $ts, EntityType: 'IfcWall'}) RETURN n.revit_element_id AS eid",
                new { ts = TsLive })).ToListAsync()).Select(r => r["eid"].As<long>()).ToList();
            Assert.Equal(new[] { 102L }, walls);

            var idx = (await (await session.RunAsync(
                @"MATCH ({timestamp: $ts, EntityType: 'IfcRelContainedInSpatialStructure'})
                        -[e:rel {rel_type: 'RelatedElements'}]->(w {EntityType: 'IfcWall'})
                  RETURN e.list_index AS i", new { ts = TsLive })).ToListAsync())
                .Select(r => r["i"].As<int>()).ToList();
            Assert.Equal(new[] { 0 }, idx);

            // No orphans: every remaining owned node belongs to wall2.
            var strayOwners = (await (await session.RunAsync(
                @"MATCH (n {timestamp: $ts}) WHERE n.revit_element_id IS NOT NULL
                  RETURN DISTINCT n.revit_element_id AS eid", new { ts = TsLive })).ToListAsync())
                .Select(r => r["eid"].As<long>()).ToList();
            Assert.Equal(new[] { 102L }, strayOwners);
        }
    }

    [Fact]
    public async Task Removing_last_member_deletes_the_containment_rel_via_SharedDelete()
    {
        if (_driver is null) return;
        await Cleanup();

        var db = new DatabaseIfc(false, ReleaseVersion.IFC4);
        var building = new IfcBuilding(db, "B");
        var storey = new IfcBuildingStorey(building, "S", 0);
        var owner = new Dictionary<int, long>();

        var w0 = StepIdWatermark.Current(db);
        var wall = new IfcWall(storey, null, null);
        var w1 = StepIdWatermark.Current(db);
        TagRange(db, w0, w1, 101, owner);
        await CypherEmitter.WriteAsync(_driver, db, TsLive, owner);

        // Remove the only wall: ggifc detach empties the rel → SharedDelete, not refresh.
        var rel = storey.ContainsElements.Single();
        rel.RelatedElements.Remove(wall);
        var (refresh, delete) = GraphletExtractor.StoreyContainmentChanges(new[] { storey }, TsLive);
        Assert.Empty(refresh);
        Assert.Equal(new[] { $"#{rel.StepId}" }, delete);

        var rule = new GraphRule(
            RuleOp.Remove, 101, TsLive,
            Graphlet: Array.Empty<EntityData>(),
            SharedRefresh: refresh,
            SharedDelete: delete);
        await CypherEmitter.ApplyRuleAsync(_driver, rule);

        await using var session = _driver.AsyncSession();
        var leftover = (await (await session.RunAsync(
            @"MATCH (n {timestamp: $ts})
              WHERE n.EntityType IN ['IfcWall', 'IfcRelContainedInSpatialStructure']
              RETURN count(*) AS c", new { ts = TsLive })).ToListAsync()).Single()["c"].As<int>();
        Assert.Equal(0, leftover);
    }

    // ── L-side capture (rule persistence step 1) ──────────────────────────────────

    [Fact]
    public async Task Remove_rule_captures_the_L_side_before_deleting_it()
    {
        if (_driver is null) return;
        await Cleanup();

        var db = new DatabaseIfc(false, ReleaseVersion.IFC4);
        var building = new IfcBuilding(db, "B");
        var storey = new IfcBuildingStorey(building, "S", 0);
        var owner = new Dictionary<int, long>();

        var w0 = StepIdWatermark.Current(db);
        var wall = new IfcWall(storey, null, null);
        var w1 = StepIdWatermark.Current(db);
        TagRange(db, w0, w1, 101, owner);
        await CypherEmitter.WriteAsync(_driver, db, TsLive, owner);

        var rel = storey.ContainsElements.Single();
        var ownedBefore = await OwnedNodeCount(101);
        Assert.True(ownedBefore > 0);

        // ggifc detach + Remove rule, exactly as LiveSyncSession.ApplyRemoved does it.
        rel.RelatedElements.Remove(wall);
        var (refresh, delete) = GraphletExtractor.StoreyContainmentChanges(new[] { storey }, TsLive);
        var applied = await CypherEmitter.ApplyRuleAsync(_driver, new GraphRule(
            RuleOp.Remove, 101, TsLive,
            Graphlet: Array.Empty<EntityData>(), SharedRefresh: refresh, SharedDelete: delete));

        Assert.NotNull(applied.BeforeGraphlet);
        var l = applied.BeforeGraphlet!;
        Assert.Equal(ownedBefore, l.Nodes.Count);

        // The wall itself, with node kind and ownership tag intact.
        var wallNode = Assert.Single(l.Nodes, d => d.EntityType == nameof(IfcWall));
        Assert.Equal(wall.StepId, wallNode.P21);
        Assert.Equal(NodeKind.Primary, wallNode.Kind);
        Assert.Equal(101L, wallNode.Properties["revit_element_id"]);
        Assert.Equal(wall.GlobalId, wallNode.GlobalId);

        // The incoming glue: the storey's containment rel pointed at the wall. DETACH
        // DELETE destroys this edge and the owned nodes alone do not record it, so
        // without IncomingGlue the L side would be unattachable.
        Assert.Contains(l.IncomingGlue, e =>
            e.SourceP21 == rel.StepId && e.RelType == "RelatedElements" && e.TargetP21 == wall.StepId);

        // The rule did delete what it captured.
        Assert.Equal(0, await OwnedNodeCount(101));
    }

    [Fact]
    public async Task Insert_captures_nothing_and_a_capture_can_be_re_applied()
    {
        if (_driver is null) return;
        await Cleanup();

        var db = new DatabaseIfc(false, ReleaseVersion.IFC4);
        var building = new IfcBuilding(db, "B");
        var storey = new IfcBuildingStorey(building, "S", 0);
        var owner = new Dictionary<int, long>();

        var w0 = StepIdWatermark.Current(db);
        _ = new IfcWall(storey, null, null);
        var w1 = StepIdWatermark.Current(db);
        TagRange(db, w0, w1, 101, owner);
        await CypherEmitter.WriteAsync(_driver, db, TsLive, owner);

        // ── INSERT wall2 (102): an insert destroys nothing, so there is no L side ──
        var wall2 = new IfcWall(storey, null, null);
        var w2 = StepIdWatermark.Current(db);
        TagRange(db, w1, w2, 102, owner);

        var (insRefresh, insDelete) = GraphletExtractor.StoreyContainmentChanges(new[] { storey }, TsLive);
        var inserted = await CypherEmitter.ApplyRuleAsync(_driver, new GraphRule(
            RuleOp.Insert, 102, TsLive,
            Graphlet: GraphletExtractor.WalkNew(db, owner, w1, w2, TsLive),
            SharedRefresh: insRefresh, SharedDelete: insDelete));
        Assert.Null(inserted.BeforeGraphlet);

        var nodesBefore = await OwnedNodeCount(102);
        var edgesBefore = await OwnedEdgeCount(102);

        // ── REMOVE wall2, then feed its captured L back in as an insert ──
        var rel = storey.ContainsElements.Single();
        rel.RelatedElements.Remove(wall2);
        var (remRefresh, remDelete) = GraphletExtractor.StoreyContainmentChanges(new[] { storey }, TsLive);
        var removed = await CypherEmitter.ApplyRuleAsync(_driver, new GraphRule(
            RuleOp.Remove, 102, TsLive,
            Graphlet: Array.Empty<EntityData>(), SharedRefresh: remRefresh, SharedDelete: remDelete));
        Assert.Equal(0, await OwnedNodeCount(102));

        await CypherEmitter.ApplyRuleAsync(_driver, new GraphRule(
            RuleOp.Insert, 102, TsLive,
            Graphlet: removed.BeforeGraphlet!.Nodes,
            SharedRefresh: Array.Empty<EntityData>(), SharedDelete: Array.Empty<string>()));

        // The capture is shaped like an insert payload, so it restores the graphlet
        // verbatim — nodes, properties and outgoing edges (incl. edges to context that
        // survived, e.g. the owner history).
        Assert.Equal(nodesBefore, await OwnedNodeCount(102));
        Assert.Equal(edgesBefore, await OwnedEdgeCount(102));
        await using (var session = _driver.AsyncSession())
        {
            var gid = (await (await session.RunAsync(
                @"MATCH (n:PrimaryNode {timestamp: $ts, revit_element_id: $eid, EntityType: 'IfcWall'})
                  RETURN n.GlobalId AS g", new { ts = TsLive, eid = 102L })).ToListAsync())
                .Single()["g"].As<string>();
            Assert.Equal(wall2.GlobalId, gid);
        }

        // Boundary: the INCOMING glue is captured but not re-applied by an insert rule —
        // restoring shared context is undo's job (plan step 5), not step 1's.
        Assert.NotEmpty(removed.BeforeGraphlet!.IncomingGlue);
    }

    // ── portable context refs (rule persistence step 2) ───────────────────────────

    [Fact]
    public async Task Rule_names_every_external_reference_portably_and_they_resolve_back()
    {
        if (_driver is null) return;
        await Cleanup();

        var db = new DatabaseIfc(false, ReleaseVersion.IFC4);
        var building = new IfcBuilding(db, "B");
        var storey = new IfcBuildingStorey(building, "S", 0);
        var owner = new Dictionary<int, long>();

        var w0 = StepIdWatermark.Current(db);
        _ = new IfcWall(storey, null, null);
        var w1 = StepIdWatermark.Current(db);
        TagRange(db, w0, w1, 101, owner);
        await CypherEmitter.WriteAsync(_driver, db, TsLive, owner);

        // ── INSERT wall2: its rule glues to context it does not own ──
        _ = new IfcWall(storey, null, null);
        var w2 = StepIdWatermark.Current(db);
        TagRange(db, w1, w2, 102, owner);

        var (refresh, delete) = GraphletExtractor.StoreyContainmentChanges(new[] { storey }, TsLive);
        var applied = await CypherEmitter.ApplyRuleAsync(_driver, new GraphRule(
            RuleOp.Insert, 102, TsLive,
            Graphlet: GraphletExtractor.WalkNew(db, owner, w1, w2, TsLive),
            SharedRefresh: refresh, SharedDelete: delete));

        var (external, own) = applied.PartitionReferences();
        Assert.NotEmpty(external);

        // Every reference leaving the graphlet got a portable name — a gap here means a
        // stored rule would carry a p21 that means nothing in another host graph.
        var unresolved = external.Where(p => !applied.ContextRefs.ContainsKey(p)).ToList();
        Assert.Empty(unresolved);

        // The storey containment rel is an IfcRoot, so it anchors directly — no walk.
        var rel = storey.ContainsElements.Single();
        var relRef = applied.ContextRefs[rel.StepId];
        Assert.Equal(ContextAnchorKind.Connection, relRef.AnchorKind);
        Assert.Equal(rel.GlobalId, relRef.AnchorGlobalId);
        Assert.Empty(relRef.Steps);

        await using var session = _driver.AsyncSession();
        foreach (var (p21, contextRef) in applied.ContextRefs)
        {
            // A ref must lead back to the node it names…
            Assert.Equal(p21, await ContextResolver.FindAsync(session, TsLive, contextRef));

            // …and must never be anchored on the rule's own graphlet, which does not exist
            // yet on insert and is about to be destroyed on remove.
            Assert.DoesNotContain(own, ownP21 => ownP21 == p21);
            Assert.True(ContextRef.TryParse(contextRef.Path, out var reparsed));
            Assert.Equal(contextRef, reparsed);
        }
    }

    [Fact]
    public async Task Remove_rule_names_its_incoming_glue_source_portably()
    {
        if (_driver is null) return;
        await Cleanup();

        var db = new DatabaseIfc(false, ReleaseVersion.IFC4);
        var building = new IfcBuilding(db, "B");
        var storey = new IfcBuildingStorey(building, "S", 0);
        var owner = new Dictionary<int, long>();

        var w0 = StepIdWatermark.Current(db);
        var wall1 = new IfcWall(storey, null, null);
        var w1 = StepIdWatermark.Current(db);
        TagRange(db, w0, w1, 101, owner);
        // A second wall keeps the containment rel alive after wall1 goes.
        _ = new IfcWall(storey, null, null);
        var w2 = StepIdWatermark.Current(db);
        TagRange(db, w1, w2, 102, owner);
        await CypherEmitter.WriteAsync(_driver, db, TsLive, owner);

        var rel = storey.ContainsElements.Single();
        rel.RelatedElements.Remove(wall1);
        var (refresh, delete) = GraphletExtractor.StoreyContainmentChanges(new[] { storey }, TsLive);
        var applied = await CypherEmitter.ApplyRuleAsync(_driver, new GraphRule(
            RuleOp.Remove, 101, TsLive,
            Graphlet: Array.Empty<EntityData>(), SharedRefresh: refresh, SharedDelete: delete));

        // The glue edge that attached wall1 to the storey came FROM the containment rel;
        // the stored rule must name that source portably, not by its p21.
        var glue = Assert.Single(applied.BeforeGraphlet!.IncomingGlue, e => e.RelType == "RelatedElements");
        var sourceRef = applied.ContextRefs[glue.SourceP21];
        Assert.Equal(rel.GlobalId, sourceRef.AnchorGlobalId);

        // The rel survived the rule, so the ref still resolves against the updated graph.
        await using var session = _driver.AsyncSession();
        Assert.Equal(glue.SourceP21, await ContextResolver.FindAsync(session, TsLive, sourceRef));
    }

    private async Task<int> OwnedNodeCount(long eid)
    {
        await using var session = _driver!.AsyncSession();
        return (await (await session.RunAsync(
            "MATCH (n:GenericNode {timestamp: $ts, revit_element_id: $eid}) RETURN count(*) AS c",
            new { ts = TsLive, eid })).ToListAsync()).Single()["c"].As<int>();
    }

    private async Task<int> OwnedEdgeCount(long eid)
    {
        await using var session = _driver!.AsyncSession();
        return (await (await session.RunAsync(
            "MATCH (:GenericNode {timestamp: $ts, revit_element_id: $eid})-[e:rel]->() RETURN count(e) AS c",
            new { ts = TsLive, eid })).ToListAsync()).Single()["c"].As<int>();
    }

    /// <summary>Same tagging as ElementConverterRegistry.ConvertOne (incl. shared-type skip).</summary>
    private static void TagRange(DatabaseIfc db, int before, int after, long eid, Dictionary<int, long> map)
    {
        for (var id = before + 1; id <= after; id++)
        {
            if (db[id] is { } e && GraphRule.SharedResourceTypes.Contains(e.GetType().Name))
                continue;
            if (db[id] is not null)
                map[id] = eid;
        }
    }
}
