using GeometryGym.Ifc;

namespace RevitGraphPlugin.Ifc;

/// <summary>
/// Deterministic GlobalIds for every IfcRoot entity the plugin creates that has no Revit
/// element of its own: property sets, the relationships that attach them, spatial
/// containment / aggregation, and the void / fill relationships of openings.
///
/// Why: ggifc hands every IfcRoot not given a GlobalId explicitly a RANDOM one per
/// conversion (proven in GgifcIdentityTests). Esser 2022 §3.3 seeds its node matching on
/// "unique identifiers assigned to each primary node" and §5.2 names unstable identifiers
/// as the method's central limitation — so a pset whose GlobalId changes on every
/// re-conversion breaks exactly the precondition the rule chain relies on: a stored
/// property change is addressed by a path anchored on the pset's GlobalId, and after a
/// second re-conversion of the same element none of the recorded names resolve
/// (ConsecutiveModifyCheckoutTests). Seeding the id from the OWNER's GlobalId (itself
/// derived from the Revit UniqueId) plus a role makes every such node the same node
/// across conversions, sessions and machines — the same scheme BoilerplateBuilder
/// already uses for Site / Building and OpeningBuilder for openings.
///
/// Seed grammar: <c>&lt;owner.GlobalId&gt;:&lt;role&gt;</c>, hashed by
/// <see cref="IfcGuidConverter.FromSeed"/>. Roles are listed per method.
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

    /// <summary>
    /// Create a property set and attach it to <paramref name="owner"/>. Roles:
    /// pset = <c>&lt;psetName&gt;</c>, relationship = <c>RelDefines:&lt;psetName&gt;</c>.
    /// One pset per (owner, name) — the converters never attach the same Pset twice.
    /// </summary>
    public static IfcPropertySet AttachPset(IfcObjectDefinition owner, string psetName, params IfcProperty[] properties)
    {
        var pset = new IfcPropertySet(psetName, properties) { GlobalId = Seed(owner, psetName) };
        _ = new IfcRelDefinesByProperties(owner, pset) { GlobalId = Seed(owner, "RelDefines:" + psetName) };
        return pset;
    }

    /// <summary>
    /// Stamp the storey containment relationship ggifc auto-created when
    /// <paramref name="element"/> was constructed with a storey as host. Role:
    /// <c>ContainsElements</c>, seeded from the STOREY (one rel per storey, so every
    /// element placed in it stamps the same value — idempotent). No-op if unhosted.
    /// </summary>
    public static void StampContainment(IfcElement element)
    {
        var rel = element.ContainedInStructure;
        if (rel?.RelatingStructure is { } structure)
            rel.GlobalId = Seed(structure, "ContainsElements");
    }

    /// <summary>
    /// Stamp the aggregation relationship that decomposes <paramref name="child"/> into
    /// its parent (Site → Building → Storeys; ggifc auto-creates it from the constructor).
    /// Role: <c>Aggregates</c>, seeded from the PARENT — one rel per parent, idempotent
    /// across its children. No-op if the child is not aggregated.
    /// </summary>
    public static void StampAggregates(IfcObjectDefinition child)
    {
        var rel = child.Decomposes;
        if (rel?.RelatingObject is { } parent)
            rel.GlobalId = Seed(parent, "Aggregates");
    }

    /// <summary>
    /// Stamp the void relationship ggifc auto-created when the opening was constructed
    /// with its host as first argument. Role: <c>RelVoids</c>, seeded from the OPENING
    /// (whose own GlobalId OpeningBuilder already seeds from the hosted element). The
    /// fill relationship is created explicitly by OpeningBuilder and seeded there with
    /// role <c>RelFills</c> — ggifc does not maintain the <c>HasFillings</c> inverse, so
    /// it cannot be reached from the opening afterwards.
    /// </summary>
    public static void StampVoids(IfcOpeningElement opening)
    {
        if (opening.VoidsElement is { } voids)
            voids.GlobalId = Seed(opening, "RelVoids");
    }
}
