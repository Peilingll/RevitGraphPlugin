using Autodesk.Revit.DB;
using GeometryGym.Ifc;

namespace RevitGraphPlugin.Ifc.Geometry;

/// <summary>
/// Shared BRep body-geometry builder: tessellates a Revit element's solid(s) into
/// an <see cref="IfcPolygonalFaceSet"/> body representation. Extracted from
/// WallConverter Step B so every element converter can reuse the same tessellation
/// (the "dedicated library" the professor asked for — one geometry path, many
/// element types).
///
/// Vertices are emitted in millimetres, local to a caller-supplied placement
/// <c>origin</c> (so the IfcLocalPlacement carries the global position and the
/// point list stays small / element-local). Returns null when the element has no
/// usable solid geometry.
/// </summary>
public static class BRepBodyBuilder
{
    /// <summary>Convert Revit internal units (feet) to millimetres.</summary>
    public static double Mm(double feet) =>
        UnitUtils.ConvertFromInternalUnits(feet, UnitTypeId.Millimeters);

    /// <summary>
    /// Build an <see cref="IfcProductDefinitionShape"/> ('Body' / Tessellation) for
    /// <paramref name="element"/>, or null if it yields no solids.
    /// </summary>
    public static IfcProductDefinitionShape? Build(
        DatabaseIfc db,
        IfcGeometricRepresentationSubContext bodyContext,
        Element element,
        XYZ origin)
    {
        var coords = new List<Tuple<double, double, double>>();
        var indexByKey = new Dictionary<(long, long, long), int>();
        var faces = new List<IfcIndexedPolygonalFace>();

        var options = new Options { ComputeReferences = false, DetailLevel = ViewDetailLevel.Fine };
        var geometry = element.get_Geometry(options);
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
}
