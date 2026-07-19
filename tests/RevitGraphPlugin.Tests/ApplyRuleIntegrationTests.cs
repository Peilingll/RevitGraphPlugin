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
/// timestamp and asserts the核心不變量: the incrementally maintained graph equals a
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
        foreach (var ts in new[] { TsLive, TsRef })
            await session.RunAsync("MATCH (n {timestamp: $ts}) DETACH DELETE n", new { ts });
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
