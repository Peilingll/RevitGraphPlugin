using RevitGraphPlugin.Cypher;
using Xunit;

namespace RevitGraphPlugin.Tests;

/// <summary>
/// Verifies the STEP-line parser and its Python-repr formatting against real lines from
/// data/samples/ifc/test/plugin_full_rebuild.ifc and the property values the temp-IFC
/// bridge stored in data/samples/cypher/01_one_wall_neo4j_query_table_data.json.
/// No Revit / Neo4j required.
/// </summary>
public class StepLineParserTests
{
    // Map a full STEP line to its ordered primitive properties by zipping the argument
    // tokens with the schema's declaration-ordered attribute names — the same pairing
    // 子步驟 3 will do inside EntityWalker. Slots the reflection layer owns (references,
    // typed inline values, aggregates thereof) are dropped.
    private static Dictionary<string, object> PrimitiveProps(string entityType, string stepLine)
    {
        var tokens = StepLineParser.ParseArguments(stepLine);
        var names = RevitGraphPlugin.Ifc.Ifc4Schema.GetOrderedAttributes(entityType);
        Assert.Equal(names.Count, tokens.Count);

        var props = new Dictionary<string, object>(StringComparer.Ordinal);
        for (var i = 0; i < names.Count; i++)
            if (StepLineParser.TryToPropertyValue(tokens[i], out var value))
                props[names[i]] = value;
        return props;
    }

    // ── PyRepr: match CPython repr(float) exactly ────────────────────────────────

    [Theory]
    [InlineData(0.0, "0.0")]
    [InlineData(1.0, "1.0")]
    [InlineData(-95.0, "-95.0")]
    [InlineData(4000.0, "4000.0")]
    [InlineData(874.99999, "874.99999")]
    [InlineData(4270.9925, "4270.9925")]
    [InlineData(-138.75, "-138.75")]
    [InlineData(93.74999999999994, "93.74999999999994")]
    [InlineData(1e-05, "1e-05")]
    [InlineData(-2e-05, "-2e-05")]
    [InlineData(6.123233995736766e-17, "6.123233995736766e-17")]
    [InlineData(1e16, "1e+16")]
    [InlineData(1e15, "1000000000000000.0")]
    [InlineData(0.0001, "0.0001")]
    public void PyRepr_matches_Python(double value, string expected) =>
        Assert.Equal(expected, StepLineParser.PyRepr(value));

    // ── Tokenizer basics ─────────────────────────────────────────────────────────

    [Fact]
    public void ParseLine_extracts_id_keyword_and_arity()
    {
        var line = StepLineParser.ParseLine("#326=IFCSIUNIT(*,.LENGTHUNIT.,.MILLI.,.METRE.);");
        Assert.Equal(326, line.Id);
        Assert.Equal("IFCSIUNIT", line.Keyword);
        Assert.Equal(4, line.Arguments.Count);
    }

    [Fact]
    public void Unset_and_derived_both_become_dollar()
    {
        // IfcSIUnit: Dimensions(*)  UnitType(.LENGTHUNIT.)  Prefix(.MILLI.)  Name(.METRE.)
        var p = PrimitiveProps("IfcSIUnit", "#326=IFCSIUNIT(*,.LENGTHUNIT.,.MILLI.,.METRE.);");
        Assert.Equal("$", p["Dimensions"]);        // derived *  → "$"
        Assert.Equal("LENGTHUNIT", p["UnitType"]); // enum
        Assert.Equal("MILLI", p["Prefix"]);
        Assert.Equal("METRE", p["Name"]);

        var q = PrimitiveProps("IfcSIUnit", "#327=IFCSIUNIT(*,.AREAUNIT.,$,.SQUARE_METRE.);");
        Assert.Equal("$", q["Prefix"]);            // unset $ → "$"
    }

    [Fact]
    public void Empty_string_stays_empty_not_dollar()
    {
        // IfcOrganization: Identification($) Name('Unknown') Description('') Roles($) Addresses($)
        var p = PrimitiveProps("IfcOrganization", "#321=IFCORGANIZATION($,'Unknown','',$,$);");
        Assert.Equal("$", p["Identification"]);
        Assert.Equal("Unknown", p["Name"]);
        Assert.Equal("", p["Description"]);        // '' → "" (NOT "$")
        Assert.Equal("$", p["Roles"]);
    }

    [Fact]
    public void OwnerHistory_unset_date_and_state_and_int_timestamp()
    {
        // The reconstruction crash source: LastModifiedDate must be "$", not an int64.
        var p = PrimitiveProps(
            "IfcOwnerHistory",
            "#325=IFCOWNERHISTORY(#322,#324,$,.NOCHANGE.,$,$,$,1782251423);");

        Assert.False(p.ContainsKey("OwningUser"));         // #322 → edge, skipped
        Assert.False(p.ContainsKey("OwningApplication"));  // #324 → edge, skipped
        Assert.Equal("$", p["State"]);
        Assert.Equal("NOCHANGE", p["ChangeAction"]);
        Assert.Equal("$", p["LastModifiedDate"]);          // was the OverflowError
        Assert.Equal("$", p["LastModifyingUser"]);
        Assert.Equal("$", p["LastModifyingApplication"]);
        Assert.Equal(1782251423L, p["CreationDate"]);      // integer, not string
    }

