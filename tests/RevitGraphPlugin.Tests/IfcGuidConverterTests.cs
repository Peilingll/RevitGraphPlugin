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

    /// <summary>
    /// Revit's exporter XORs the element id into the GUID's last 8 hex characters as ONE
    /// big-endian integer: the id's most significant byte lands in GUID byte 12, the
    /// least in byte 15. Found 2026-09-11 when a real wall's IfcGUID parameter agreed
    /// with ours on the first 16 characters and disagreed on the last 6 — the XOR
    /// difference between the two was byte-symmetric (EA C8 C8 EA), the signature of the
    /// same id folded in with the byte order reversed. Production converters now take the
    /// GUID from ExportUtils.GetExportId; this pins the string re-implementation.
    /// </summary>
    /// <summary>
    /// Pinned against Revit 2025 itself: wall 314334 of Project1, UniqueId read from
    /// live.log, IfcGUID read off the element's IFC Parameters — the same value
    /// ExportUtils.GetExportId produced in the graph. Keeps the string re-implementation
    /// honest for callers that have no Document.
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
