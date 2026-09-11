using GeometryGym.Ifc;
using RevitGraphPlugin.Ifc;
using Xunit;

namespace RevitGraphPlugin.Tests;

/// <summary>
/// Evidence tests for the two identity instabilities the rule chain's portable naming
/// works around — Esser 2022 §5.2 names unstable identifiers as the method's central
/// domain limitation, and ggifc exhibits both flavors of it:
/// (1) every entity not handed a GlobalId explicitly gets a RANDOM one per conversion,
/// (2) StepIds (p21) are allocated monotonically, so re-converting the same element
///     yields an entirely fresh p21 range.
/// The four-name fallback in <c>PropertyChange</c> and the dual deletion in undo-Insert
/// exist because of exactly these two facts. If either test ever fails, that diagnosis
/// (and the deep-apply design choice built on it) must be revisited.
/// No Neo4j, no Revit — pure ggifc.
/// Since 2026-09-11 the converters no longer leave (1) to ggifc: every pset / rel /
/// containment / aggregation / void / fill entity is seeded through <c>StableIds</c>
/// (see StableIdsTests). This test keeps documenting what ggifc does on its own, which
/// is why the seeding is needed at all. (2) still holds and is the reason p21 stays a
/// masked column.
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

    /// <summary>
    /// Same code, two builds: the identity we control survives, every identity ggifc
    /// invents does not. Before StableIds the live pipeline only controlled the products'
    /// GlobalId (from the Revit UniqueId) while rel/pset/spatial nodes were ggifc-generated
    /// — so a re-conversion churned exactly the anchors ContextRef paths route through.
    /// </summary>
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

    /// <summary>
    /// The modify path (detach + re-convert the same element, as
    /// <c>LiveSyncSession.ApplyModified</c> does) renumbers every p21: ggifc allocates
    /// monotonically and never reuses, so the rebuilt graphlet shares no StepId with
    /// the one it replaces — even though it describes the same wall, unchanged.
    /// </summary>
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
