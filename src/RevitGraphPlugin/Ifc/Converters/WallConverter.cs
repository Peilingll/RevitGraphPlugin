using Autodesk.Revit.DB;
using GeometryGym.Ifc;

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
            new IfcAxis2Placement3D(new IfcCartesianPoint(db, Mm(origin.X), Mm(origin.Y), Mm(origin.Z))));

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

        // Step B: BRep body geometry, vertices local to the wall origin.
        var shape = BuildBodyBRep(db, ctx.BodyContext, wall, origin);
        if (shape is not null)
            ifcWall.Representation = shape;

        AttachWallCommonPset(db, ifcWall, wall);
    }

    /// <summary>
    /// Tessellate the wall's solid(s) into an IfcPolygonalFaceSet body representation.
    /// Vertices are emitted in mm, local to <paramref name="origin"/> (the wall
    /// placement point). Returns null if the wall has no usable solid geometry.
    /// </summary>
    private static IfcProductDefinitionShape BuildBodyBRep(
        DatabaseIfc db, IfcGeometricRepresentationSubContext bodyContext, Wall wall, XYZ origin)
    {
        var coords = new List<Tuple<double, double, double>>();
        var indexByKey = new Dictionary<(long, long, long), int>();
        var faces = new List<IfcIndexedPolygonalFace>();

        var options = new Options { ComputeReferences = false, DetailLevel = ViewDetailLevel.Fine };
        var geometry = wall.get_Geometry(options);
        if (geometry is null) return null;

        foreach (var gobj in geometry)
        {
            if (gobj is Solid solid)
                TessellateSolid(db, solid, origin, coords, indexByKey, faces);
            else if (gobj is GeometryInstance instance)
                foreach (var io in instance.GetInstanceGeometry())
                    if (io is Solid s) TessellateSolid(db, s, origin, coords, indexByKey, faces);
        }

        if (coords.Count == 0 || faces.Count == 0) return null;

        var pointList = new IfcCartesianPointList3D(db, coords);
        var faceSet = new IfcPolygonalFaceSet(pointList, faces);
        var shapeRep = new IfcShapeRepresentation(
            bodyContext, faceSet, ShapeRepresentationType.Tessellation);
        return new IfcProductDefinitionShape(shapeRep);
    }

    private static void TessellateSolid(
        DatabaseIfc db, Solid solid, XYZ origin,
        List<Tuple<double, double, double>> coords,
        Dictionary<(long, long, long), int> indexByKey,
        List<IfcIndexedPolygonalFace> faces)
    {
        if (solid.Faces.IsEmpty || solid.Volume <= 0) return;
        foreach (Face face in solid.Faces)
        {
            var mesh = face.Triangulate();
            if (mesh is null) continue;
            for (var i = 0; i < mesh.NumTriangles; i++)
            {
                var tri = mesh.get_Triangle(i);
                var a = AddVertex(coords, indexByKey, tri.get_Vertex(0), origin);
                var b = AddVertex(coords, indexByKey, tri.get_Vertex(1), origin);
                var c = AddVertex(coords, indexByKey, tri.get_Vertex(2), origin);
                faces.Add(new IfcIndexedPolygonalFace(db, a, b, c));
            }
        }
    }

    /// <summary>Add a vertex (deduplicated), returning its 1-based index in the point list.</summary>
    private static int AddVertex(
        List<Tuple<double, double, double>> coords,
        Dictionary<(long, long, long), int> indexByKey,
        XYZ v, XYZ origin)
    {
        var x = Mm(v.X - origin.X);
        var y = Mm(v.Y - origin.Y);
        var z = Mm(v.Z - origin.Z);
        var key = ((long)Math.Round(x * 1000), (long)Math.Round(y * 1000), (long)Math.Round(z * 1000));
        if (indexByKey.TryGetValue(key, out var existing)) return existing;
        coords.Add(Tuple.Create(x, y, z));
        var index = coords.Count;   // IFC indices are 1-based
        indexByKey[key] = index;
        return index;
    }

    private static double Mm(double feet) =>
        UnitUtils.ConvertFromInternalUnits(feet, UnitTypeId.Millimeters);

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
