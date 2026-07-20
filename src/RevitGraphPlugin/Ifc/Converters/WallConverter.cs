using Autodesk.Revit.DB;
using GeometryGym.Ifc;
using RevitGraphPlugin.Ifc.Geometry;

namespace RevitGraphPlugin.Ifc.Converters;

/// <summary>
/// Wall → IfcWall sub-graph.
///
/// Step A (this file): identity + placement + spatial containment + Pset_WallCommon.
/// Step B will add BRep body geometry (IfcPolygonalFaceSet) to the wall's
/// representation. Geometry is intentionally a separate step so the wall node /
/// containment can be verified in the graph first.
/// </summary>
public sealed class WallConverter : IElementConverter
{
    public BuiltInCategory Category => BuiltInCategory.OST_Walls;

    public void Convert(Element element, IfcModelContext ctx)
    {
        if (element is not Wall wall) return;

        // Anchor to the storey built from the wall's base level.
        var levelId = wall.get_Parameter(BuiltInParameter.WALL_BASE_CONSTRAINT)?.AsElementId();
        if (levelId is null || !ctx.StoreyByLevel.TryGetValue(levelId, out var storey))
            return;   // wall on a level we have no storey for — skip for now

        var db = ctx.Db;

        // Placement: wall origin (location-curve start) in mm, relative to the storey.
        var origin = (wall.Location as LocationCurve)?.Curve.GetEndPoint(0) ?? XYZ.Zero;
        var placement = new IfcLocalPlacement(
            storey.ObjectPlacement,
            new IfcAxis2Placement3D(new IfcCartesianPoint(
                db, BRepBodyBuilder.Mm(origin.X), BRepBodyBuilder.Mm(origin.Y), BRepBodyBuilder.Mm(origin.Z))));

        // host = storey → ggifc creates the IfcRelContainedInSpatialStructure.
        // Step A: no representation yet (added in Step B).
        var ifcWall = new IfcWall(storey, placement, null);
        ifcWall.GlobalId = IfcGuidConverter.FromRevitUniqueId(wall.UniqueId);

        var family = wall.WallType?.FamilyName ?? "Basic Wall";
        var typeName = wall.WallType?.Name ?? "Wall";
        ifcWall.Name = $"{family}:{typeName}:{wall.Id.Value}";   // native: 'Basic Wall:<type>:<id>'
        ifcWall.ObjectType = $"{family}:{typeName}";
        ifcWall.Tag = wall.Id.Value.ToString();
        ifcWall.PredefinedType = IfcWallTypeEnum.NOTDEFINED;

        // Step B: BRep body geometry, vertices local to the wall origin (shared builder).
        var shape = BRepBodyBuilder.Build(db, ctx.BodyContext, wall, origin);
        if (shape is not null)
            ifcWall.Representation = shape;

        AttachWallCommonPset(db, ifcWall, wall);
        // ConvertedElements registration (for hosted-element host lookup + live modify/
        // remove) is done uniformly by ElementConverterRegistry.ConvertOne.
    }

    private static void AttachWallCommonPset(DatabaseIfc db, IfcWall ifcWall, Wall wall)
    {
        var isExternal = new IfcPropertySingleValue(db, "IsExternal",
            new IfcBoolean(IsExternal(wall)));
        var loadBearing = new IfcPropertySingleValue(db, "LoadBearing",
            new IfcBoolean(IsLoadBearing(wall)));
        var pset = new IfcPropertySet("Pset_WallCommon",
            new IfcProperty[] { isExternal, loadBearing });
        _ = new IfcRelDefinesByProperties(ifcWall, pset);
    }

    /// <summary>Exterior walls (WallType Function = Exterior) map to IsExternal = true.</summary>
    private static bool IsExternal(Wall wall)
    {
        var p = wall.WallType?.get_Parameter(BuiltInParameter.FUNCTION_PARAM);
        return p is not null && p.AsInteger() == (int)WallFunction.Exterior;
    }

    private static bool IsLoadBearing(Wall wall)
    {
        var p = wall.get_Parameter(BuiltInParameter.WALL_STRUCTURAL_SIGNIFICANT);
        return p is not null && p.AsInteger() == 1;
    }
}
