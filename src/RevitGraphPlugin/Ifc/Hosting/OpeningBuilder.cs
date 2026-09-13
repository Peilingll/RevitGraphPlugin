using GeometryGym.Ifc;

namespace RevitGraphPlugin.Ifc.Hosting;

/// <summary>
/// The opening chain shared by windows and doors: cut an <see cref="IfcOpeningElement"/>
/// into the host wall (ggifc creates the IfcRelVoidsElement) and fill it with the element
/// (IfcRelFillsElement, created explicitly).
/// </summary>
public static class OpeningBuilder
{
    /// <summary>Cut an opening into <paramref name="host"/> and fill it with <paramref name="filler"/>; the opening's GlobalId is derived from <paramref name="globalIdSeed"/>.</summary>
    public static IfcOpeningElement VoidAndFill(
        IfcElement host,
        IfcElement filler,
        IfcObjectPlacement placement,
        IfcProductDefinitionShape? openingShape,
        string globalIdSeed)
    {
        // host as first arg → ggifc creates IfcRelVoidsElement.
        var opening = new IfcOpeningElement(host, placement, openingShape);
        opening.GlobalId = IfcGuidConverter.FromSeed(globalIdSeed);
        opening.Name = "Opening";

        // IfcRelFillsElement is not auto-created. Both rels get stable GlobalIds.
        _ = new IfcRelFillsElement(opening, filler) { GlobalId = StableIds.Seed(opening, "RelFills") };
        StableIds.StampVoids(opening);
        return opening;
    }
}
