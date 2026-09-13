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
    /// <summary>The GlobalId Revit itself exports for <paramref name="element"/> (its IfcGUID parameter), via <see cref="ExportUtils.GetExportId"/>. Use this in converters.</summary>
    public static string ForElement(Element element)
    {
        var guid = ExportUtils.GetExportId(element.Document, element.Id);
        return ParserIfc.EncodeGuid(guid);
    }

    /// <summary>
    /// Revit UniqueId ("&lt;episode GUID&gt;-&lt;8 hex&gt;") → IFC GlobalId, as Revit's exporter
    /// does it: XOR the element suffix into the GUID's last four bytes (the episode GUID is
    /// shared by every element in a document). For code without a Document (tests, tools).
    /// </summary>
    public static string FromRevitUniqueId(string revitUniqueId)
    {
        if (string.IsNullOrWhiteSpace(revitUniqueId))
            throw new ArgumentException("Revit UniqueId is null or empty.", nameof(revitUniqueId));

        var guidPart = revitUniqueId.Length >= 36
            ? revitUniqueId.Substring(0, 36)
            : revitUniqueId;
        var guid = Guid.Parse(guidPart);

        // Fold the element suffix into the GUID's trailing bytes.
        if (revitUniqueId.Length > 37)
        {
            var suffix = revitUniqueId.Substring(37);
            if (uint.TryParse(suffix, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
            {
                // Big-endian: the id's most significant byte lands in byte 12, the least in
                // byte 15 (Guid.ToByteArray keeps the trailing 8 bytes in string order).
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

    /// <summary>Deterministic IFC GlobalId from a seed string (synthetic entities: Site, Building, psets, rels — see <see cref="StableIds"/>).</summary>
    public static string FromSeed(string seed)
    {
        if (string.IsNullOrWhiteSpace(seed))
            throw new ArgumentException("Seed is null or empty.", nameof(seed));

        // MD5 → 16 bytes → Guid (stable, not security-sensitive).
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(seed));
        return ParserIfc.EncodeGuid(new Guid(hash));
    }
}
