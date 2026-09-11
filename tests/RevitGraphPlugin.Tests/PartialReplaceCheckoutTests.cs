using GeometryGym.Ifc;
using Neo4j.Driver;
using RevitGraphPlugin.Cypher;
using RevitGraphPlugin.Ifc;
using Xunit;
using Xunit.Abstractions;

namespace RevitGraphPlugin.Tests;

/// <summary>
/// Acceptance test for the partial replace (doc_process/2026-09-11-plan-partial-replace.md):
/// a structural change that keeps most of the graphlet must store only the pushout, keep
/// the interface nodes in place (renumbered to the mirror's p21s), and stay fully
/// reversible. The chain: insert a wall with one pset property → re-convert with a second
/// property (Partial: one node inserted) → re-convert without it (Partial: one node
/// deleted) → checkout 1 / head / 2 / head, comparing node + edge + value signatures
/// against the states the live apply produced.
/// </summary>
public sealed class PartialReplaceCheckoutTests : IDisposable
{
    private const string Ts = "test-partial-checkout";
    private const string Gid = "1PartialWall000000001";
    private readonly IDriver? _driver;
    private readonly ITestOutputHelper _output;

    public PartialReplaceCheckoutTests(ITestOutputHelper output)
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
    public async Task Structural_change_stores_only_the_pushout_and_round_trips()
    {
        Skip.If(_driver is null, "Neo4j not reachable at bolt://127.0.0.1:7687 (set NEO4J_LOCAL_PASSWORD, start the instance)");
        await Cleanup();

        var db = new DatabaseIfc(false, ReleaseVersion.IFC4);
        var building = new IfcBuilding(db, "B");
        var storey = new IfcBuildingStorey(building, "S", 0);
        var owner = new Dictionary<int, long>();

        var stats = await CypherEmitter.WriteAsync(_driver, db, Ts, owner);
        await RuleStore.RecordBaselineAsync(_driver, Ts, stats);                       // seq 1

        var w0 = StepIdWatermark.Current(db);
        BuildWall(db, storey, loadBearing: false);
        var w1 = StepIdWatermark.Current(db);
        TagRange(db, w0, w1, 101, owner);
        await Apply(db, storey, owner, RuleOp.Insert, 101, w0, w1);                     // seq 2
        var sig2 = await Signature();

        // Add a property → Partial: interface = wall, pset, rel, IsExternal; pushout R = LoadBearing.
        var r1 = await Reconvert(db, storey, owner, loadBearing: true);                // seq 3
        Assert.Equal(GraphletDiffKind.Partial, r1.Diff!.Kind);
        Assert.Equal("Replace", r1.Stored!.Op);
        Assert.Empty(r1.Diff.PushoutL);
        Assert.Single(r1.Diff.PushoutR);
        await AssertRuleCopies(r1.Stored.Seq, deletes: 0, inserts: 1);
        await AssertInterfaceFollowsTheMirror(db);
        var sig3 = await Signature();

        // Remove it again → Partial: pushout L = LoadBearing.
        var r2 = await Reconvert(db, storey, owner, loadBearing: false);               // seq 4
        Assert.Equal(GraphletDiffKind.Partial, r2.Diff!.Kind);
        Assert.Single(r2.Diff.PushoutL);
        Assert.Empty(r2.Diff.PushoutR);
        await AssertRuleCopies(r2.Stored!.Seq, deletes: 1, inserts: 0);
        await AssertInterfaceFollowsTheMirror(db);
        var sig4 = await Signature();
        Assert.Equal(sig2.Describe(), sig4.Describe());   // same shape as before the detour

        // Round trips through the chain, every stop compared with the live state.
        await RuleReplayer.CheckoutAsync(_driver, Ts, Ts, 1);
        await RuleReplayer.CheckoutAsync(_driver, Ts, Ts, 3);
        AssertSame(sig3, await Signature(), "replay to 3");
        await RuleReplayer.CheckoutAsync(_driver, Ts, Ts, 4);
        AssertSame(sig4, await Signature(), "replay to 4");
        await RuleReplayer.CheckoutAsync(_driver, Ts, Ts, 2);
        AssertSame(sig2, await Signature(), "undo to 2");
        await RuleReplayer.CheckoutAsync(_driver, Ts, Ts, 4);
        AssertSame(sig4, await Signature(), "replay to head");
        await AssertInterfaceFollowsTheMirror(db);      // back at head: p21s match the mirror again
    }

    // ── helpers ──────────────────────────────────────────────────────────────────

    private static void BuildWall(DatabaseIfc db, IfcBuildingStorey storey, bool loadBearing)
    {
        var wall = new IfcWall(storey, null, null) { GlobalId = Gid, Name = "W" };
        StableIds.StampContainment(wall);
        var props = new List<IfcProperty> { new IfcPropertySingleValue(db, "IsExternal", new IfcBoolean(true)) };
        if (loadBearing) props.Add(new IfcPropertySingleValue(db, "LoadBearing", new IfcBoolean(true)));
        StableIds.AttachPset(wall, "Pset_WallCommon", props.ToArray());
    }

