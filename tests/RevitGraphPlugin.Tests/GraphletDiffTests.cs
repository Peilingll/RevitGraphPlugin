using GeometryGym.Ifc;
using RevitGraphPlugin.Cypher;
using RevitGraphPlugin.Ifc;
using Xunit;

namespace RevitGraphPlugin.Tests;

/// <summary>
/// Rule persistence step 3a: GraphletDiff decides whether a Replace was really a
/// property-only Modify. Revit-free — both sides are built with ggifc and walked with
/// EntityWalker, the second build behind a StepId offset so every p21 differs (exactly
/// what a live re-conversion does). The L side normally comes back from Neo4j with
/// long-typed numerics; one test covers that normalization explicitly.
/// </summary>
public sealed class GraphletDiffTests
{
    private const string Ts = "diff-test";
    private const string WallGid = "1hRFML9_L7IO_zyzwdpriJ";   // stable: derives from UniqueId

    /// <summary>
    /// Build the graphlet of "the wall" — IfcWall + Pset_WallCommon(IsExternal) wired
    /// through IfcRelDefinesByProperties — and walk exactly its watermark range, the way
    /// the live path builds a rule's R side. <paramref name="prepad"/> shifts StepIds so
    /// two builds never share a p21. The wall keeps its stable GlobalId; the rel and pset
    /// get fresh random ggifc GlobalIds per build (the churn the diff must mask).
    /// </summary>
    private static List<EntityData> BuildWallGraphlet(
        int prepad, string wallName, bool isExternal, bool secondProperty = false)
    {
        var db = new DatabaseIfc(false, ReleaseVersion.IFC4);
        var building = new IfcBuilding(db, "B");
        var storey = new IfcBuildingStorey(building, "S", 0);
        for (var i = 0; i < prepad; i++) _ = new IfcCartesianPoint(db, i, 0, 0);

        var before = StepIdWatermark.Current(db);
        var wall = new IfcWall(storey, null, null) { GlobalId = WallGid, Name = wallName };
        var props = new List<IfcProperty>
        {
            new IfcPropertySingleValue(db, "IsExternal", new IfcBoolean(isExternal)),
        };
        if (secondProperty)
            props.Add(new IfcPropertySingleValue(db, "LoadBearing", new IfcBoolean(false)));
        _ = new IfcRelDefinesByProperties(wall, new IfcPropertySet("Pset_WallCommon", props));
        var after = StepIdWatermark.Current(db);

        var walked = new List<EntityData>();
        for (var id = before + 1; id <= after; id++)
            if (db[id] is { StepId: > 0 } entity)
                walked.Add(EntityWalker.Walk(entity, Ts));
        return walked;
    }

    private static GraphletCapture AsCapture(List<EntityData> nodes)
        => new(nodes, Array.Empty<EdgeData>());

    [Fact]
    public void Identical_rebuild_is_NoChange_despite_p21_and_rel_globalid_churn()
    {
        var l = BuildWallGraphlet(prepad: 0, "Wall-A", isExternal: true);
        var r = BuildWallGraphlet(prepad: 7, "Wall-A", isExternal: true);

        // The renumbering is real…
        Assert.NotEqual(l.Select(d => d.P21).ToHashSet(), r.Select(d => d.P21).ToHashSet());
        // …and so is the rel/pset GlobalId churn (only the wall's is stable).
        Assert.Single(l.Select(d => d.GlobalId).Intersect(r.Select(d => d.GlobalId)
            .Where(g => g is not null)));

        var outcome = GraphletDiff.Compare(AsCapture(l), r);
        Assert.Equal(GraphletDiffKind.NoChange, outcome.Kind);
        Assert.Empty(outcome.Changes);
    }

    [Fact]
    public void Property_value_change_is_PropertyOnly_anchored_on_the_wall()
    {
        var l = BuildWallGraphlet(0, "Wall-A", isExternal: true);
        var r = BuildWallGraphlet(3, "Wall-B", isExternal: true);

        var outcome = GraphletDiff.Compare(AsCapture(l), r);
        Assert.Equal(GraphletDiffKind.PropertyOnly, outcome.Kind);

        var change = Assert.Single(outcome.Changes);
        Assert.Equal("Name", change.Key);
        Assert.False(change.Inline);
        Assert.Equal("Wall-A", change.Before);
        Assert.Equal("Wall-B", change.After);
        // The wall IS the anchor: depth 0, its own stable GlobalId.
        Assert.Equal(ContextAnchorKind.Primary, change.Node.AnchorKind);
        Assert.Equal(WallGid, change.Node.AnchorGlobalId);
        Assert.Empty(change.Node.Steps);
    }

