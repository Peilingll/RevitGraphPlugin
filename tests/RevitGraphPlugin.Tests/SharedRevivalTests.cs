using GeometryGym.Ifc;
using Neo4j.Driver;
using RevitGraphPlugin.Cypher;
using RevitGraphPlugin.Ifc;
using Xunit;
using Xunit.Abstractions;

namespace RevitGraphPlugin.Tests;

/// <summary>
/// A containment rel dropped when its storey emptied is re-joined by the next element
/// (ggifc reuses the object) and arrives as a refresh of a node the graph no longer
/// holds. The apply must record it as inserted so the chain can rebuild it.
/// </summary>
public sealed class SharedRevivalTests : IDisposable
{
    private const string Ts = "test-shared-revival";
    private const string Gid1 = "1RevivalWallA00000001";
    private const string Gid2 = "1RevivalWallB00000002";
    private readonly IDriver? _driver;
    private readonly ITestOutputHelper _output;

    public SharedRevivalTests(ITestOutputHelper output)
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
            _output.WriteLine($"Neo4j unreachable: {ex.Message}");
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
        await session.RunAsync("MATCH (n) WHERE n.timestamp STARTS WITH $ts DETACH DELETE n", new { ts = Ts });
    }

    [SkippableFact]
    public async Task Containment_rel_emptied_then_rejoined_is_recorded_and_replays()
    {
        Skip.If(_driver is null, "Neo4j not reachable at bolt://127.0.0.1:7687");
        await Cleanup();

        var db = new DatabaseIfc(false, ReleaseVersion.IFC4);
        var building = new IfcBuilding(db, "B");
        var storey = new IfcBuildingStorey(building, "S", 0);
        var owner = new Dictionary<int, long>();

        var stats = await CypherEmitter.WriteAsync(_driver, db, Ts, owner);
        await RuleStore.RecordBaselineAsync(_driver, Ts, stats);                          // seq 1

        // wall A: first element on the storey → the containment rel rides in its Insert.
        var w0 = StepIdWatermark.Current(db);
        var wallA = new IfcWall(storey, null, null) { GlobalId = Gid1, Name = "A" };
        StableIds.StampContainment(wallA);
        var w1 = StepIdWatermark.Current(db);
        TagRange(db, w0, w1, 101, owner);
        await Apply(db, storey, owner, RuleOp.Insert, 101, w0, w1);                        // seq 2
        var rel = storey.ContainsElements.Single();

        // remove wall A → the rel is memberless → SharedDelete drops its graph node.
        LiveRuleBuilder.DetachFromContainment(wallA);
        LiveRuleBuilder.ForgetOwnership(owner, 101);
        var remove = await Apply(db, storey, owner, RuleOp.Remove, 101, 0, 0);            // seq 3
        Assert.Contains($"#{rel.StepId}", remove.SharedDelete);
        Assert.Equal(0, await Count("IfcRelContainedInSpatialStructure"));

        // wall B: re-joins the same rel object → must be revived as an insert.
        var w2 = StepIdWatermark.Current(db);
        var wallB = new IfcWall(storey, null, null) { GlobalId = Gid2, Name = "B" };
        StableIds.StampContainment(wallB);
        var w3 = StepIdWatermark.Current(db);
        Assert.Same(rel, storey.ContainsElements.Single());
        TagRange(db, w2, w3, 102, owner);
        var insertB = await Apply(db, storey, owner, RuleOp.Insert, 102, w2, w3);          // seq 4
        Assert.Equal(1, await Count("IfcRelContainedInSpatialStructure"));
        Assert.Contains(insertB.Graphlet, d => d.EntityType == nameof(IfcRelContainedInSpatialStructure));
        Assert.DoesNotContain(insertB.SharedRefresh, d => d.EntityType == nameof(IfcRelContainedInSpatialStructure));
        Assert.Equal(1, await CopiesOf(insertB.Stored!.Seq, "IfcRelContainedInSpatialStructure"));

        // The whole point: the chain can rebuild that state from the baseline and back.
        await RuleReplayer.CheckoutAsync(_driver, Ts, Ts, 1);
        Assert.Equal(0, await Count("IfcRelContainedInSpatialStructure"));
        await RuleReplayer.CheckoutAsync(_driver, Ts, Ts, 4);
        Assert.Equal(1, await Count("IfcRelContainedInSpatialStructure"));
        Assert.Equal(1, await Members());
        await RuleReplayer.CheckoutAsync(_driver, Ts, Ts, 3);
        Assert.Equal(0, await Count("IfcRelContainedInSpatialStructure"));
        await RuleReplayer.CheckoutAsync(_driver, Ts, Ts, 2);
        Assert.Equal(1, await Members());
        await RuleReplayer.CheckoutAsync(_driver, Ts, Ts, 4);
        Assert.Equal(1, await Members());
    }

    /// <summary>A lingering memberless rel (listed for deletion by every later rule) must not cost later rules their alignment.</summary>
    [SkippableFact]
    public async Task Lingering_memberless_rel_does_not_break_alignment_of_later_rules()
    {
        Skip.If(_driver is null, "Neo4j not reachable at bolt://127.0.0.1:7687");
        await Cleanup();

        var db = new DatabaseIfc(false, ReleaseVersion.IFC4);
        var building = new IfcBuilding(db, "B");
        var storey1 = new IfcBuildingStorey(building, "S1", 0);
        var storey2 = new IfcBuildingStorey(building, "S2", 4000);
        var storeys = new[] { storey1, storey2 };
        var owner = new Dictionary<int, long>();

        var stats = await CypherEmitter.WriteAsync(_driver, db, Ts, owner);
        await RuleStore.RecordBaselineAsync(_driver, Ts, stats);

        var w0 = StepIdWatermark.Current(db);
        var wallA = new IfcWall(storey1, null, null) { GlobalId = Gid1, Name = "A" };
        StableIds.StampContainment(wallA);
        var w1 = StepIdWatermark.Current(db);
        TagRange(db, w0, w1, 101, owner);
        await ApplyOn(db, storeys, owner, RuleOp.Insert, 101, w0, w1);

        var w2 = StepIdWatermark.Current(db);
        var wallB = new IfcWall(storey2, null, null) { GlobalId = Gid2, Name = "B" };
        StableIds.StampContainment(wallB);
        StableIds.AttachPset(wallB, "Pset_WallCommon",
            new IfcPropertySingleValue(db, "IsExternal", new IfcBoolean(true)));
        var w3 = StepIdWatermark.Current(db);
        TagRange(db, w2, w3, 102, owner);
        await ApplyOn(db, storeys, owner, RuleOp.Insert, 102, w2, w3);

        // Empty storey 1: its rel node is dropped, the ggifc object lingers memberless.
        LiveRuleBuilder.DetachFromContainment(wallA);
        LiveRuleBuilder.ForgetOwnership(owner, 101);
        var remove = await ApplyOn(db, storeys, owner, RuleOp.Remove, 101, 0, 0);
        Assert.Single(remove.SharedDelete);

        // Rename wall B (a re-conversion with one value changed) — must still align.
        storey2.ContainsElements.Single().RelatedElements.Remove(wallB);
        LiveRuleBuilder.ForgetOwnership(owner, 102);
        var w4 = StepIdWatermark.Current(db);
        var wallB2 = new IfcWall(storey2, null, null) { GlobalId = Gid2, Name = "B-renamed" };
        StableIds.StampContainment(wallB2);
        StableIds.AttachPset(wallB2, "Pset_WallCommon",
            new IfcPropertySingleValue(db, "IsExternal", new IfcBoolean(true)));
        var w5 = StepIdWatermark.Current(db);
        TagRange(db, w4, w5, 102, owner);
        var rename = await ApplyOn(db, storeys, owner, RuleOp.Replace, 102, w4, w5);

        Assert.Empty(rename.SharedDelete);                       // the lingering rel was filtered out
        Assert.Equal(GraphletDiffKind.PropertyOnly, rename.Diff!.Kind);
        Assert.Equal("Modify", rename.Stored!.Op);
    }

    private async Task<GraphRule> ApplyOn(
        DatabaseIfc db, IfcBuildingStorey[] storeys, Dictionary<int, long> owner,
        RuleOp op, long eid, int before, int after)
    {
        var (refresh, delete) = GraphletExtractor.StoreyContainmentChanges(storeys, Ts);
        var graphlet = op == RuleOp.Remove
            ? Array.Empty<EntityData>()
            : (IReadOnlyList<EntityData>)GraphletExtractor.WalkNew(db, owner, before, after, Ts);
        return await CypherEmitter.ApplyRuleAsync(_driver!, new GraphRule(op, eid, Ts, graphlet, refresh, delete));
    }

    private async Task<int> Count(string entityType)
    {
        await using var session = _driver!.AsyncSession();
        return (await (await session.RunAsync(
            "MATCH (n {timestamp: $ts, EntityType: $t}) RETURN count(n) AS c",
            new { ts = Ts, t = entityType })).ToListAsync()).Single()["c"].As<int>();
    }

    private async Task<int> Members()
    {
        await using var session = _driver!.AsyncSession();
        return (await (await session.RunAsync(
            @"MATCH (c {timestamp: $ts, EntityType: 'IfcRelContainedInSpatialStructure'})-[:rel {rel_type: 'RelatedElements'}]->(e)
              RETURN count(e) AS c", new { ts = Ts })).ToListAsync()).Single()["c"].As<int>();
    }

    private async Task<int> CopiesOf(long seq, string entityType)
    {
        await using var session = _driver!.AsyncSession();
        return (await (await session.RunAsync(
            "MATCH (r:Rule {target_ts: $t, seq: $seq})-[:INSERTS]->(x {EntityType: $et}) RETURN count(x) AS c",
            new { t = Ts, seq, et = entityType })).ToListAsync()).Single()["c"].As<int>();
    }

    private async Task<GraphRule> Apply(
        DatabaseIfc db, IfcBuildingStorey storey, Dictionary<int, long> owner,
        RuleOp op, long eid, int before, int after)
    {
        var (refresh, delete) = GraphletExtractor.StoreyContainmentChanges(new[] { storey }, Ts);
        var graphlet = op == RuleOp.Remove
            ? Array.Empty<EntityData>()
            : (IReadOnlyList<EntityData>)GraphletExtractor.WalkNew(db, owner, before, after, Ts);
        return await CypherEmitter.ApplyRuleAsync(_driver!, new GraphRule(op, eid, Ts, graphlet, refresh, delete));
    }

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
