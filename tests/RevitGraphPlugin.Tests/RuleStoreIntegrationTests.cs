using GeometryGym.Ifc;
using Neo4j.Driver;
using RevitGraphPlugin.Cypher;
using RevitGraphPlugin.Ifc;
using Xunit;
using Xunit.Abstractions;

namespace RevitGraphPlugin.Tests;

/// <summary>
/// Rule persistence step 3b: <see cref="RuleStore.PersistAsync"/>, running inside
/// <see cref="CypherEmitter.ApplyRuleAsync"/>'s transaction. Asserts the frozen schema
/// (op-dependent payload: copies for Insert/Remove/Replace, Change rows for Modify,
/// nothing for a provably-empty Replace) and the zero-pollution invariant: persisting
/// never adds a node to the current-state timestamp, and a re-baseline wipe never
/// removes one from the chain. Same local-Neo4j convention as ApplyRuleIntegrationTests.
/// </summary>
public sealed class RuleStoreIntegrationTests : IDisposable
{
    private const string TsLive = "test-rules-live";
    private const string WallGid = "3xRuleStoreWallGid0001";

    private readonly IDriver? _driver;
    private readonly ITestOutputHelper _output;

    public RuleStoreIntegrationTests(ITestOutputHelper output)
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

    private async Task<long> Count(string cypher, object? args = null)
    {
        await using var session = _driver!.AsyncSession();
        return (await (await session.RunAsync(cypher, args ?? new { })).ToListAsync())
            .Single()[0].As<long>();
    }

    private Task<long> NodesAt(string ts)
        => Count("MATCH (n {timestamp: $ts}) RETURN count(*)", new { ts });

    private Task<long> RuleCount()
        => Count("MATCH (r:Rule {target_ts: $ts}) RETURN count(*)", new { ts = TsLive });

