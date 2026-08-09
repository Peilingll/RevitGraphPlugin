using GeometryGym.Ifc;
using Neo4j.Driver;
using RevitGraphPlugin.Cypher;
using RevitGraphPlugin.Ifc;
using Xunit;
using Xunit.Abstractions;

namespace RevitGraphPlugin.Tests;

/// <summary>
/// Rule persistence step 5 — the closed loops that accept the whole chain design
/// (plan §7): REPLAY of every stored rule over a copy of the baseline must reproduce
/// the current-state graph, and UNDO of every rule must take the current-state graph
/// back to its baseline. Comparison is the structural signature (EntityType and edge
/// triples with counts) — p21-agnostic, since a Modify-collapsed rule leaves replay
/// with the original p21s where the live graph renumbered.
/// </summary>
public sealed class RuleReplayTests : IDisposable
{
    private const string TsLive   = "test-rplay-live";
    private const string TsReplay = "test-rplay-out";
    private const string Gid1 = "1ReplayWallAlpha000001";
    private const string Gid2 = "2ReplayWallBeta0000002";

    private readonly IDriver? _driver;
    private readonly ITestOutputHelper _output;

    public RuleReplayTests(ITestOutputHelper output)
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
        foreach (var ts in new[] { TsLive, TsReplay })
            await session.RunAsync(
                "MATCH (n) WHERE n.timestamp STARTS WITH $ts DETACH DELETE n", new { ts });
    }

    // ── structural signature (p21-agnostic), as in ApplyRuleIntegrationTests ──────

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
        (Dictionary<string, int> nodes, Dictionary<string, int> edges) actual,
        (Dictionary<string, int> nodes, Dictionary<string, int> edges) expected)
    {
        Assert.Equal(
            expected.nodes.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"),
            actual.nodes.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"));
        Assert.Equal(
            expected.edges.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"),
            actual.edges.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"));
    }

    // ── the loops ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// insert w1 → insert w2 → rename w2 (stored as Modify) → remove w2. Tail-only
    /// membership changes, so no containment renumbering ambiguity; the final state is
    /// NOT the baseline (w1 remains), so a do-nothing replay cannot pass by accident.
    /// </summary>
    [Fact]
    public async Task Replay_reproduces_the_live_graph_and_undo_returns_it_to_baseline()
    {
        if (_driver is null) return;
        await Cleanup();

        var db = new DatabaseIfc(false, ReleaseVersion.IFC4);
        var building = new IfcBuilding(db, "B");
        var storey = new IfcBuildingStorey(building, "S", 0);
        var owner = new Dictionary<int, long>();

        // Baseline: written to TsLive (with its chain anchor) AND to TsReplay — the
        // "apply the baseline" step of the replay loop.
        var stats = await CypherEmitter.WriteAsync(_driver, db, TsLive, owner);
        await RuleStore.RecordBaselineAsync(_driver, TsLive, stats);
        await CypherEmitter.WriteAsync(_driver, db, TsReplay, owner);
        var baselineSig = await Signature(TsLive);

        // insert w1
        var w0 = StepIdWatermark.Current(db);
        _ = new IfcWall(storey, null, null) { GlobalId = Gid1, Name = "Alpha" };
        var w1 = StepIdWatermark.Current(db);
        TagRange(db, w0, w1, 101, owner);
        await Apply(db, storey, owner, RuleOp.Insert, 101, w0, w1);

        // insert w2
        _ = new IfcWall(storey, null, null) { GlobalId = Gid2, Name = "Beta" };
        var w2 = StepIdWatermark.Current(db);
        TagRange(db, w1, w2, 102, owner);
        await Apply(db, storey, owner, RuleOp.Insert, 102, w1, w2);

        // rename w2 → property-only Replace → stored as Modify
        var wall2a = db.OfType<IfcWall>().Single(w => w.GlobalId == Gid2);
        storey.ContainsElements.Single().RelatedElements.Remove(wall2a);
        var w3 = StepIdWatermark.Current(db);
        var wall2b = new IfcWall(storey, null, null) { GlobalId = Gid2, Name = "Beta-renamed" };
        var w4 = StepIdWatermark.Current(db);
        TagRange(db, w3, w4, 102, owner);
        var renamed = await Apply(db, storey, owner, RuleOp.Replace, 102, w3, w4);
        Assert.Equal("Modify", renamed.Stored!.Op);

        // remove w2 (tail member — no renumbering of w1)
        storey.ContainsElements.Single().RelatedElements.Remove(wall2b);
        await Apply(db, storey, owner, RuleOp.Remove, 102, 0, 0);

        var liveSig = await Signature(TsLive);

        // ── REPLAY loop: baseline copy + chain ⇒ live graph ──
        var replayed = await RuleReplayer.ReplayAsync(_driver, TsLive, TsReplay);
        Assert.Equal(4, replayed);
        AssertSameSignature(await Signature(TsReplay), liveSig);

        // ── UNDO loop: live graph − chain ⇒ baseline ──
        var undone = await RuleReplayer.UndoAsync(_driver, TsLive, TsLive);
        Assert.Equal(4, undone);
        AssertSameSignature(await Signature(TsLive), baselineSig);
    }

    /// <summary>
    /// insert w → remove w: the removal empties the containment rel (SharedDelete).
    /// Replay must drop the rel too (via the stored portable refs) or the loop ends one
    /// node heavy; undo must restore rel AND wall from the L copies, then take both away.
    /// </summary>
    [Fact]
    public async Task Shared_delete_survives_the_loop_in_both_directions()
    {
        if (_driver is null) return;
        await Cleanup();

        var db = new DatabaseIfc(false, ReleaseVersion.IFC4);
        var building = new IfcBuilding(db, "B");
        var storey = new IfcBuildingStorey(building, "S", 0);
        var owner = new Dictionary<int, long>();

        var stats = await CypherEmitter.WriteAsync(_driver, db, TsLive, owner);
        await RuleStore.RecordBaselineAsync(_driver, TsLive, stats);
        await CypherEmitter.WriteAsync(_driver, db, TsReplay, owner);
        var baselineSig = await Signature(TsLive);

        var w0 = StepIdWatermark.Current(db);
        var wall = new IfcWall(storey, null, null) { GlobalId = Gid1, Name = "Solo" };
        var w1 = StepIdWatermark.Current(db);
        TagRange(db, w0, w1, 101, owner);
        await Apply(db, storey, owner, RuleOp.Insert, 101, w0, w1);
        var afterInsertSig = await Signature(TsLive);

        storey.ContainsElements.Single().RelatedElements.Remove(wall);
        var removed = await Apply(db, storey, owner, RuleOp.Remove, 101, 0, 0);
        Assert.NotEmpty(removed.SharedDelete);                       // the rel went
        Assert.NotEmpty(removed.BeforeGraphlet!.SharedDeletedOrEmpty); // …and was captured

        // Replay ends at baseline — a leftover rel would make this fail.
        Assert.Equal(2, await RuleReplayer.ReplayAsync(_driver, TsLive, TsReplay));
        AssertSameSignature(await Signature(TsReplay), baselineSig);

        // Undo the remove alone: wall AND rel come back.
        Assert.Equal(1, await RuleReplayer.UndoAsync(_driver, TsLive, TsLive, count: 1));
        AssertSameSignature(await Signature(TsLive), afterInsertSig);

        // Continue below the already-undone remove: the insert goes, back to baseline.
        Assert.Equal(1, await RuleReplayer.UndoAsync(
            _driver, TsLive, TsLive, count: 1, belowSeq: removed.Stored!.Seq));
        AssertSameSignature(await Signature(TsLive), baselineSig);
    }

    // ── helpers ───────────────────────────────────────────────────────────────────

    /// <summary>Build + apply one rule the way the live path does.</summary>
    private async Task<GraphRule> Apply(
        DatabaseIfc db, IfcBuildingStorey storey, Dictionary<int, long> owner,
        RuleOp op, long eid, int before, int after)
    {
        var (refresh, delete) = GraphletExtractor.StoreyContainmentChanges(new[] { storey }, TsLive);
        var graphlet = op == RuleOp.Remove
            ? Array.Empty<EntityData>()
            : (IReadOnlyList<EntityData>)GraphletExtractor.WalkNew(db, owner, before, after, TsLive);
        return await CypherEmitter.ApplyRuleAsync(_driver!, new GraphRule(
            op, eid, TsLive, graphlet, refresh, delete));
    }

    private static IfcWall FindWall(DatabaseIfc db, string globalId)
        => db.OfType<IfcWall>().Single(w => w.GlobalId == globalId);

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

    /// <summary>Undo of a Remove must stop at nothing it cannot see — guard: chain must not cross a baseline.</summary>
    [Fact]
    public async Task Undo_stops_at_a_baseline_anchor()
    {
        if (_driver is null) return;
        await Cleanup();

        var db = new DatabaseIfc(false, ReleaseVersion.IFC4);
        var building = new IfcBuilding(db, "B");
        var storey = new IfcBuildingStorey(building, "S", 0);
        var owner = new Dictionary<int, long>();

        var stats = await CypherEmitter.WriteAsync(_driver, db, TsLive, owner);
        await RuleStore.RecordBaselineAsync(_driver, TsLive, stats);

        var w0 = StepIdWatermark.Current(db);
        _ = new IfcWall(storey, null, null) { GlobalId = Gid1 };
        var w1 = StepIdWatermark.Current(db);
        TagRange(db, w0, w1, 101, owner);
        await Apply(db, storey, owner, RuleOp.Insert, 101, w0, w1);

        // Re-baseline: rules before this anchor are unreachable for undo.
        stats = await CypherEmitter.WriteAsync(_driver, db, TsLive, owner);
        await RuleStore.RecordBaselineAsync(_driver, TsLive, stats);

        Assert.Equal(0, await RuleReplayer.UndoAsync(_driver, TsLive, TsLive));
    }
}
