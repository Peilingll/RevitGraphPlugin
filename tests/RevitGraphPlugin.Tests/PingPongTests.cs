using GeometryGym.Ifc;
using Neo4j.Driver;
using RevitGraphPlugin.Cypher;
using RevitGraphPlugin.Ifc;
using Xunit;
using Xunit.Abstractions;

namespace RevitGraphPlugin.Tests;

/// <summary>
/// Repeated-reversibility acceptance: Esser 2022 §3.6 demands that the reverse
/// application of a rule returns the initial graph — the closed loops in
/// <see cref="RuleReplayTests"/> prove it ONCE, this test proves it stays true when
/// the chain is bounced N times (head → baseline → head …), the way checkout is
/// actually used. A rule whose undo∘replay is not exactly the identity leaves a
/// little drift each round; comparing every round against the round-0 signatures
/// turns "checkout sometimes fails" into "round K, seq S, direction D".
/// Walks through <see cref="RuleReplayer.CheckoutAsync"/> — the same path
/// checkout.ps1 drives — so the checked_out_seq bookkeeping is exercised too.
/// </summary>
public sealed class PingPongTests : IDisposable
{
    private const int Rounds = 10;
    private const string TsLive = "test-pong-live";
    private const string Gid1 = "1PongWallAlpha00000001";
    private const string Gid2 = "2PongWallBeta000000002";

    /// <summary>
    /// Identity columns that legitimately churn when a graphlet is rebuilt (same set
    /// GraphletDiff masks): p21s renumber, ggifc-generated GlobalIds are random per
    /// conversion — proven in GgifcIdentityTests. Everything else must round-trip.
    /// </summary>
    private static readonly HashSet<string> MaskedKeys = new(StringComparer.Ordinal)
    {
        "p21_id", "timestamp", "revit_element_id", "GlobalId",
    };

    private readonly IDriver? _driver;
    private readonly ITestOutputHelper _output;

