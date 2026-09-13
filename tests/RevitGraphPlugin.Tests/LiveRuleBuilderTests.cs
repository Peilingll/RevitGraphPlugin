using GeometryGym.Ifc;
using RevitGraphPlugin.Cypher;
using RevitGraphPlugin.Ifc;
using Xunit;

namespace RevitGraphPlugin.Tests;

/// <summary><see cref="LiveRuleBuilder"/> with real ggifc entities, following the session flow: watermark → convert → build rule.</summary>
public class LiveRuleBuilderTests
{
    private static (DatabaseIfc db, IfcBuildingStorey storey) NewStorey()
    {
        var db = new DatabaseIfc(false, ReleaseVersion.IFC4);
        var building = new IfcBuilding(db, "B");
        var storey = new IfcBuildingStorey(building, "S", 0);
        return (db, storey);
    }

    /// <summary>Registry-style tagging (shared types skipped) around a "conversion".</summary>
    private static (int before, int after) Convert(
        DatabaseIfc db, Dictionary<int, long> owner, long eid, Func<IfcElement> create)
    {
        var before = StepIdWatermark.Current(db);
        create();
        var after = StepIdWatermark.Current(db);
        for (var id = before + 1; id <= after; id++)
        {
            if (db[id] is { } e && !GraphRule.SharedResourceTypes.Contains(e.GetType().Name))
                owner[id] = eid;
        }
        return (before, after);
    }

    [Fact]
    public void BuildUpsert_insert_carries_graphlet_and_containment_refresh()
    {
        var (db, storey) = NewStorey();
        var owner = new Dictionary<int, long>();
        Convert(db, owner, 101, () => new IfcWall(storey, null, null));
        var (before, after) = Convert(db, owner, 102, () => new IfcWall(storey, null, null));

        var rule = LiveRuleBuilder.BuildUpsert(
            RuleOp.Insert, db, owner, new[] { storey }, 102, before, after, "t");

        Assert.Equal(RuleOp.Insert, rule.Op);
        Assert.Equal(102, rule.RevitElementId);
        // Graphlet: only wall2's new entities, each tagged 102.
        Assert.Contains(rule.Graphlet, d => d.EntityType == nameof(IfcWall));
        Assert.All(rule.Graphlet, d => Assert.Equal(102L, d.Properties["revit_element_id"]));
        // Containment refresh lists both members, renumbered 0..1.
        var rel = Assert.Single(rule.SharedRefresh);
        Assert.Equal(
            new[] { 0, 1 },
            rel.Edges.Where(e => e.RelType == "RelatedElements")
               .OrderBy(e => e.ListIndex).Select(e => e.ListIndex));
        Assert.Empty(rule.SharedDelete);
    }

    [Fact]
    public void BuildUpsert_rejects_remove_op()
    {
        var (db, storey) = NewStorey();
        Assert.Throws<ArgumentException>(() => LiveRuleBuilder.BuildUpsert(
            RuleOp.Remove, db, new Dictionary<int, long>(), new[] { storey }, 1, 0, 0, "t"));
    }

    [Fact]
    public void Remove_flow_detach_forget_build()
    {
        var (db, storey) = NewStorey();
        var owner = new Dictionary<int, long>();
        IfcWall? wall1 = null;
        Convert(db, owner, 101, () => wall1 = new IfcWall(storey, null, null));
        Convert(db, owner, 102, () => new IfcWall(storey, null, null));
        var wall1Ids = owner.Where(kv => kv.Value == 101).Select(kv => kv.Key).ToList();
        Assert.NotEmpty(wall1Ids);

        // Session's remove sequence: ggifc detach → forget ownership → build rule.
        LiveRuleBuilder.DetachFromContainment(wall1!);
        LiveRuleBuilder.ForgetOwnership(owner, 101);
        var rule = LiveRuleBuilder.BuildRemove(new[] { storey }, 101, "t");

        Assert.Equal(RuleOp.Remove, rule.Op);
        Assert.Empty(rule.Graphlet);
        // wall2 renumbered to 0; nothing to delete (rel still has a member).
        var rel = Assert.Single(rule.SharedRefresh);
        var member = Assert.Single(rel.Edges, e => e.RelType == "RelatedElements");
        Assert.Equal(0, member.ListIndex);
        Assert.Empty(rule.SharedDelete);
        // Ownership forgotten for 101 only.
        Assert.DoesNotContain(owner, kv => kv.Value == 101);
        Assert.Contains(owner, kv => kv.Value == 102);
    }

