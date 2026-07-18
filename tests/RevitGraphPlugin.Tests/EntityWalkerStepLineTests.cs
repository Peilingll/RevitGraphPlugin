using GeometryGym.Ifc;
using RevitGraphPlugin.Cypher;
using RevitGraphPlugin.Ifc;
using Xunit;
using Xunit.Abstractions;

namespace RevitGraphPlugin.Tests;

/// <summary>
/// End-to-end checks that <see cref="EntityWalker.Walk"/> sources node properties from
/// real ggifc <c>entity.ToString()</c> Part-21 output through <see cref="StepLineParser"/>.
/// Exercises the actual ggifc serialization (no Revit, no Neo4j) so a format mismatch
/// surfaces here rather than only during a full round-trip.
/// </summary>
public class EntityWalkerStepLineTests
{
    private readonly ITestOutputHelper _output;
    public EntityWalkerStepLineTests(ITestOutputHelper output) => _output = output;

    private static DatabaseIfc NewDb() => new(false, ReleaseVersion.IFC4);

    [Fact]
    public void CartesianPoint_coordinates_use_python_tuple_repr()
    {
        var pt = new IfcCartesianPoint(NewDb(), 0, 0, 0);
        var data = EntityWalker.Walk(pt, "t");

        _output.WriteLine($"STEP: {pt}");
        Assert.Equal("(0.0, 0.0, 0.0)", data.Properties["Coordinates"]);
    }

    [Fact]
    public void Direction_ratios_use_python_tuple_repr()
    {
        var dir = new IfcDirection(NewDb(), 0, 1, 0);
        var data = EntityWalker.Walk(dir, "t");

        _output.WriteLine($"STEP: {dir}");
        Assert.Equal("(0.0, 1.0, 0.0)", data.Properties["DirectionRatios"]);
    }

    [Fact]
    public void OwnerHistory_unset_and_enum_and_int_from_real_ggifc_serialization()
    {
        var db = NewDb();
        var project = new IfcProject(db, "TestProject");
        RevitOwnerHistory.Override(
            project, new RevitOwnerHistory.Source("Autodesk Revit 2025 (ENG)", "2025", "peiling.song", null, null));

        var oh = project.OwnerHistory;
        var data = EntityWalker.Walk(oh, "t");
        _output.WriteLine($"STEP: {oh}");

        // The reconstruction-crash guard: the unset LastModifiedDate must serialize as "$",
        // never as an int64 sentinel (that OverflowError killed graph_2_ifc).
        Assert.Equal("$", data.Properties["LastModifiedDate"]);
        Assert.Equal("$", data.Properties["State"]);              // ggifc default enum → unset
        Assert.Equal("$", data.Properties["LastModifyingUser"]);
        Assert.Equal("$", data.Properties["LastModifyingApplication"]);
        Assert.Equal("NOCHANGE", data.Properties["ChangeAction"]);
        Assert.IsType<long>(data.Properties["CreationDate"]);     // integer, not a string

        // OwningUser / OwningApplication are references → edges, never properties.
        Assert.False(data.Properties.ContainsKey("OwningUser"));
        Assert.False(data.Properties.ContainsKey("OwningApplication"));
        Assert.Contains(data.Edges, e => e.RelType == "OwningUser");
        Assert.Contains(data.Edges, e => e.RelType == "OwningApplication");
    }

    [Fact]
    public void SIUnit_derived_dimensions_becomes_dollar_enums_preserved()
    {
        var db = NewDb();
        var unit = new IfcSIUnit(db, IfcUnitEnum.LENGTHUNIT, IfcSIPrefix.MILLI, IfcSIUnitName.METRE);
        var data = EntityWalker.Walk(unit, "t");
        _output.WriteLine($"STEP: {unit}");

        Assert.Equal("$", data.Properties["Dimensions"]);        // derived * → "$"
        Assert.Equal("LENGTHUNIT", data.Properties["UnitType"]);
        Assert.Equal("MILLI", data.Properties["Prefix"]);
        Assert.Equal("METRE", data.Properties["Name"]);
    }
}
