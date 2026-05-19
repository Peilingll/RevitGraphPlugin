using RevitGraphPlugin.Ifc;
using Xunit;

namespace RevitGraphPlugin.Tests;

public class Ifc4SchemaTests
{
    [Theory]
    [InlineData("IfcPerson", "GivenName")]
    [InlineData("IfcPerson", "FamilyName")]
    [InlineData("IfcPerson", "Identification")]
    [InlineData("IfcPersonAndOrganization", "ThePerson")]
    [InlineData("IfcApplication", "ApplicationFullName")]
    [InlineData("IfcWall", "Name")]
    [InlineData("IfcWall", "Description")]
    [InlineData("IfcWall", "GlobalId")]
    [InlineData("IfcSite", "RefLatitude")]
    public void Recognises_real_IFC4_attributes(string entityType, string attributeName)
    {
        Assert.True(Ifc4Schema.IsSchemaAttribute(entityType, attributeName));
    }

    [Theory]
    [InlineData("IfcPerson", "Name")]               // ggifc convenience getter
    [InlineData("IfcApplication", "Name")]          // ggifc convenience getter
    [InlineData("IfcPersonAndOrganization", "Name")] // ggifc convenience getter
    [InlineData("IfcWall", "TotallyFakeAttribute")]
    public void Rejects_attributes_absent_from_schema(string entityType, string attributeName)
    {
        Assert.False(Ifc4Schema.IsSchemaAttribute(entityType, attributeName));
    }

    [Fact]
    public void Unknown_entity_type_falls_back_to_true()
    {
        // ggifc may expose internal helper classes that have no IFC4 declaration.
        // The loader returns true rather than silently dropping their properties.
        Assert.True(Ifc4Schema.IsSchemaAttribute("NotAnIfcEntity", "WhateverProperty"));
    }
}