    [Fact]
    public void Inline_value_change_is_PropertyOnly_with_a_walkable_path()
    {
        // The IsExternal flip — the exact type-parameter-propagation scenario.
        var l = BuildWallGraphlet(0, "Wall-A", isExternal: true);
        var r = BuildWallGraphlet(5, "Wall-A", isExternal: false);

        var outcome = GraphletDiff.Compare(AsCapture(l), r);
        Assert.Equal(GraphletDiffKind.PropertyOnly, outcome.Kind);

        var change = Assert.Single(outcome.Changes);
        Assert.True(change.Inline);
        Assert.Equal("NominalValue", change.Key);
        Assert.Equal(true, change.Before);
        Assert.Equal(false, change.After);

        // The changed node (IfcPropertySingleValue) has no stable GlobalId of its own,
        // so its name must be a path — and one that re-parses.
        Assert.NotEmpty(change.Node.Steps);
        Assert.Equal("IfcPropertySingleValue", change.Node.Steps[^1].EntityType);
        Assert.True(ContextRef.TryParse(change.Node.Path, out var reparsed));
        Assert.Equal(change.Node, reparsed);
    }

    [Fact]
    public void Structural_difference_wins_over_any_property_change()
    {
        // R gained a second property — node added ⇒ Replace, even though Name changed too.
        var l = BuildWallGraphlet(0, "Wall-A", isExternal: true);
        var r = BuildWallGraphlet(0, "Wall-B", isExternal: true, secondProperty: true);

        Assert.Equal(GraphletDiffKind.Structural, GraphletDiff.Compare(AsCapture(l), r).Kind);
    }

    [Fact]
    public void External_edge_retarget_is_Structural()
    {
        var l = BuildWallGraphlet(0, "Wall-A", isExternal: true);
        var r = BuildWallGraphlet(0, "Wall-A", isExternal: true);

        // Point one of R's outgoing-glue edges (e.g. OwnerHistory) somewhere else:
        // same shape, different context wiring — must NOT pass as property-only.
        var victim = r.First(d => d.Edges.Any(e => r.All(x => x.P21 != e.TargetP21)));
        var glue = victim.Edges.First(e => r.All(x => x.P21 != e.TargetP21));
        victim.Edges[victim.Edges.IndexOf(glue)] = glue with { TargetP21 = glue.TargetP21 + 999 };

        Assert.Equal(GraphletDiffKind.Structural, GraphletDiff.Compare(AsCapture(l), r).Kind);
    }

    [Fact]
    public void Missing_product_anchor_is_Structural()
    {
        // A graphlet whose stable GlobalId disappeared on one side cannot be aligned.
        var l = BuildWallGraphlet(0, "Wall-A", isExternal: true);
        var r = BuildWallGraphlet(0, "Wall-A", isExternal: true);
        var wall = r.First(d => d.GlobalId == WallGid);
        r[r.IndexOf(wall)] = wall with { GlobalId = "0DIFFERENTGID0000000ID" };

        Assert.Equal(GraphletDiffKind.Structural, GraphletDiff.Compare(AsCapture(l), r).Kind);
    }

    [Fact]
    public void Neo4j_long_vs_walker_int_is_not_a_change()
    {
        var l = BuildWallGraphlet(0, "Wall-A", isExternal: true);
        var r = BuildWallGraphlet(2, "Wall-A", isExternal: true);

        // Simulate the Neo4j read-back: some numeric property comes home as long.
        foreach (var node in l)
            foreach (var key in node.Properties.Keys.ToList())
                if (node.Properties[key] is int i)
                    node.Properties[key] = (long)i;

        Assert.Equal(GraphletDiffKind.NoChange, GraphletDiff.Compare(AsCapture(l), r).Kind);
    }

    [Fact]
    public void Empty_or_size_mismatched_sides_are_Structural()
    {
        var r = BuildWallGraphlet(0, "Wall-A", isExternal: true);

        Assert.Equal(GraphletDiffKind.Structural,
            GraphletDiff.Compare(new GraphletCapture(
                Array.Empty<EntityData>(), Array.Empty<EdgeData>()), r).Kind);

        var l = BuildWallGraphlet(0, "Wall-A", isExternal: true);
        Assert.Equal(GraphletDiffKind.Structural,
            GraphletDiff.Compare(AsCapture(l), r.Take(r.Count - 1).ToList()).Kind);
    }
}