    [Fact]
    public void Removing_last_element_routes_rel_to_SharedDelete()
    {
        var (db, storey) = NewStorey();
        var owner = new Dictionary<int, long>();
        IfcWall? wall = null;
        Convert(db, owner, 101, () => wall = new IfcWall(storey, null, null));
        var relId = storey.ContainsElements.Single().StepId;

        LiveRuleBuilder.DetachFromContainment(wall!);
        LiveRuleBuilder.ForgetOwnership(owner, 101);
        var rule = LiveRuleBuilder.BuildRemove(new[] { storey }, 101, "t");

        Assert.Empty(rule.SharedRefresh);
        Assert.Equal(new[] { $"#{relId}" }, rule.SharedDelete);
    }

    [Fact]
    public void Replace_flow_produces_fresh_graphlet_with_same_owner()
    {
        var (db, storey) = NewStorey();
        var owner = new Dictionary<int, long>();
        IfcWall? wall = null;
        Convert(db, owner, 101, () => wall = new IfcWall(storey, null, null));
        var oldIds = owner.Keys.ToHashSet();

        // Session's modify sequence: detach + forget, then re-convert same element id.
        LiveRuleBuilder.DetachFromContainment(wall!);
        LiveRuleBuilder.ForgetOwnership(owner, 101);
        var (before, after) = Convert(db, owner, 101, () => new IfcWall(storey, null, null));

        var rule = LiveRuleBuilder.BuildUpsert(
            RuleOp.Replace, db, owner, new[] { storey }, 101, before, after, "t");

        Assert.Equal(RuleOp.Replace, rule.Op);
        // New graphlet: fresh StepIds (no overlap with the superseded conversion).
        Assert.All(rule.Graphlet, d => Assert.DoesNotContain(d.P21, oldIds));
        Assert.All(rule.Graphlet, d => Assert.Equal(101L, d.Properties["revit_element_id"]));
        // The re-added wall is the containment's only member, at index 0.
        var rel = Assert.Single(rule.SharedRefresh);
        var member = Assert.Single(rel.Edges, e => e.RelType == "RelatedElements");
        Assert.Equal(0, member.ListIndex);
    }

    // ── levels: the building's aggregation rel is shared like containment ──────────

    private static (DatabaseIfc db, IfcBuilding building) NewBuilding()
    {
        var db = new DatabaseIfc(false, ReleaseVersion.IFC4);
        return (db, new IfcBuilding(db, "B"));
    }

    /// <summary>A live level insert: the storey and its own pset are owned by the level id.</summary>
    private static IfcBuildingStorey AddLevel(DatabaseIfc db, IfcBuilding building, Dictionary<int, long> owner, long levelId, string name)
    {
        var before = StepIdWatermark.Current(db);
        var storey = new IfcBuildingStorey(building, name, 0) { GlobalId = $"0Level{levelId}0000000000000"[..22] };
        StableIds.StampAggregates(storey);
        StableIds.AttachPset(storey, "Pset_BuildingStoreyCommon",
            new IfcPropertySingleValue(db, "AboveGround", new IfcLogical(IfcLogicalEnum.UNKNOWN)));
        var after = StepIdWatermark.Current(db);
        for (var id = before + 1; id <= after; id++)
            if (db[id] is { } e && !GraphRule.SharedResourceTypes.Contains(e.GetType().Name))
                owner[id] = levelId;
        return storey;
    }

