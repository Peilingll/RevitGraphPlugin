using GeometryGym.Ifc;
using RevitGraphPlugin.Cypher;
using Xunit;

namespace RevitGraphPlugin.Tests;

public class EntityWalkerTests
{
    private const string Timestamp = "2026-05-19T00:00:00Z";

    [Fact]
    public void IfcPerson_has_no_convenience_Name_attribute()
    {
        var db = new DatabaseIfc(false, ReleaseVersion.IFC4);
        var person = new IfcPerson(db) { GivenName = "peiling.song" };

        var result = EntityWalker.Walk(person, Timestamp);

        Assert.DoesNotContain("Name", result.Properties.Keys);
        Assert.Contains("GivenName", result.Properties.Keys);
        Assert.Equal("peiling.song", result.Properties["GivenName"]);
    }

    [Fact]
    public void IfcPersonAndOrganization_has_no_convenience_Name_attribute()
    {
        // ggifc auto-creates the full OwnerHistory chain when an IfcProject is built;
        // pull IfcPersonAndOrganization from there rather than constructing one manually.
        var db = new DatabaseIfc(false, ReleaseVersion.IFC4);
        var project = new IfcProject(db, "TestProject");
        var pao = project.OwnerHistory.OwningUser;

        var result = EntityWalker.Walk(pao, Timestamp);

        Assert.DoesNotContain("Name", result.Properties.Keys);
        Assert.Contains("ThePerson", result.Edges.ConvertAll(e => e.RelType));
        Assert.Contains("TheOrganization", result.Edges.ConvertAll(e => e.RelType));
    }

    [Fact]
    public void IfcApplication_has_no_convenience_Name_attribute()
    {
        // Same approach as above: ggifc creates the application via IfcProject.
        var db = new DatabaseIfc(false, ReleaseVersion.IFC4);
        var project = new IfcProject(db, "TestProject");
        var app = project.OwnerHistory.OwningApplication;

        var result = EntityWalker.Walk(app, Timestamp);

        Assert.DoesNotContain("Name", result.Properties.Keys);
        Assert.Contains("ApplicationFullName", result.Properties.Keys);
        Assert.Contains("ApplicationIdentifier", result.Properties.Keys);
    }

    [Fact]
    public void IfcRoot_subclass_retains_Name_Description_GlobalId()
    {
        var db = new DatabaseIfc(false, ReleaseVersion.IFC4);
        var site = new IfcSite(db, "TestSite");

        var result = EntityWalker.Walk(site, Timestamp);

        Assert.Contains("GlobalId", result.Properties.Keys);
        // Name appears when ggifc set it on construction; Description may be null ("$")
        Assert.True(
            result.Properties.ContainsKey("Name"),
            $"Expected IfcSite to expose schema attribute 'Name'; got keys: {string.Join(",", result.Properties.Keys)}");
    }
}
