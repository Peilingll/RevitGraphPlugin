using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Autodesk.Revit.DB;
using GeometryGym.Ifc;

namespace RevitGraphPlugin.Ifc;

/// <summary>
/// IFC GlobalIds (22-char base64) for the plugin's entities: Revit elements get the
/// GUID Revit's own IFC exporter would give them, synthetic entities get a deterministic
/// one from a seed.
/// </summary>
public static class IfcGuidConverter
{
    /// <summary>
    /// The GlobalId Revit itself exports for <paramref name="element"/> — the value of
    /// its IfcGUID parameter — via the Revit API (<see cref="ExportUtils.GetExportId"/>),
    /// so the graph's product ids line up with a native export by construction rather
    /// than by re-implementing the exporter's UniqueId arithmetic. Use this in every
    /// converter; <see cref="FromRevitUniqueId"/> is the string-only re-implementation
    /// for code that has no Document (tests, tools).
    /// </summary>
    public static string ForElement(Element element)
    {
        var guid = ExportUtils.GetExportId(element.Document, element.Id);
        return ParserIfc.EncodeGuid(guid);
    }

    /// <summary>
    /// Convert a Revit Element.UniqueId ("xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx-yyyyyyyy")
    /// to an IFC GlobalId (22-char base64), mirroring Revit's own IFC exporter: the 36-char
    /// episode GUID prefix is SHARED across every element in a document, so the element-
    /// distinguishing 8-hex suffix must be XORed into the GUID's last four bytes — otherwise
    /// all elements collapse to one GlobalId (violates IfcRoot.UR1 "GlobalId shall be unique"
    /// the moment a model holds two of anything). Deterministic, so the GlobalId stays stable
    /// across syncs (required for ConMan2's GlobalId-based diff).
    /// </summary>
    public static string FromRevitUniqueId(string revitUniqueId)
    {
        if (string.IsNullOrWhiteSpace(revitUniqueId))
            throw new ArgumentException("Revit UniqueId is null or empty.", nameof(revitUniqueId));

        var guidPart = revitUniqueId.Length >= 36
            ? revitUniqueId.Substring(0, 36)
            : revitUniqueId;
        var guid = Guid.Parse(guidPart);

        // Fold the element-specific suffix (everything past the episode GUID and its
        // separating '-') into the GUID's trailing bytes so distinct elements differ.
        if (revitUniqueId.Length > 37)
        {
            var suffix = revitUniqueId.Substring(37);
            if (uint.TryParse(suffix, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
            {
                // Revit's exporter treats the GUID's last 8 hex characters as ONE
                // big-endian 32-bit integer and XORs the element id into it — so the
                // most significant byte of the id lands in byte 12, the least in byte 15
                // (Guid.ToByteArray keeps the trailing 8 bytes in string order). Verified
                // 2026-09-11 against the IfcGUID parameter Revit shows on a real wall.
                var bytes = guid.ToByteArray();
                bytes[12] ^= (byte)((value >> 24) & 0xFF);
                bytes[13] ^= (byte)((value >> 16) & 0xFF);
                bytes[14] ^= (byte)((value >> 8) & 0xFF);
                bytes[15] ^= (byte)(value & 0xFF);
                guid = new Guid(bytes);
            }
        }

        return ParserIfc.EncodeGuid(guid);
    }

    /// <summary>
    /// Deterministically derive an IFC GlobalId from an arbitrary seed string.
    /// Used for synthetic boilerplate entities (Site / Building) that have no Revit
    /// element of their own: seeding from the project UniqueId plus a role keeps the
    /// GlobalId STABLE across syncs (required for ConMan2's GlobalId-based run_diff)
    /// while staying unique per project.
    /// </summary>
    public static string FromSeed(string seed)
    {
        if (string.IsNullOrWhiteSpace(seed))
            throw new ArgumentException("Seed is null or empty.", nameof(seed));

        // MD5 → 16 bytes → a deterministic Guid (not security-sensitive, just stable).
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(seed));
        return ParserIfc.EncodeGuid(new Guid(hash));
    }
}
