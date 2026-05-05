using Autodesk.Revit.DB;
using GeometryGym.Ifc;

namespace RevitGraphPlugin.Conversion;

/// <summary>
/// Bridges Revit geometry (decimal-feet, double-precision XYZ) to GeometryGym
/// IFC tessellated BRep (millimetre, IfcPolygonalFaceSet). Follows the
/// IfcInfraToolKit pattern (related-work.md §4.1):
///   1. Tessellate to vertices + triangle indices
///   2. Translate so centroid sits at origin (origin then carried by IfcLocalPlacement)
///   3. Emit shared IfcCartesianPointList3D + per-face IfcIndexedPolygonalFace
///
/// Stage 3 simplifications: triangulate every Face (so each IfcIndexedPolygonalFace
/// is a 3-vertex polygon), pick the largest Solid from compound walls, ignore
/// holes / non-planar curvature beyond what Revit's tessellator returns.
/// </summary>
internal static class GeometryHelpers
{
    public const double FeetToMm = 304.8;

    /// <summary>Iterate every <see cref="Solid"/> instance reachable from <paramref name="element"/>.</summary>
    public static IEnumerable<Solid> EnumerateSolids(Element element)
    {
        var options = new Options
        {
            ComputeReferences = false,
            DetailLevel = ViewDetailLevel.Fine,
            IncludeNonVisibleObjects = false,
        };
        var geometry = element.get_Geometry(options);
        if (geometry is null) yield break;

        foreach (var go in geometry)
        {
            switch (go)
            {
                case Solid s when s.Volume > 1e-9:
                    yield return s;
                    break;
                case GeometryInstance gi:
                    foreach (var inner in gi.GetInstanceGeometry())
                        if (inner is Solid sInner && sInner.Volume > 1e-9)
                            yield return sInner;
                    break;
            }
        }
    }

    /// <summary>Pick the highest-volume solid; null if the element has none.</summary>
    public static Solid? LargestSolid(Element element) =>
        EnumerateSolids(element).MaxBy(s => s.Volume);

    /// <summary>
    /// Tessellated BRep result: <paramref name="centroid"/> is the centre of the
    /// raw vertex cloud in **mm**, before translation. The face set's vertices
    /// are already translated so centroid sits at the origin.
    /// </summary>
    public sealed record TessellatedBRep(IfcPolygonalFaceSet FaceSet, XYZ Centroid);

    public static TessellatedBRep BuildPolygonalFaceSet(Solid solid, DatabaseIfc db)
    {
        // Step 1: collect all triangles (per-face Mesh -> triangles).
        var verticesFt = new List<XYZ>();
        var triangles = new List<(int A, int B, int C)>();

        foreach (Face face in solid.Faces)
        {
            var mesh = face.Triangulate();
            if (mesh is null) continue;

            // Each Mesh has its own Vertices array. We append into the shared list
            // and remap triangle indices.
            var baseIdx = verticesFt.Count;
            foreach (var v in mesh.Vertices) verticesFt.Add(v);
            for (var i = 0; i < mesh.NumTriangles; i++)
            {
                var t = mesh.get_Triangle(i);
                triangles.Add((
                    baseIdx + (int)t.get_Index(0),
                    baseIdx + (int)t.get_Index(1),
                    baseIdx + (int)t.get_Index(2)));
            }
        }

        if (verticesFt.Count == 0)
            throw new InvalidOperationException("Solid produced no triangles.");

        // Step 2: convert ft -> mm and compute centroid.
        var verticesMm = verticesFt.Select(v => new XYZ(
            v.X * FeetToMm, v.Y * FeetToMm, v.Z * FeetToMm)).ToList();

        var cx = verticesMm.Average(v => v.X);
        var cy = verticesMm.Average(v => v.Y);
        var cz = verticesMm.Min(v => v.Z); // bottom of bounding box per IfcInfraToolKit
        var centroid = new XYZ(cx, cy, cz);

        // Step 3: translated coordinates.
        var coords = verticesMm
            .Select(v => Tuple.Create(v.X - cx, v.Y - cy, v.Z - cz))
            .ToList();

        var pointList = new IfcCartesianPointList3D(db, coords);

        // IFC indices are 1-based.
        var faces = triangles
            .Select(t => new IfcIndexedPolygonalFace(db, t.A + 1, t.B + 1, t.C + 1))
            .ToArray();

        var faceSet = new IfcPolygonalFaceSet(pointList, faces);
        return new TessellatedBRep(faceSet, centroid);
    }

    public static IfcLocalPlacement BuildLocalPlacement(DatabaseIfc db, XYZ originMm)
    {
        var origin = new IfcCartesianPoint(db, originMm.X, originMm.Y, originMm.Z);
        var axis = new IfcAxis2Placement3D(origin);
        return new IfcLocalPlacement(axis);
    }

    public static IfcProductDefinitionShape WrapAsBRepShape(IfcPolygonalFaceSet faceSet)
    {
        var rep = new IfcShapeRepresentation(faceSet);
        return new IfcProductDefinitionShape(rep);
    }
}