    [Fact]
    public void Level_insert_refreshes_the_building_aggregation_and_never_owns_it()
    {
        var (db, building) = NewBuilding();
        var owner = new Dictionary<int, long>();
        var s1 = AddLevel(db, building, owner, 201, "L0");
        var before = StepIdWatermark.Current(db);
        var s2 = AddLevel(db, building, owner, 202, "L1");
        var after = StepIdWatermark.Current(db);

        var rule = LiveRuleBuilder.BuildUpsert(
            RuleOp.Insert, db, owner, new[] { s1, s2 }, 202, before, after, "t", building);

        Assert.Contains(rule.Graphlet, d => d.EntityType == nameof(IfcBuildingStorey));
        Assert.Contains(rule.Graphlet, d => d.EntityType == nameof(IfcPropertySet));
        Assert.DoesNotContain(rule.Graphlet, d => d.EntityType == nameof(IfcRelAggregates));   // shared, not new
        Assert.All(rule.Graphlet, d => Assert.Equal(202L, d.Properties["revit_element_id"]));
        var agg = Assert.Single(rule.SharedRefresh, d => d.EntityType == nameof(IfcRelAggregates));
        Assert.Equal(new[] { 0, 1 },
            agg.Edges.Where(e => e.RelType == "RelatedObjects").OrderBy(e => e.ListIndex).Select(e => e.ListIndex));
        Assert.DoesNotContain(owner, kv => db[kv.Key] is IfcRelAggregates);
    }

    [Fact]
    public void Level_modify_rewalks_its_owned_entities_with_unchanged_p21s()
    {
        var (db, building) = NewBuilding();
        var owner = new Dictionary<int, long>();
        var storey = AddLevel(db, building, owner, 201, "L0");
        var ownedIds = owner.Keys.OrderBy(k => k).ToList();

        storey.Name = "Ground";
        storey.Elevation = 150;
        var rule = LiveRuleBuilder.BuildLevelModify(db, owner, new[] { storey }, building, 201, "t");

        Assert.Equal(RuleOp.Replace, rule.Op);
        Assert.Equal(ownedIds, rule.Graphlet.Select(d => d.P21).OrderBy(p => p));
        var walked = Assert.Single(rule.Graphlet, d => d.EntityType == nameof(IfcBuildingStorey));
        Assert.Equal("Ground", walked.Properties["Name"]);
        Assert.Equal(150.0, System.Convert.ToDouble(walked.Properties["Elevation"]));
    }

    [Fact]
    public void Level_remove_drops_the_last_aggregation_and_the_storey_containment()
    {
        var (db, building) = NewBuilding();
        var owner = new Dictionary<int, long>();
        var s1 = AddLevel(db, building, owner, 201, "L0");
        var s2 = AddLevel(db, building, owner, 202, "L1");
        Convert(db, owner, 101, () => new IfcWall(s2, null, null));
        var aggId = building.IsDecomposedBy.Single().StepId;
        var containmentId = s2.ContainsElements.Single().StepId;

        // Remove level 202: detach, forget, build (containment rel of s2 goes with it).
        var containment = LiveRuleBuilder.DetachStorey(s2);
        LiveRuleBuilder.ForgetOwnership(owner, 202);
        var rule = LiveRuleBuilder.BuildRemove(new[] { s1 }, 202, "t", building, containment);

        Assert.Equal(new[] { $"#{containmentId}" }, rule.SharedDelete);
        var agg = Assert.Single(rule.SharedRefresh, d => d.EntityType == nameof(IfcRelAggregates));
        Assert.Single(agg.Edges, e => e.RelType == "RelatedObjects");           // s1 only, renumbered 0

        // Removing the last storey drops the aggregation rel itself.
        LiveRuleBuilder.DetachStorey(s1);
        var last = LiveRuleBuilder.BuildRemove(Array.Empty<IfcBuildingStorey>(), 201, "t", building);
        Assert.Contains($"#{aggId}", last.SharedDelete);
        Assert.DoesNotContain(last.SharedRefresh, d => d.EntityType == nameof(IfcRelAggregates));
    }
}
