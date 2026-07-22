using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using GeometryGym.Ifc;

namespace RevitGraphPlugin.Ifc;

/// <summary>
/// Convert Revit Element UniqueId to IFC GlobalId (22-char base64).
/// </summary>
public static class IfcGuidConverter
{
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
                var bytes = guid.ToByteArray();
                bytes[12] ^= (byte)(value & 0xFF);
                bytes[13] ^= (byte)((value >> 8) & 0xFF);
                bytes[14] ^= (byte)((value >> 16) & 0xFF);
                bytes[15] ^= (byte)((value >> 24) & 0xFF);
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
