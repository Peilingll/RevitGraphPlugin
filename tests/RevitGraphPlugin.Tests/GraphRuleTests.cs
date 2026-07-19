using GeometryGym.Ifc;
using RevitGraphPlugin.Cypher;
using RevitGraphPlugin.Ifc;
using Xunit;

namespace RevitGraphPlugin.Tests;

/// <summary>
/// Unit tests (no Neo4j, no Revit) for the incremental-sync building blocks:
/// the ggifc shared-containment-rel premise behind 問題 A, watermark-based graphlet
/// extraction, and containment-rel walking with list_index renumbering.
/// </summary>
public class GraphRuleTests
{
    private static DatabaseIfc NewDb() => new(false, ReleaseVersion.IFC4);

    private static (DatabaseIfc db, IfcBuildingStorey storey) NewStorey()
    {
        var db = NewDb();
        var building = new IfcBuilding(db, "B");
        var storey = new IfcBuildingStorey(building, "S", 0);
        return (db, storey);
    }

    /// <summary>Mimics ElementConverterRegistry.ConvertOne's tagging (incl. shared-type skip).</summary>
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

    // The premise of 問題 A, verified against real ggifc: elements on the same storey
    // share ONE containment rel, created during the FIRST element's conversion.
    [Fact]
    public void ggifc_shares_one_containment_rel_per_storey()
    {
        var (db, storey) = NewStorey();
        var w1 = new IfcWall(storey, null, null);
        var w2 = new IfcWall(storey, null, null);

        var rel = Assert.Single(storey.ContainsElements);
        Assert.Equal(2, rel.RelatedElements.Count);
        Assert.Contains(w1, rel.RelatedElements);
        Assert.Contains(w2, rel.RelatedElements);
    }

    [Fact]
    public void Registry_style_tagging_skips_shared_containment_rel()
    {
        var (db, storey) = NewStorey();

        var before = StepIdWatermark.Current(db);
        _ = new IfcWall(storey, null, null);   // first element → ggifc creates the rel here
        var after = StepIdWatermark.Current(db);

        var map = new Dictionary<int, long>();
        TagRange(db, before, after, 101, map);

        var rel = Assert.Single(storey.ContainsElements);
        Assert.False(map.ContainsKey(rel.StepId));       // shared: untagged
        Assert.True(map.Count > 0);                      // the wall itself is tagged
    }

    [Fact]
    public void NewEntities_filters_exactly_the_watermark_range()
    {
        var (db, storey) = NewStorey();
        _ = new IfcWall(storey, null, null);

        var before = StepIdWatermark.Current(db);
        var w2 = new IfcWall(storey, null, null);
        var after = StepIdWatermark.Current(db);

        var walked = CypherEmitter.WalkAll(db, "t");
        var fresh = GraphletExtractor.NewEntities(walked, before, after);

        Assert.Contains(fresh, d => d.P21 == w2.StepId);
        Assert.All(fresh, d => Assert.InRange(d.P21, before + 1, after));
        // wall1's entities and the (pre-existing) containment rel are not in the range.
        Assert.DoesNotContain(fresh, d => d.EntityType == nameof(IfcRelContainedInSpatialStructure));
    }

    [Fact]
    public void WalkStoreyContainmentRels_renumbers_after_ggifc_member_removal()
    {
        var (db, storey) = NewStorey();
        var w1 = new IfcWall(storey, null, null);
        var w2 = new IfcWall(storey, null, null);

        var (refresh, delete) = GraphletExtractor.StoreyContainmentChanges(new[] { storey }, "t");
        Assert.Empty(delete);
        var members = Assert.Single(refresh).Edges
            .Where(e => e.RelType == "RelatedElements").OrderBy(e => e.ListIndex).ToList();
        Assert.Equal(new[] { 0, 1 }, members.Select(m => m.ListIndex));
        Assert.Equal(new[] { w1.StepId, w2.StepId }, members.Select(m => m.TargetP21));

        // Detach w1 in ggifc (what live Remove must do), re-walk: w2 renumbers to 0.
        var rel = storey.ContainsElements.Single();
        rel.RelatedElements.Remove(w1);
        var (after, afterDelete) = GraphletExtractor.StoreyContainmentChanges(new[] { storey }, "t");
        Assert.Empty(afterDelete);
        var member = Assert.Single(Assert.Single(after).Edges, e => e.RelType == "RelatedElements");
        Assert.Equal(0, member.ListIndex);
        Assert.Equal(w2.StepId, member.TargetP21);

        // Detach the last member too: the rel is no longer walkable (ggifc refuses to
        // serialize a memberless rel) — it must surface as a deletion instead.
        rel.RelatedElements.Remove(w2);
        var (finalRefresh, finalDelete) = GraphletExtractor.StoreyContainmentChanges(new[] { storey }, "t");
        Assert.Empty(finalRefresh);
        Assert.Equal(new[] { $"#{rel.StepId}" }, finalDelete);
    }
}
