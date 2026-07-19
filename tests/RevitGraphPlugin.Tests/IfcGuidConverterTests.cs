using RevitGraphPlugin.Ifc;
using Xunit;

namespace RevitGraphPlugin.Tests;

/// <summary>
/// Guards the Revit UniqueId → IFC GlobalId conversion, in particular the uniqueness
/// property that a whole-model round-trip depends on (IfcRoot.UR1). Regression for the
/// collision found 2026-07-19: two walls in one document shared one GlobalId because
/// only the document-wide episode GUID prefix was used.
/// </summary>
public class IfcGuidConverterTests
{
    // Same 36-char episode GUID (shared across a document), different 8-hex element suffix.
    private const string Episode = "d4d3f8a1-9c2b-4e6f-8a7d-1b2c3d4e5f60";

    [Fact]
    public void Distinct_elements_sharing_the_episode_guid_get_distinct_GlobalIds()
    {
        var a = IfcGuidConverter.FromRevitUniqueId($"{Episode}-00001a2b");
        var b = IfcGuidConverter.FromRevitUniqueId($"{Episode}-00001a2c");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Same_UniqueId_is_stable_across_calls()
    {
        var uid = $"{Episode}-000abc12";
        Assert.Equal(
            IfcGuidConverter.FromRevitUniqueId(uid),
            IfcGuidConverter.FromRevitUniqueId(uid));
    }

    [Fact]
    public void Produces_a_valid_22_char_ifc_globalid()
    {
        var g = IfcGuidConverter.FromRevitUniqueId($"{Episode}-00001a2b");
        Assert.Equal(22, g.Length);
    }

    [Fact]
    public void Suffix_only_difference_still_diverges_across_many_elements()
    {
        // A run of consecutive element suffixes must all map to distinct GlobalIds.
        var ids = Enumerable.Range(0, 64)
            .Select(i => IfcGuidConverter.FromRevitUniqueId($"{Episode}-{i:x8}"))
            .ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }
}
