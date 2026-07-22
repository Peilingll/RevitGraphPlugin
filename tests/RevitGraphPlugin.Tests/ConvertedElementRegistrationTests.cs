using GeometryGym.Ifc;
using RevitGraphPlugin.Ifc.Converters;
using Xunit;

namespace RevitGraphPlugin.Tests;

/// <summary>
/// Guards the registry's principal-product lookup (ElementConverterRegistry.FindProduct),
/// the mechanism that populates ConvertedElements for EVERY converter. Regression for the
/// bug found 2026-07-20: only WallConverter registered its product, so a live modify of any
/// other element type fell back to Insert and duplicated the graphlet instead of replacing.
/// </summary>
public class ConvertedElementRegistrationTests
{
    private static (DatabaseIfc db, IfcBuildingStorey storey) NewStorey()
    {
        var db = new DatabaseIfc(false, ReleaseVersion.IFC4);
        var building = new IfcBuilding(db, "B");
        return (db, new IfcBuildingStorey(building, "S", 0));
    }

    [Fact]
    public void FindProduct_returns_the_element_with_matching_globalid()
    {
        var (db, storey) = NewStorey();
        var before = RevitGraphPlugin.Ifc.StepIdWatermark.Current(db);
        var column = new IfcColumn(storey, null, null);  // stands in for a converter's product
        var after = RevitGraphPlugin.Ifc.StepIdWatermark.Current(db);

        var found = ElementConverterRegistry.FindProduct(db, before, after, column.GlobalId);
        Assert.Same(column, found);
    }

    [Fact]
    public void FindProduct_ignores_non_matching_globalid_and_out_of_range_entities()
    {
        var (db, storey) = NewStorey();               // storey/building are before the range
        var before = RevitGraphPlugin.Ifc.StepIdWatermark.Current(db);
        var column = new IfcColumn(storey, null, null);
        var after = RevitGraphPlugin.Ifc.StepIdWatermark.Current(db);

        Assert.Null(ElementConverterRegistry.FindProduct(db, before, after, "000000000000000000000X"));
        // storey exists but is out of (before, after]
        Assert.Null(ElementConverterRegistry.FindProduct(db, before, after, storey.GlobalId));
    }

    [Fact]
    public void FindProduct_picks_the_product_not_its_placement_or_geometry()
    {
        // A converter creates the product plus placement/geometry entities; only the
        // IfcElement product carries the element's GlobalId.
        var (db, storey) = NewStorey();
        var before = RevitGraphPlugin.Ifc.StepIdWatermark.Current(db);
        var placement = new IfcLocalPlacement(storey.ObjectPlacement, db.Factory.XYPlanePlacement);
        var column = new IfcColumn(storey, placement, null);
        var after = RevitGraphPlugin.Ifc.StepIdWatermark.Current(db);

        var found = ElementConverterRegistry.FindProduct(db, before, after, column.GlobalId);
        Assert.IsType<IfcColumn>(found);
        Assert.Same(column, found);
    }
}