    [Fact]
    public void References_are_skipped_derived_context_slots_become_dollar()
    {
        // #334 Body context: 'Body','Model',*,*,*,*,#330,$,.MODEL_VIEW.,$
        var p = PrimitiveProps(
            "IfcGeometricRepresentationSubContext",
            "#334=IFCGEOMETRICREPRESENTATIONSUBCONTEXT('Body','Model',*,*,*,*,#330,$,.MODEL_VIEW.,$);");

        Assert.Equal("Body", p["ContextIdentifier"]);
        Assert.Equal("Model", p["ContextType"]);
        Assert.Equal("$", p["CoordinateSpaceDimension"]);  // *
        Assert.Equal("$", p["Precision"]);                 // *
        Assert.Equal("$", p["WorldCoordinateSystem"]);     // *
        Assert.Equal("$", p["TrueNorth"]);                 // *
        Assert.False(p.ContainsKey("ParentContext"));      // #330 → edge, skipped
        Assert.Equal("$", p["TargetScale"]);
        Assert.Equal("MODEL_VIEW", p["TargetView"]);
        Assert.Equal("$", p["UserDefinedTargetView"]);
    }

    // ── Primitive lists → Python str(tuple) ──────────────────────────────────────

    [Fact]
    public void CartesianPoint_real_list_uses_python_tuple_repr()
    {
        var p = PrimitiveProps("IfcCartesianPoint", "#331=IFCCARTESIANPOINT((0.,0.,0.));");
        Assert.Equal("(0.0, 0.0, 0.0)", p["Coordinates"]);
    }

    [Fact]
    public void Direction_two_element_real_list()
    {
        var p = PrimitiveProps("IfcDirection", "#333=IFCDIRECTION((0.,1.));");
        Assert.Equal("(0.0, 1.0)", p["DirectionRatios"]);
    }

    [Fact]
    public void Direction_scientific_notation_element()
    {
        var p = PrimitiveProps("IfcDirection", "#9=IFCDIRECTION((6.123233995736766E-17,1.));");
        Assert.Equal("(6.123233995736766e-17, 1.0)", p["DirectionRatios"]);
    }

    [Fact]
    public void PostalAddress_single_string_list_gets_trailing_comma()
    {
        // #344 AddressLines is the 5th attribute: ('Enter address here')
        var p = PrimitiveProps(
            "IfcPostalAddress",
            "#344=IFCPOSTALADDRESS($,$,$,$,('Enter address here'),$,'London','London',$,'United Kingdom');");
        Assert.Equal("('Enter address here',)", p["AddressLines"]);
        Assert.Equal("London", p["Town"]);
        Assert.Equal("United Kingdom", p["Country"]);
    }

    [Fact]
    public void Nested_real_list_coordlist()
    {
        var tokens = StepLineParser.ParseArguments("#84=IFCCARTESIANPOINTLIST3D(((1.,2.,3.),(4.,5.,6.)));");
        Assert.True(StepLineParser.TryToPropertyValue(tokens[0], out var value));
        Assert.Equal("((1.0, 2.0, 3.0), (4.0, 5.0, 6.0))", value);
    }

    [Fact]
    public void Integer_aggregate_stays_integer_repr()
    {
        // IfcCompoundPlaneAngleMeasure round-trips as an AGGREGATE OF INTEGER.
        var tokens = StepLineParser.ParseArguments("IFCDUMMY((0,-7,-37,-956022));");
        Assert.True(StepLineParser.TryToPropertyValue(tokens[0], out var value));
        Assert.Equal("(0, -7, -37, -956022)", value);
    }

    [Fact]
    public void Reference_and_typed_lists_are_skipped()
    {
        // Faces (#180,#181,…) → edges; PnIndex $. Only Closed/PnIndex-style primitives stay.
        var p = PrimitiveProps(
            "IfcPolygonalFaceSet", "#85=IFCPOLYGONALFACESET(#84,$,(#180,#181,#182),$);");
        Assert.False(p.ContainsKey("Coordinates")); // #84 ref → edge
        Assert.Equal("$", p["Closed"]);
        Assert.False(p.ContainsKey("Faces"));        // (#180,…) ref list → edges
        Assert.Equal("$", p["PnIndex"]);
    }

    [Fact]
    public void Typed_inline_value_is_skipped()
    {
        // IfcPropertySingleValue.NominalValue = IFCBOOLEAN(.T.) → inline node, not a property.
        var p = PrimitiveProps(
            "IfcPropertySingleValue", "#88=IFCPROPERTYSINGLEVALUE('IsExternal',$,IFCBOOLEAN(.T.),$);");
        Assert.Equal("IsExternal", p["Name"]);
        Assert.Equal("$", p["Description"]);
        Assert.False(p.ContainsKey("NominalValue")); // typed inline → skipped
        Assert.Equal("$", p["Unit"]);                // unset $ → "$"
    }

    [Fact]
    public void Backslash_escape_fails_loud()
    {
        Assert.Throws<NotSupportedException>(
            () => StepLineParser.ParseArguments(@"#1=IFCLABEL('a\X2\00E9\X0\b');"));
    }
}
