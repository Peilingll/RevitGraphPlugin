using GeometryGym.Ifc;

namespace RevitGraphPlugin.Ifc.Hosting;

/// <summary>
/// Shared "hosted element" wiring for openings: cut an <see cref="IfcOpeningElement"/>
/// into a host (wall) and fill it with a filler (window / door). Reused by
/// WindowConverter and DoorConverter — the void/fill machinery is identical; only the
/// filler's IFC class and Pset differ.
///
/// ggifc auto-creates the <c>IfcRelVoidsElement</c> when the host is the opening's
/// first constructor argument (verified against ggifc 0.1.22: constructing
/// <c>new IfcOpeningElement(wall, …)</c> takes the void count 0 → 1, same pattern as
/// <c>IfcWall(storey,…)</c> → <c>IfcRelContainedInSpatialStructure</c>). Only the
/// <c>IfcRelFillsElement</c> must be created explicitly.
/// </summary>
public static class OpeningBuilder
{
    /// <summary>
    /// Cut an opening into <paramref name="host"/> and fill it with
    /// <paramref name="filler"/>. The opening's GlobalId is derived deterministically
    /// from <paramref name="globalIdSeed"/> (it is a synthetic element with no Revit
    /// counterpart, so a stable seed keeps the graph diff-friendly — same reasoning as
    /// the boilerplate Site/Building).
    /// </summary>
    public static IfcOpeningElement VoidAndFill(
        IfcElement host,
        IfcElement filler,
        IfcObjectPlacement placement,
        IfcProductDefinitionShape? openingShape,
        string globalIdSeed)
    {
        // host as first arg → ggifc wires IfcRelVoidsElement (host ──voids──> opening).
        var opening = new IfcOpeningElement(host, placement, openingShape);
        opening.GlobalId = IfcGuidConverter.FromSeed(globalIdSeed);
        opening.Name = "Opening";

        // opening ──fills──> filler (window / door). Not auto-created by ggifc. Both
        // rels get stable GlobalIds seeded from the opening (see StableIds).
        _ = new IfcRelFillsElement(opening, filler) { GlobalId = StableIds.Seed(opening, "RelFills") };
        StableIds.StampVoids(opening);
        return opening;
    }
}
