using Autodesk.Revit.DB;
using GeometryGym.Ifc;

namespace RevitGraphPlugin.Conversion;

/// <summary>
/// Converts a Revit <see cref="Wall"/> into an <see cref="IfcWall"/> hosted under
/// the supplied <see cref="IfcBuildingStorey"/>. Geometry follows the
/// IfcInfraToolKit pattern (related-work.md §4.1): tessellate the largest
/// <see cref="Solid"/> on the wall, translate to centroid, emit
/// <see cref="IfcPolygonalFaceSet"/>.
/// </summary>
internal static class WallConverter
{
    public static IfcWall? Convert(Wall wall, DatabaseIfc db, IfcBuildingStorey storey)
    {
        var solid = GeometryHelpers.LargestSolid(wall);
        if (solid is null) return null;

        var brep = GeometryHelpers.BuildPolygonalFaceSet(solid, db);
        var placement = GeometryHelpers.BuildLocalPlacement(db, brep.Centroid);
        var shape = GeometryHelpers.WrapAsBRepShape(brep.FaceSet);

        var ifcWall = new IfcWall(storey, placement, shape)
        {
            Name = wall.Name ?? string.Empty,
            // Tag carries the Revit UniqueId so Stage 4 can resolve ElementId →
            // graph node without depending on the auto-generated IFC GlobalId.
            Tag = wall.UniqueId,
        };
        return ifcWall;
    }
}
