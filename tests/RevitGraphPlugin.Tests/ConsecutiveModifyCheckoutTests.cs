using GeometryGym.Ifc;
using Neo4j.Driver;
using RevitGraphPlugin.Cypher;
using RevitGraphPlugin.Ifc;
using Xunit;
using Xunit.Abstractions;

namespace RevitGraphPlugin.Tests;

/// <summary>
/// Acceptance test for stable GlobalIds on pset / rel nodes (2026-09-11 finding).
/// Two consecutive property-only modifies on the same element, then checkout below the
/// second one. Under the shallow apply every re-conversion rebuilds the pset with fresh
/// p21s; before StableIds it also got a fresh ggifc-random GlobalId, so the FIRST
/// Modify's three names (L path, R path, R p21) all pointed at nodes the SECOND rebuild
/// had already replaced — undo of seq 3 threw <c>context not found</c>. A change on the
/// product itself (its Name) never tripped this: the product's GlobalId is Revit-derived.
/// With the pset GlobalId seeded from the wall, the L / R paths are the same name and
/// resolve in every graph state.
/// </summary>
public sealed class ConsecutiveModifyCheckoutTests : IDisposable
{
    private const string Ts = "test-2mod-checkout";
    private const string Gid1 = "1ProbeWallAlpha0000001";
    private readonly IDriver? _driver;
    private readonly ITestOutputHelper _output;

    public ConsecutiveModifyCheckoutTests(ITestOutputHelper output)
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

    [Fact]
    public async Task Two_consecutive_modifies_then_checkout_to_baseline_and_back()
    {
        if (_driver is null) return;
        await Cleanup();

        var db = new DatabaseIfc(false, ReleaseVersion.IFC4);
        var building = new IfcBuilding(db, "B");
        var storey = new IfcBuildingStorey(building, "S", 0);
        var owner = new Dictionary<int, long>();

        var stats = await CypherEmitter.WriteAsync(_driver!, db, Ts, owner);
        await RuleStore.RecordBaselineAsync(_driver!, Ts, stats);                 // seq 1

        var w0 = StepIdWatermark.Current(db);
        BuildWall(db, storey, isExternal: true);
        var w1 = StepIdWatermark.Current(db);
        TagRange(db, w0, w1, 101, owner);
        await Apply(db, storey, owner, RuleOp.Insert, 101, w0, w1);               // seq 2

        // toggle IsExternal #1 (true → false) → Modify                       // seq 3
        var r1 = await Reconvert(db, storey, owner, isExternal: false);
        Assert.Equal("Modify", r1.Stored!.Op);
        // toggle IsExternal #2 (false → true) → Modify                       // seq 4
        var r2 = await Reconvert(db, storey, owner, isExternal: true);
        Assert.Equal("Modify", r2.Stored!.Op);

        _output.WriteLine($"names after live: {await Names()}");

        var down = await RuleReplayer.CheckoutAsync(_driver!, Ts, Ts, 1);
        _output.WriteLine($"checkout -> 1: {down.From} -> {down.To} ({down.Steps} steps); names: {await Names()}");
        var up = await RuleReplayer.CheckoutAsync(_driver!, Ts, Ts, 4);
        _output.WriteLine($"checkout -> 4: {up.From} -> {up.To} ({up.Steps} steps); names: {await Names()}");
        var down2 = await RuleReplayer.CheckoutAsync(_driver!, Ts, Ts, 2);
        _output.WriteLine($"checkout -> 2: {down2.From} -> {down2.To} ({down2.Steps} steps); names: {await Names()}");
    }

    private async Task<string> Names()
    {
        await using var session = _driver!.AsyncSession();
        var rows = await (await session.RunAsync(
            @"MATCH (w {timestamp: $ts, EntityType: 'IfcWall'})
              OPTIONAL MATCH (ps {timestamp: $ts, EntityType: 'IfcPropertySet'})-[:rel]->(v {timestamp: $ts, EntityType: 'IfcPropertySingleValue'})
              RETURN w.p21_id AS wp, ps.GlobalId AS pg, v.NominalValue AS val, v.p21_id AS vp",
            new { ts = Ts })).ToListAsync();
        return string.Join(" | ", rows.Select(r => $"wall@{r["wp"]} pset={r["pg"]} IsExternal={r["val"]}@{r["vp"]}"));
    }

    /// <summary>
    /// The converter shape: wall + Pset_WallCommon(IsExternal), pset / rel / containment
    /// GlobalIds seeded through StableIds exactly as WallConverter does. (With raw ggifc
    /// psets — random GlobalIds per build — this test's checkout to seq 1 throws
    /// <c>context not found</c> on undoing seq 3; that run is recorded in
    /// doc/log/2026-09-11.)
    /// </summary>
    private static void BuildWall(DatabaseIfc db, IfcBuildingStorey storey, bool isExternal)
    {
        var wall = new IfcWall(storey, null, null) { GlobalId = Gid1, Name = "A" };
        StableIds.StampContainment(wall);
        StableIds.AttachPset(wall, "Pset_WallCommon",
            new IfcPropertySingleValue(db, "IsExternal", new IfcBoolean(isExternal)));
    }

    private async Task<GraphRule> Reconvert(DatabaseIfc db, IfcBuildingStorey storey, Dictionary<int, long> owner, bool isExternal)
    {
        var wall = db.OfType<IfcWall>().Single(w => w.GlobalId == Gid1 && w.ContainedInStructure is not null);
        storey.ContainsElements.Single().RelatedElements.Remove(wall);
        var b = StepIdWatermark.Current(db);
        BuildWall(db, storey, isExternal);
        var a = StepIdWatermark.Current(db);
        TagRange(db, b, a, 101, owner);
        return await Apply(db, storey, owner, RuleOp.Replace, 101, b, a);
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