    public PingPongTests(ITestOutputHelper output)
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
        await session.RunAsync(
            "MATCH (n) WHERE n.timestamp STARTS WITH $ts DETACH DELETE n", new { ts = TsLive });
    }

    /// <summary>
    /// The full mixed chain of RuleReplayTests (insert ×2 → rename stored as Modify →
    /// remove, so every stored op shape is on it), bounced Rounds times between head
    /// and baseline. Structure AND non-identity values must match round 0 every time.
    /// </summary>
    [Fact]
    public async Task Checkout_bounces_between_head_and_baseline_without_drift()
    {
        if (_driver is null) return;
        await Cleanup();

        var db = new DatabaseIfc(false, ReleaseVersion.IFC4);
        var building = new IfcBuilding(db, "B");
        var storey = new IfcBuildingStorey(building, "S", 0);
        var owner = new Dictionary<int, long>();

        var stats = await CypherEmitter.WriteAsync(_driver, db, TsLive, owner);
        await RuleStore.RecordBaselineAsync(_driver, TsLive, stats);       // seq 1

        // insert w1                                                       // seq 2
        var w0 = StepIdWatermark.Current(db);
        _ = new IfcWall(storey, null, null) { GlobalId = Gid1, Name = "Alpha" };
        var w1 = StepIdWatermark.Current(db);
        TagRange(db, w0, w1, 101, owner);
        await Apply(db, storey, owner, RuleOp.Insert, 101, w0, w1);

        // insert w2                                                       // seq 3
        _ = new IfcWall(storey, null, null) { GlobalId = Gid2, Name = "Beta" };
        var w2 = StepIdWatermark.Current(db);
        TagRange(db, w1, w2, 102, owner);
        await Apply(db, storey, owner, RuleOp.Insert, 102, w1, w2);

        // rename w2 → property-only Replace → stored as Modify            // seq 4
        var wall2a = db.OfType<IfcWall>().Single(w => w.GlobalId == Gid2);
        storey.ContainsElements.Single().RelatedElements.Remove(wall2a);
        var w3 = StepIdWatermark.Current(db);
        var wall2b = new IfcWall(storey, null, null) { GlobalId = Gid2, Name = "Beta-renamed" };
        var w4 = StepIdWatermark.Current(db);
        TagRange(db, w3, w4, 102, owner);
        var renamed = await Apply(db, storey, owner, RuleOp.Replace, 102, w3, w4);
        Assert.Equal("Modify", renamed.Stored!.Op);

        // remove w2 (SharedDelete: the containment rel of w2 empties? no — w1 stays,
        // so the rel survives; the mixed SharedDelete path is covered by the real
        // chain in ManualChainTools)                                      // seq 5
        storey.ContainsElements.Single().RelatedElements.Remove(wall2b);
        await Apply(db, storey, owner, RuleOp.Remove, 102, 0, 0);

        const long headSeq = 5, baseSeq = 1;
        var sigHead0 = await Signature();
        _output.WriteLine($"round 0: head = {sigHead0.Describe()}");

        var toBase = await RuleReplayer.CheckoutAsync(_driver, TsLive, TsLive, baseSeq);
        Assert.Equal((headSeq, baseSeq, 4), (toBase.From, toBase.To, toBase.Steps));
        var sigBase0 = await Signature();
        _output.WriteLine($"round 0: base = {sigBase0.Describe()}");

        for (var round = 1; round <= Rounds; round++)
        {
            var up = await RuleReplayer.CheckoutAsync(_driver, TsLive, TsLive, headSeq);
            Assert.Equal(4, up.Steps);
            AssertSame(sigHead0, await Signature(), round, "replay to head");

            var down = await RuleReplayer.CheckoutAsync(_driver, TsLive, TsLive, baseSeq);
            Assert.Equal(4, down.Steps);
            AssertSame(sigBase0, await Signature(), round, "undo to baseline");
        }
    }

    // ── signature: structure + non-identity values ───────────────────────────────

    private sealed record Sig(
        Dictionary<string, int> Nodes, Dictionary<string, int> Edges, Dictionary<string, int> Values)
    {
        public string Describe() =>
            $"{Nodes.Values.Sum()} nodes / {Edges.Values.Sum()} edges / {Values.Count} distinct value rows";
    }

    private async Task<Sig> Signature()
    {
        await using var session = _driver!.AsyncSession();

        var nodes = new Dictionary<string, int>();
        foreach (var r in await (await session.RunAsync(
            "MATCH (n {timestamp: $ts}) RETURN n.EntityType AS t, count(*) AS c",
            new { ts = TsLive })).ToListAsync())
            nodes[r["t"].As<string>()] = r["c"].As<int>();

        var edges = new Dictionary<string, int>();
        foreach (var r in await (await session.RunAsync(
            @"MATCH (a {timestamp: $ts})-[e:rel]->(b {timestamp: $ts})
              RETURN a.EntityType + '|' + e.rel_type + '|' + toString(e.list_index) + '|' + b.EntityType AS k,
                     count(*) AS c", new { ts = TsLive })).ToListAsync())
            edges[r["k"].As<string>()] = r["c"].As<int>();

        // Every non-identity property value in the graph, as a multiset of
        // "EntityType|key=value" rows — this is what catches value drift (a Name or
        // IsExternal bounced to the wrong side) that the structural half cannot see.
        var values = new Dictionary<string, int>();
        foreach (var r in await (await session.RunAsync(
            "MATCH (n {timestamp: $ts}) RETURN n.EntityType AS t, properties(n) AS p",
            new { ts = TsLive })).ToListAsync())
        {
            var t = r["t"].As<string>();
            foreach (var (key, value) in r["p"].As<IDictionary<string, object>>())
            {
                if (MaskedKeys.Contains(key)) continue;
                var row = $"{t}|{key}={value}";
                values[row] = values.GetValueOrDefault(row) + 1;
            }
        }

        return new Sig(nodes, edges, values);
    }

    private void AssertSame(Sig expected, Sig actual, int round, string direction)
    {
        var drift = new List<string>();
        Diff("node", expected.Nodes, actual.Nodes, drift);
        Diff("edge", expected.Edges, actual.Edges, drift);
        Diff("value", expected.Values, actual.Values, drift);
        if (drift.Count == 0) return;

        foreach (var line in drift) _output.WriteLine(line);
        Assert.Fail($"round {round}, {direction}: {drift.Count} drifted rows (see output)");
    }

    private static void Diff(
        string kind, Dictionary<string, int> expected, Dictionary<string, int> actual, List<string> drift)
    {
        foreach (var key in expected.Keys.Union(actual.Keys))
        {
            var (e, a) = (expected.GetValueOrDefault(key), actual.GetValueOrDefault(key));
            if (e != a) drift.Add($"  {kind} '{key}': expected {e}, got {a}");
        }
    }

    // ── chain-building helpers (RuleReplayTests idiom) ───────────────────────────

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