    /// <summary>The full lifecycle: Insert → property-only Replace → no-op Replace → Remove.</summary>
    [Fact]
    public async Task Chain_stores_the_op_dependent_payload_and_never_touches_the_live_graph()
    {
        if (_driver is null) return;
        await Cleanup();

        var db = new DatabaseIfc(false, ReleaseVersion.IFC4);
        var building = new IfcBuilding(db, "B");
        var storey = new IfcBuildingStorey(building, "S", 0);
        var owner = new Dictionary<int, long>();

        // A baseline wall keeps the containment rel alive and EXTERNAL to the rules
        // under test — the second member's containment membership is genuine in-glue
        // (the first member carries the freshly created rel inside its own graphlet).
        var b0 = StepIdWatermark.Current(db);
        _ = new IfcWall(storey, null, null);
        var b1 = StepIdWatermark.Current(db);
        TagRange(db, b0, b1, 100, owner);
        await CypherEmitter.WriteAsync(_driver, db, TsLive, owner);

        // ── INSERT ────────────────────────────────────────────────────────────────
        var w0 = StepIdWatermark.Current(db);
        var wall = new IfcWall(storey, null, null) { GlobalId = WallGid, Name = "Wall-A" };
        var w1 = StepIdWatermark.Current(db);
        TagRange(db, w0, w1, 101, owner);

        var liveBefore = await NodesAt(TsLive);
        var (refresh, delete) = GraphletExtractor.StoreyContainmentChanges(new[] { storey }, TsLive);
        var graphlet = GraphletExtractor.WalkNew(db, owner, w0, w1, TsLive);
        var inserted = await CypherEmitter.ApplyRuleAsync(_driver, new GraphRule(
            RuleOp.Insert, 101, TsLive, graphlet, refresh, delete));

        Assert.NotNull(inserted.Stored);
        Assert.Equal(1, inserted.Stored!.Seq);
        Assert.Equal("Insert", inserted.Stored.Op);
        Assert.Equal($"{TsLive}-rule-1", inserted.Stored.RuleTimestamp);

        // R copies linked, counted, and in their own namespace; no L side.
        Assert.Equal(graphlet.Count, await Count(
            "MATCH (:Rule {timestamp: $ts})-[:INSERTS]->(n) RETURN count(n)",
            new { ts = inserted.Stored.RuleTimestamp }));
        Assert.Equal(0, await Count(
            "MATCH (:Rule {timestamp: $ts})-[:DELETES]->(n) RETURN count(n)",
            new { ts = inserted.Stored.RuleTimestamp }));

        // Glue: outgoing (owner history …) and incoming (containment membership), all
        // with a parseable portable context.
        var glueRows = await GlueContexts(inserted.Stored.RuleTimestamp);
        Assert.Contains(glueRows, g => g.Direction == "out");
        Assert.Contains(glueRows, g => g.Direction == "in" && g.RelType == "RelatedElements");
        Assert.All(glueRows, g => Assert.True(ContextRef.TryParse(g.Context, out _),
            $"unparseable glue context: {g.Context}"));

        // Persisting polluted nothing: the live graph grew by exactly the applied
        // graphlet (+ its inline children), same as before rule persistence existed.
        var liveAfterInsert = await NodesAt(TsLive);
        var inlineCount = graphlet.Sum(d => d.Inlines.Count);
        Assert.Equal(liveBefore + graphlet.Count + inlineCount, liveAfterInsert);

        // ── REPLACE, property-only: rename → stored as a Modify without copies ────
        var rel = storey.ContainsElements.Single();
        rel.RelatedElements.Remove(wall);
        var w2 = StepIdWatermark.Current(db);
        _ = new IfcWall(storey, null, null) { GlobalId = WallGid, Name = "Wall-B" };
        var w3 = StepIdWatermark.Current(db);
        TagRange(db, w2, w3, 101, owner);

        (refresh, delete) = GraphletExtractor.StoreyContainmentChanges(new[] { storey }, TsLive);
        var modified = await CypherEmitter.ApplyRuleAsync(_driver, new GraphRule(
            RuleOp.Replace, 101, TsLive,
            GraphletExtractor.WalkNew(db, owner, w2, w3, TsLive), refresh, delete));

        Assert.NotNull(modified.Stored);
        Assert.Equal(2, modified.Stored!.Seq);
        Assert.Equal("Modify", modified.Stored.Op);

        Assert.Equal(0, await Count(
            "MATCH (:Rule {timestamp: $ts})-[:INSERTS|DELETES]->(n) RETURN count(n)",
            new { ts = modified.Stored.RuleTimestamp }));
        await using (var session = _driver.AsyncSession())
        {
            var change = (await (await session.RunAsync(
                @"MATCH (:Rule {timestamp: $ts})-[:SETS]->(c:Change)
                  RETURN c.path AS path, c.key AS key, c.before AS before, c.after AS after,
                         c.inline AS inline",
                new { ts = modified.Stored.RuleTimestamp })).ToListAsync()).Single();
            Assert.Equal("Name", change["key"].As<string>());
            Assert.Equal("Wall-A", change["before"].As<string>());
            Assert.Equal("Wall-B", change["after"].As<string>());
            Assert.False(change["inline"].As<bool>());
            Assert.True(ContextRef.TryParse(change["path"].As<string>(), out var pathRef));
            Assert.Equal(WallGid, pathRef!.AnchorGlobalId);
        }

        // ── REPLACE, provably empty: identical rebuild → not stored at all ────────
        var wall2 = db[FindWallP21(db, w2, w3)] as IfcWall;
        rel.RelatedElements.Remove((IfcProduct)wall2!);
        var w4 = StepIdWatermark.Current(db);
        _ = new IfcWall(storey, null, null) { GlobalId = WallGid, Name = "Wall-B" };
        var w5 = StepIdWatermark.Current(db);
        TagRange(db, w4, w5, 101, owner);

        (refresh, delete) = GraphletExtractor.StoreyContainmentChanges(new[] { storey }, TsLive);
        var noop = await CypherEmitter.ApplyRuleAsync(_driver, new GraphRule(
            RuleOp.Replace, 101, TsLive,
            GraphletExtractor.WalkNew(db, owner, w4, w5, TsLive), refresh, delete));

        Assert.Null(noop.Stored);
        Assert.Equal(2, await RuleCount());

        // ── REMOVE: L side stored as DELETES copies ───────────────────────────────
        var wall3 = db[FindWallP21(db, w4, w5)] as IfcWall;
        rel.RelatedElements.Remove((IfcProduct)wall3!);
        (refresh, delete) = GraphletExtractor.StoreyContainmentChanges(new[] { storey }, TsLive);
        var removed = await CypherEmitter.ApplyRuleAsync(_driver, new GraphRule(
            RuleOp.Remove, 101, TsLive, Array.Empty<EntityData>(), refresh, delete));

        Assert.NotNull(removed.Stored);
        Assert.Equal(3, removed.Stored!.Seq);
        Assert.Equal("Remove", removed.Stored.Op);
        Assert.Equal(removed.BeforeGraphlet!.Nodes.Count, await Count(
            "MATCH (:Rule {timestamp: $ts})-[:DELETES]->(n) RETURN count(n)",
            new { ts = removed.Stored.RuleTimestamp }));

        // ── Re-baseline wipes the live graph, never the chain ─────────────────────
        await CypherEmitter.WriteAsync(_driver, db, TsLive, owner);
        Assert.Equal(3, await RuleCount());
    }

    private sealed record GlueRow(string Context, string RelType, string Direction);

    private async Task<List<GlueRow>> GlueContexts(string ruleTs)
    {
        await using var session = _driver!.AsyncSession();
        return (await (await session.RunAsync(
            @"MATCH (:Rule {timestamp: $ts})-[:GLUE]->(g:Glue)
              RETURN g.context AS c, g.rel_type AS r, g.direction AS d",
            new { ts = ruleTs })).ToListAsync())
            .Select(row => new GlueRow(
                row["c"].As<string>(), row["r"].As<string>(), row["d"].As<string>()))
            .ToList();
    }

    private static int FindWallP21(DatabaseIfc db, int before, int after)
    {
        for (var id = before + 1; id <= after; id++)
            if (db[id] is IfcWall w) return w.StepId;
        throw new InvalidOperationException("no wall in range");
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
