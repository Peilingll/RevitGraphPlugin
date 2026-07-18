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
    /// Convert a Revit Element.UniqueId (e.g. "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx-xxxxxxxx")
    /// to an IFC GlobalId (22-char base64) by taking the 36-char GUID prefix and encoding it
    /// via GeometryGym's ParserIfc.EncodeGuid.
    /// </summary>
    /// <remarks>
    /// Revit's own IFC exporter additionally XORs the element-id suffix into the lower bits of
    /// the GUID before encoding. We do NOT mirror that here — acceptable for entities whose
    /// element-id is fixed (e.g. ProjectInformation singleton). Revisit if cross-validation
    /// against Revit-IFC-export GlobalId is required for multi-instance elements.
    /// </remarks>
    public static string FromRevitUniqueId(string revitUniqueId)
    {
        if (string.IsNullOrWhiteSpace(revitUniqueId))
            throw new ArgumentException("Revit UniqueId is null or empty.", nameof(revitUniqueId));

        var guidPart = revitUniqueId.Length >= 36
            ? revitUniqueId.Substring(0, 36)
            : revitUniqueId;

        var guid = Guid.Parse(guidPart);
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
