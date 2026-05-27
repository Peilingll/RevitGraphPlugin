using System.Reflection;
using GeometryGym.Ifc;
using RevitGraphPlugin.Ifc;
using Xunit;
using Xunit.Abstractions;

namespace RevitGraphPlugin.Tests;

public class RevitOwnerHistoryTests
{
    private const string FullName = "Autodesk Revit 2025 (ENG)";
    private const string Author = "peiling.song";

    private readonly ITestOutputHelper _output;
    public RevitOwnerHistoryTests(ITestOutputHelper output) => _output = output;

    private static IfcProject NewProject()
    {
        var db = new DatabaseIfc(false, ReleaseVersion.IFC4);
        return new IfcProject(db, "TestProject");
    }

    private static RevitOwnerHistory.Source SampleSource(
        string? orgName = null,
        string? orgDescription = null) =>
        new(FullName, "2025", Author, orgName, orgDescription);

    [Fact]
    public void Person_GivenName_and_FamilyName_come_from_Author()
    {
        var project = NewProject();
        RevitOwnerHistory.Override(project, SampleSource());

        var person = project.OwnerHistory.OwningUser.ThePerson;
        Assert.Equal(Author, person.GivenName);
        Assert.Equal("", person.FamilyName);
    }

    [Fact]
    public void Application_FullName_Identifier_Version_match_Source()
    {
        var project = NewProject();
        RevitOwnerHistory.Override(project, SampleSource());

        var app = project.OwnerHistory.OwningApplication;
        Assert.Equal(FullName, app.ApplicationFullName);
        Assert.Equal("Revit", app.ApplicationIdentifier);
        Assert.Equal("2025", app.Version);
    }

    [Fact]
    public void Developer_Organization_Name_matches_ProductFullName()
    {
        var project = NewProject();
        RevitOwnerHistory.Override(project, SampleSource());

        var developer = project.OwnerHistory.OwningApplication.ApplicationDeveloper;
        Assert.Equal(FullName, developer.Name);
    }

    [Fact]
    public void User_Organization_Name_and_Description_use_Source_values()
    {
        var project = NewProject();
        RevitOwnerHistory.Override(project, SampleSource(orgName: "AcmeArchitects", orgDescription: "Test desc"));

        var userOrg = project.OwnerHistory.OwningUser.TheOrganization;
        Assert.Equal("AcmeArchitects", userOrg.Name);
        Assert.Equal("Test desc", userOrg.Description);
    }

    [Fact]
    public void User_Organization_is_blank_when_Source_values_are_null()
    {
        var project = NewProject();
        RevitOwnerHistory.Override(project, SampleSource());

        var userOrg = project.OwnerHistory.OwningUser.TheOrganization;
        Assert.True(
            string.IsNullOrEmpty(userOrg.Name),
            $"Expected user org Name to be null or empty after Override with null source, got '{userOrg.Name}'");
        Assert.True(
            string.IsNullOrEmpty(userOrg.Description),
            $"Expected user org Description to be null or empty, got '{userOrg.Description}'");
    }

    [Fact]
    public void OwnerHistory_ChangeAction_is_NOCHANGE()
    {
        var project = NewProject();
        RevitOwnerHistory.Override(project, SampleSource());

        Assert.Equal(IfcChangeActionEnum.NOCHANGE, project.OwnerHistory.ChangeAction);
    }

    [Fact]
    public void OwnerHistory_LastModified_fields_serialize_as_unset()
    {
        var project = NewProject();
        RevitOwnerHistory.Override(project, SampleSource());

        var oh = project.OwnerHistory;

        // Reference-type backing fields are cleared to null → STEP $.
        foreach (var name in new[] { "LastModifyingUser", "LastModifyingApplication" })
        {
            var field = FindBackingField(typeof(IfcOwnerHistory), name);
            Assert.NotNull(field);
            Assert.Null(field!.GetValue(oh));
        }

        // LastModifiedDate is non-nullable Int32; ggifc uses int.MinValue as the
        // IfcTimeStamp "unset" sentinel that serialises to STEP $.
        var lmdField = FindBackingField(typeof(IfcOwnerHistory), "LastModifiedDate");
        Assert.NotNull(lmdField);
        Assert.Equal(int.MinValue, lmdField!.GetValue(oh));

        // STEP serialisation: attrs 5/6/7 (LastModifiedDate / LastModifyingUser /
        // LastModifyingApplication) must all be $ — i.e. 3 consecutive $ between
        // .NOCHANGE. and the CreationDate integer.
        var step = oh.ToString();
        _output.WriteLine($"STEP: {step}");
        Assert.Contains(".NOCHANGE.,$,$,$,", step);
    }

    private static FieldInfo? FindBackingField(System.Type type, string propertyName)
    {
        var candidates = new[]
        {
            "m" + propertyName,
            "_" + propertyName,
            char.ToLowerInvariant(propertyName[0]) + propertyName.Substring(1),
        };
        System.Type? t = type;
        while (t is not null)
        {
            foreach (var name in candidates)
            {
                var field = t.GetField(
                    name,
                    BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (field is not null) return field;
            }
            t = t.BaseType;
        }
        return null;
    }

    [Fact]
    public void OwnerHistory_State_path_is_logged_for_research_log()
    {
        var project = NewProject();
        RevitOwnerHistory.Override(project, SampleSource());

        var state = project.OwnerHistory.State;
        var cleared = RevitOwnerHistory.StateWasClearedToNull;
        _output.WriteLine($"State path: {(cleared ? "A (reflection cleared)" : "C (fallback)")}");
        _output.WriteLine($"State property reports: {state}");

        if (cleared)
        {
            // Option A succeeded. The backing field is null; the property may still expose
            // a non-null default enum value depending on ggifc's getter, which is fine.
        }
        else
        {
            // Option C: ggifc's default value remained.
            Assert.Equal(IfcStateEnum.NOTDEFINED, state);
        }
    }
}
