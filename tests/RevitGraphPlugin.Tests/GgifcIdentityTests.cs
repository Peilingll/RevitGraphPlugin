using GeometryGym.Ifc;
using RevitGraphPlugin.Ifc;
using Xunit;

namespace RevitGraphPlugin.Tests;

/// <summary>
/// Documents the two ggifc identity instabilities the design works around (Esser 2022
/// §5.2): (1) an IfcRoot not given a GlobalId gets a random one per conversion — hence
/// <c>StableIds</c>; (2) StepIds are never reused, so a re-conversion gets a fresh p21
/// range — hence p21 is a masked column. Pure ggifc.
/// </summary>
public sealed class GgifcIdentityTests
{
    private const string WallGid = "0IdentityWall00000001";

    private static (DatabaseIfc Db, IfcWall Wall, IfcRelContainedInSpatialStructure Rel,
                    IfcBuildingStorey Storey) Build()
    {
        var db = new DatabaseIfc(false, ReleaseVersion.IFC4);
        var building = new IfcBuilding(db, "B");
        var storey = new IfcBuildingStorey(building, "S", 0);
        var wall = new IfcWall(storey, null, null) { GlobalId = WallGid, Name = "W" };
        return (db, wall, storey.ContainsElements.Single(), storey);
    }

    /// <summary>Same code, two builds: an explicitly assigned GlobalId survives, a ggifc-generated one does not.</summary>
    [Fact]
    public void Generated_globalids_differ_between_identical_builds()
    {
        var a = Build();
        var b = Build();

        // Explicitly assigned (plugin-controlled) → stable across conversions.
        Assert.Equal(a.Wall.GlobalId, b.Wall.GlobalId);

        // ggifc-generated → fresh random value per conversion.
        Assert.NotEqual(a.Rel.GlobalId, b.Rel.GlobalId);
        Assert.NotEqual(a.Storey.GlobalId, b.Storey.GlobalId);
    }

    /// <summary>Detach + re-convert (the modify path) renumbers every p21: ggifc never reuses a StepId.</summary>
    [Fact]
    public void Reconversion_shares_no_stepid_with_the_original()
    {
        var (db, first, rel, storey) = Build();
        var w1 = StepIdWatermark.Current(db);

        // Detach + rebuild, byte-identical properties — the ApplyModified sequence.
        rel.RelatedElements.Remove(first);
        var second = new IfcWall(storey, null, null) { GlobalId = WallGid, Name = "W" };
        var w2 = StepIdWatermark.Current(db);

        Assert.InRange(first.StepId, 1, w1);           // old graphlet: at or below the mark
        Assert.InRange(second.StepId, w1 + 1, w2);     // rebuilt graphlet: strictly above it
        Assert.True(w2 > w1);                          // fresh ids were allocated at all
        Assert.NotEqual(first.StepId, second.StepId);  // same wall, different p21
    }
}
