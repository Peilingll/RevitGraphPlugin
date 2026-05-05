using Autodesk.Revit.DB;
using GeometryGym.Ifc;

namespace RevitGraphPlugin.Conversion;

/// <summary>
/// Converts a Revit window <see cref="FamilyInstance"/> into an
/// <see cref="IfcWindow"/>, synthesises an <see cref="IfcOpeningElement"/>
/// in the host wall, and wires up the two relationship objects
/// (<see cref="IfcRelVoidsElement"/> via the opening's host argument,
/// <see cref="IfcRelFillsElement"/> created explicitly because GG.Ifc does
/// not auto-create it for windows — see Stage 2 research log).
///
/// Stage 3 simplification: opening geometry is a copy of the window's
/// largest solid. A real opening would be a wall-thickness-aligned box
/// derived from the window's section. Sufficient for design.md §4 Stage 5
/// verification because the relationships, not the volume, are the test.
/// </summary>
internal static class WindowConverter
{
    public static IfcWindow? Convert(
        FamilyInstance windowInstance,
        DatabaseIfc db,
        IfcWall hostIfcWall)
    {
        var solid = GeometryHelpers.LargestSolid(windowInstance);
        if (solid is null) return null;

        var brep = GeometryHelpers.BuildPolygonalFaceSet(solid, db);
        var placement = GeometryHelpers.BuildLocalPlacement(db, brep.Centroid);
        var shape = GeometryHelpers.WrapAsBRepShape(brep.FaceSet);

        // 1. Opening hosted on the wall — GG.Ifc auto-creates IfcRelVoidsElement.
        var openingPlacement = GeometryHelpers.BuildLocalPlacement(db, brep.Centroid);
        var openingShape = GeometryHelpers.WrapAsBRepShape(
            GeometryHelpers.BuildPolygonalFaceSet(solid, db).FaceSet);
        var opening = new IfcOpeningElement(hostIfcWall, openingPlacement, openingShape)
        {
            Name = $"Opening for {windowInstance.Name}",
        };

        // 2. The window itself.
        var ifcWindow = new IfcWindow(hostIfcWall, placement, shape)
        {
            Name = windowInstance.Name ?? string.Empty,
            Tag = windowInstance.UniqueId,
        };

        var bbox = windowInstance.get_BoundingBox(null);
        if (bbox is not null)
        {
            ifcWindow.OverallHeight = (bbox.Max.Z - bbox.Min.Z) * GeometryHelpers.FeetToMm;
            ifcWindow.OverallWidth = (bbox.Max.X - bbox.Min.X) * GeometryHelpers.FeetToMm;
        }

        // 3. RelFillsElement(opening, window) — GG.Ifc does NOT auto-create.
        _ = new IfcRelFillsElement(opening, ifcWindow);

        return ifcWindow;
    }
}
