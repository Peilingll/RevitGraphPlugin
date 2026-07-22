using GeometryGym.Ifc;
using RevitGraphPlugin.Cypher;
using RevitGraphPlugin.Ifc;
using Xunit;

namespace RevitGraphPlugin.Tests;

/// <summary>
/// Verifies the ownership-tagging mechanics of step 1 (live incremental sync plan)
/// without Revit: the StepId-watermark assumption on ggifc's DatabaseIfc, and
/// <see cref="CypherEmitter.WalkAll"/> injecting <c>revit_element_id</c> only into
/// entities listed in the ownership map.
/// </summary>
public class OwnershipTaggingTests
{
    private static DatabaseIfc NewDb() => new(false, ReleaseVersion.IFC4);

    // The watermark pattern (ElementConverterRegistry.ConvertOne) relies on ggifc
    // allocating StepIds monotonically: everything created between two
    // StepIdWatermark.Current reads lies in the range (before, after].
    [Fact]
    public void Watermark_captures_exactly_the_entities_created_between_reads()
    {
        var db = NewDb();
        var project = new IfcProject(db, "P");           // "boilerplate": before watermark

        // Simulate one converter call between watermark reads.
        var before = StepIdWatermark.Current(db);
        var point = new IfcCartesianPoint(db, 1, 2, 3);
        var dir = new IfcDirection(db, 0, 0, 1);
        var after = StepIdWatermark.Current(db);

        // Both new entities fall inside (before, after]; the boilerplate does not.
        Assert.True(after > before);
        Assert.InRange(point.StepId, before + 1, after);
        Assert.InRange(dir.StepId, before + 1, after);
        Assert.True(project.StepId <= before);

        // The range contains no foreign entities: every existing id in it belongs
        // to what the "converter" just created (monotonic allocation, no interleaving).
        var created = new HashSet<int> { point.StepId, dir.StepId };
        for (var id = before + 1; id <= after; id++)
        {
            var entity = db[id];
            if (entity is not null)
                Assert.Contains(id, created);
        }
    }

    [Fact]
    public void WalkAll_injects_revit_element_id_only_for_owned_entities()
    {
        var db = NewDb();
        var project = new IfcProject(db, "P");            // shared: untagged
        var point = new IfcCartesianPoint(db, 1, 2, 3);   // owned by element 42
        var dir = new IfcDirection(db, 0, 0, 1);          // owned by element 42

        var owner = new Dictionary<int, long>
        {
            [point.StepId] = 42L,
            [dir.StepId] = 42L,
        };

        var all = CypherEmitter.WalkAll(db, "t", owner);

        var byId = all.ToDictionary(d => d.P21);
        Assert.Equal(42L, byId[point.StepId].Properties["revit_element_id"]);
        Assert.Equal(42L, byId[dir.StepId].Properties["revit_element_id"]);
        Assert.False(byId[project.StepId].Properties.ContainsKey("revit_element_id"));
    }

    [Fact]
    public void WalkAll_propagates_owner_to_inline_values_of_owned_entities()
    {
        var db = NewDb();
        var owned = new IfcPropertySingleValue(db, "IsExternal", new IfcBoolean(true));
        var shared = new IfcPropertySingleValue(db, "AboveGround", new IfcLogical(IfcLogicalEnum.UNKNOWN));

        var owner = new Dictionary<int, long> { [owned.StepId] = 42L };
        var all = CypherEmitter.WalkAll(db, "t", owner);
        var byId = all.ToDictionary(d => d.P21);

        // The owned parent's inline value inherits the element id; the shared one doesn't.
        var ownedInline = Assert.Single(byId[owned.StepId].Inlines);
        Assert.Equal(42L, ownedInline.OwnerElementId);

        var sharedInline = Assert.Single(byId[shared.StepId].Inlines);
        Assert.Null(sharedInline.OwnerElementId);
    }

    [Fact]
    public void WalkAll_without_map_adds_no_ownership_property()
    {
        var db = NewDb();
        _ = new IfcProject(db, "P");
        _ = new IfcCartesianPoint(db, 0, 0, 0);

        var all = CypherEmitter.WalkAll(db, "t");

        Assert.All(all, d => Assert.False(d.Properties.ContainsKey("revit_element_id")));
    }
}