    private async Task<GraphRule> Reconvert(DatabaseIfc db, IfcBuildingStorey storey, Dictionary<int, long> owner, bool loadBearing)
    {
        var wall = db.OfType<IfcWall>().Single(w => w.GlobalId == Gid && w.ContainedInStructure is not null);
        storey.ContainsElements.Single().RelatedElements.Remove(wall);
        var b = StepIdWatermark.Current(db);
        BuildWall(db, storey, loadBearing);
        var a = StepIdWatermark.Current(db);
        TagRange(db, b, a, 101, owner);
        return await Apply(db, storey, owner, RuleOp.Replace, 101, b, a);
    }

    private async Task<GraphRule> Apply(
        DatabaseIfc db, IfcBuildingStorey storey, Dictionary<int, long> owner,
        RuleOp op, long eid, int before, int after)
    {
        var (refresh, delete) = GraphletExtractor.StoreyContainmentChanges(new[] { storey }, Ts);
        var graphlet = (IReadOnlyList<EntityData>)GraphletExtractor.WalkNew(db, owner, before, after, Ts);
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

    /// <summary>The stored rule copied exactly the pushout — nothing more.</summary>
    private async Task AssertRuleCopies(long seq, int deletes, int inserts)
    {
        await using var session = _driver!.AsyncSession();
        var row = (await (await session.RunAsync(@"
MATCH (r:Rule {target_ts: $t, seq: $seq})
OPTIONAL MATCH (r)-[:DELETES]->(l) WITH r, count(l) AS l
OPTIONAL MATCH (r)-[:INSERTS]->(i) RETURN l, count(i) AS i, r.aligned AS aligned, size(r.renumber_from) AS kept",
            new { t = Ts, seq })).ToListAsync()).Single();
        Assert.Equal((deletes, inserts, true), (row["l"].As<int>(), row["i"].As<int>(), row["aligned"].As<bool>()));
        Assert.True(row["kept"].As<int>() >= 4, "the interface (wall, rel, pset, IsExternal) must be renumbered, not copied");
    }

    /// <summary>After an aligned apply the kept nodes carry the mirror's current p21s.</summary>
    private async Task AssertInterfaceFollowsTheMirror(DatabaseIfc db)
    {
        var wall = db.OfType<IfcWall>().Single(w => w.GlobalId == Gid && w.ContainedInStructure is not null);
        var pset = (IfcPropertySet)wall.IsDefinedBy.Single().RelatingPropertyDefinition.Single();
        await using var session = _driver!.AsyncSession();
        var rows = await (await session.RunAsync(
            "MATCH (n {timestamp: $ts}) WHERE n.GlobalId IN $gids RETURN n.GlobalId AS g, n.p21_id AS p",
            new { ts = Ts, gids = new[] { wall.GlobalId, pset.GlobalId } })).ToListAsync();
        var byGid = rows.ToDictionary(r => r["g"].As<string>(), r => r["p"].As<string>());
        Assert.Equal($"#{wall.StepId}", byGid[wall.GlobalId]);
        Assert.Equal($"#{pset.StepId}", byGid[pset.GlobalId]);
    }

    // ── signature: structure + non-identity values (same idea as PingPongTests) ──

    private static readonly HashSet<string> MaskedKeys = new(StringComparer.Ordinal)
    {
        "p21_id", "timestamp", "revit_element_id",
    };

    private sealed record Sig(Dictionary<string, int> Rows)
    {
        public string Describe() =>
            $"{Rows.Where(r => r.Key.StartsWith("node|")).Sum(r => r.Value)} nodes / " +
            $"{Rows.Where(r => r.Key.StartsWith("edge|")).Sum(r => r.Value)} edges / " +
            $"{Rows.Count(r => r.Key.StartsWith("value|"))} distinct value rows";
    }

    private async Task<Sig> Signature()
    {
        await using var session = _driver!.AsyncSession();
        var rows = new Dictionary<string, int>();
        void Add(string row) => rows[row] = rows.GetValueOrDefault(row) + 1;

        foreach (var r in await (await session.RunAsync(
            "MATCH (n {timestamp: $ts}) RETURN n.EntityType AS t, properties(n) AS p", new { ts = Ts })).ToListAsync())
        {
            var t = r["t"].As<string>();
            Add($"node|{t}");
            foreach (var (key, value) in r["p"].As<IDictionary<string, object>>())
                if (!MaskedKeys.Contains(key)) Add($"value|{t}|{key}={value}");
        }
        foreach (var r in await (await session.RunAsync(
            @"MATCH (a {timestamp: $ts})-[e:rel]->(b {timestamp: $ts})
              RETURN a.EntityType + '|' + e.rel_type + '|' + toString(e.list_index) + '|' + b.EntityType AS k",
            new { ts = Ts })).ToListAsync())
            Add($"edge|{r["k"].As<string>()}");
        return new Sig(rows);
    }

    private void AssertSame(Sig expected, Sig actual, string step)
    {
        var drift = expected.Rows.Keys.Union(actual.Rows.Keys)
            .Select(k => (k, e: expected.Rows.GetValueOrDefault(k), a: actual.Rows.GetValueOrDefault(k)))
            .Where(d => d.e != d.a).ToList();
        foreach (var (k, e, a) in drift) _output.WriteLine($"  drift '{k}': expected {e}, got {a}");
        if (drift.Count > 0) Assert.Fail($"{step}: {drift.Count} drifted rows (see output)");
    }
}
