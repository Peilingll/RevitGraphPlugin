using GeometryGym.Ifc;
using RevitGraphPlugin.Ifc;
using RevitGraphPlugin.Ifc.Hosting;
using Xunit;

namespace RevitGraphPlugin.Tests;

/// <summary>
/// The converters' synthetic IfcRoot entities (pset, RelDefines, containment,
/// aggregation, voids / fills) must carry the SAME GlobalId every time the same owner is
/// converted — the precondition Esser 2022 §3.3 / §5.2 puts on node matching, and the
/// one <see cref="GgifcIdentityTests"/> shows ggifc does not meet on its own. Pure ggifc,
/// no Neo4j, no Revit: the Revit-derived owner GlobalIds are literals here.
/// </summary>
public sealed class StableIdsTests
{
    private const string WallGid = "0StableWall0000000001";
    private const string OtherWallGid = "0StableWall0000000002";
    private const string StoreyGid = "0StableStorey000000001";
    private const string BuildingGid = "0StableBuilding0000001";

    private static (DatabaseIfc Db, IfcBuildingStorey Storey, IfcBuilding Building) Site()
    {
        var db = new DatabaseIfc(false, ReleaseVersion.IFC4);
        var building = new IfcBuilding(db, "B") { GlobalId = BuildingGid };
        var storey = new IfcBuildingStorey(building, "S", 0) { GlobalId = StoreyGid };
        return (db, storey, building);
    }

    private static (IfcWall Wall, IfcPropertySet Pset, IfcRelDefinesByProperties Rel) ConvertWall(
        DatabaseIfc db, IfcBuildingStorey storey, string gid, bool isExternal)
    {
        var wall = new IfcWall(storey, null, null) { GlobalId = gid, Name = "W" };
        StableIds.StampContainment(wall);
        var pset = StableIds.AttachPset(wall, "Pset_WallCommon",
            new IfcPropertySingleValue(db, "IsExternal", new IfcBoolean(isExternal)));
        return (wall, pset, wall.IsDefinedBy.Single());
    }

    [Fact]
    public void Pset_and_rel_ids_survive_reconversion_even_when_the_value_changes()
    {
        var (db, storey, _) = Site();
        var first = ConvertWall(db, storey, WallGid, isExternal: true);

        // The ApplyModified sequence: detach, rebuild with a different value.
        storey.ContainsElements.Single().RelatedElements.Remove(first.Wall);
        var second = ConvertWall(db, storey, WallGid, isExternal: false);

        Assert.NotEqual(first.Pset.StepId, second.Pset.StepId);        // ggifc still renumbers…
        Assert.Equal(first.Pset.GlobalId, second.Pset.GlobalId);       // …but the identity holds
        Assert.Equal(first.Rel.GlobalId, second.Rel.GlobalId);
    }

    [Fact]
    public void Different_owners_get_different_ids_for_the_same_role()
    {
        var (db, storey, _) = Site();
        var a = ConvertWall(db, storey, WallGid, true);
        var b = ConvertWall(db, storey, OtherWallGid, true);

        Assert.NotEqual(a.Pset.GlobalId, b.Pset.GlobalId);
        Assert.NotEqual(a.Rel.GlobalId, b.Rel.GlobalId);
        Assert.NotEqual(a.Pset.GlobalId, a.Rel.GlobalId);              // roles differ too
    }

    [Fact]
    public void Containment_and_aggregation_are_seeded_from_the_parent()
    {
        var (db, storey, building) = Site();
        StableIds.StampAggregates(storey);
        var a = ConvertWall(db, storey, WallGid, true);
        var b = ConvertWall(db, storey, OtherWallGid, true);

        var containment = storey.ContainsElements.Single();
        Assert.Same(containment, a.Wall.ContainedInStructure);
        Assert.Same(containment, b.Wall.ContainedInStructure);
        Assert.Equal(StableIds.Seed(storey, "ContainsElements"), containment.GlobalId);
        Assert.Equal(StableIds.Seed(building, "Aggregates"), storey.Decomposes.GlobalId);

        // A second database with the same Revit-derived ids reproduces every value.
        var (db2, storey2, building2) = Site();
        StableIds.StampAggregates(storey2);
        _ = ConvertWall(db2, storey2, WallGid, true);
        Assert.Equal(containment.GlobalId, storey2.ContainsElements.Single().GlobalId);
        Assert.Equal(storey.Decomposes.GlobalId, storey2.Decomposes.GlobalId);
    }

    [Fact]
    public void Opening_voids_and_fills_are_seeded_from_the_opening()
    {
        static (IfcOpeningElement Opening, IfcRelFillsElement Fills) Build()
        {
            var (db, storey, _) = Site();
            var host = ConvertWall(db, storey, WallGid, true).Wall;
            var window = new IfcWindow(storey, null, null) { GlobalId = OtherWallGid };
            var placement = new IfcLocalPlacement(storey.ObjectPlacement,
                new IfcAxis2Placement3D(new IfcCartesianPoint(db, 0, 0, 0)));
            var opening = OpeningBuilder.VoidAndFill(host, window, placement, null, OtherWallGid + ":Opening");
            // ggifc keeps no HasFillings inverse — find the rel through the database.
            var fills = db.OfType<IfcRelFillsElement>().Single(r => r.RelatingOpeningElement == opening);
            return (opening, fills);
        }

        var a = Build();
        var b = Build();

        Assert.Equal(a.Opening.GlobalId, b.Opening.GlobalId);
        Assert.Equal(a.Opening.VoidsElement.GlobalId, b.Opening.VoidsElement.GlobalId);
        Assert.Equal(a.Fills.GlobalId, b.Fills.GlobalId);
        Assert.Equal(StableIds.Seed(a.Opening, "RelVoids"), a.Opening.VoidsElement.GlobalId);
        Assert.Equal(StableIds.Seed(a.Opening, "RelFills"), a.Fills.GlobalId);
        Assert.NotEqual(a.Opening.VoidsElement.GlobalId, a.Fills.GlobalId);
    }
}
