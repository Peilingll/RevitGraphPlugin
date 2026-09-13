using RevitGraphPlugin.Ifc;
using Xunit;

namespace RevitGraphPlugin.Tests;

/// <summary>
/// Revit UniqueId → IFC GlobalId: distinct elements must get distinct ids (IfcRoot.UR1;
/// the episode GUID prefix alone is shared by the whole document).
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

    /// <summary>
    /// Pinned against a real Revit 2025 element (UniqueId from live.log, IfcGUID from its
    /// IFC parameters): the XOR is big-endian, most significant byte in GUID byte 12.
    /// </summary>
    [Fact]
    public void Matches_the_IfcGUID_revit_shows_for_a_real_element()
    {
        var uid = "37aa155a-26ad-4e1d-9d57-d9ca5d731856-0004cbde";
        Assert.Equal("0tgXLQ9grE7PrNsSfTTzE8", IfcGuidConverter.FromRevitUniqueId(uid));
    }

    [Fact]
    public void Element_id_is_folded_into_the_guid_tail_big_endian()
    {
        static byte[] Bytes(string suffix) =>
            GeometryGym.Ifc.ParserIfc.DecodeGlobalID(
                IfcGuidConverter.FromRevitUniqueId($"{Episode}-{suffix}")).ToByteArray();

        var zero = Bytes("00000000");
        Assert.Equal(zero[15] ^ 0x01, Bytes("00000001")[15]);   // least significant → byte 15
        Assert.Equal(zero[14] ^ 0x01, Bytes("00000100")[14]);
        Assert.Equal(zero[13] ^ 0x01, Bytes("00010000")[13]);
        Assert.Equal(zero[12] ^ 0x01, Bytes("01000000")[12]);   // most significant → byte 12
        Assert.Equal(zero[..12], Bytes("ffffffff")[..12]);       // the first 12 bytes never move
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
