using GeometryGym.Ifc;

namespace RevitGraphPlugin.Ifc;

/// <summary>
/// Deterministic GlobalIds for the IfcRoot entities that have no Revit element of their
/// own (psets, their rels, containment / aggregation, void / fill). ggifc would give them
/// a random id per conversion; the rule chain needs stable identifiers to match nodes
/// across versions (Esser 2022 §3.3, §5.2). Seed: <c>&lt;owner.GlobalId&gt;:&lt;role&gt;</c>.
/// </summary>
public static class StableIds
{
    /// <summary>GlobalId for a synthetic entity owned by <paramref name="owner"/> in <paramref name="role"/>.</summary>
    public static string Seed(IfcRoot owner, string role)
    {
        if (string.IsNullOrEmpty(owner.GlobalId))
            throw new InvalidOperationException(
                $"{owner.GetType().Name} has no GlobalId yet — set the owner's GlobalId before seeding '{role}' from it.");
        return IfcGuidConverter.FromSeed(owner.GlobalId + ":" + role);
    }

    /// <summary>Create a property set and attach it to <paramref name="owner"/>. Roles: <c>&lt;psetName&gt;</c>, <c>RelDefines:&lt;psetName&gt;</c>.</summary>
    public static IfcPropertySet AttachPset(IfcObjectDefinition owner, string psetName, params IfcProperty[] properties)
    {
        var pset = new IfcPropertySet(psetName, properties) { GlobalId = Seed(owner, psetName) };
        _ = new IfcRelDefinesByProperties(owner, pset) { GlobalId = Seed(owner, "RelDefines:" + psetName) };
        return pset;
    }

    /// <summary>Stamp the element's storey containment rel. Role: <c>ContainsElements</c>, seeded from the storey (idempotent). No-op if unhosted.</summary>
    public static void StampContainment(IfcElement element)
    {
        var rel = element.ContainedInStructure;
        if (rel?.RelatingStructure is { } structure)
            rel.GlobalId = Seed(structure, "ContainsElements");
    }

    /// <summary>Stamp the aggregation rel of <paramref name="child"/>. Role: <c>Aggregates</c>, seeded from the parent (idempotent). No-op if not aggregated.</summary>
    public static void StampAggregates(IfcObjectDefinition child)
    {
        var rel = child.Decomposes;
        if (rel?.RelatingObject is { } parent)
            rel.GlobalId = Seed(parent, "Aggregates");
    }

    /// <summary>Stamp the opening's void rel. Role: <c>RelVoids</c>, seeded from the opening. (The fill rel is seeded in OpeningBuilder: ggifc keeps no <c>HasFillings</c> inverse.)</summary>
    public static void StampVoids(IfcOpeningElement opening)
    {
        if (opening.VoidsElement is { } voids)
            voids.GlobalId = Seed(opening, "RelVoids");
    }
}
