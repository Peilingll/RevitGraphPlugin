using GeometryGym.Ifc;
using Neo4j.Driver;
using RevitGraphPlugin.Cypher;
using RevitGraphPlugin.Ifc;
using Xunit;
using Xunit.Abstractions;

namespace RevitGraphPlugin.Tests;

/// <summary>
/// Two consecutive property-only modifies on the same element, then checkout below the
/// second: the first Modify's names must still resolve. Requires the pset GlobalId to be
/// stable across re-conversions (StableIds).
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
        _driver = Neo4jTest.TryConnect(_output);
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
    public async Task Two_consecutive_modifies_then_checkout_to_baseline_and_back()
    {
        Skip.If(_driver is null, Neo4jTest.SkipReason);
        await Cleanup();

        var db = new DatabaseIfc(ReleaseVersion.IFC4A2);
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

    /// <summary>Wall + Pset_WallCommon(IsExternal), GlobalIds seeded through StableIds as WallConverter does.</summary>
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
