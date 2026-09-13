using Autodesk.Revit.DB;
using GeometryGym.Ifc;
using RevitGraphPlugin.Ifc.Geometry;

namespace RevitGraphPlugin.Ifc.Converters;

/// <summary>
/// Roof → IfcRoof: placement + BRep body + Pset_RoofCommon + storey containment, as one
/// IfcRoof carrying the body directly (native decomposes it into IfcSlab parts — a known
/// convention difference). Not emitted: IfcRoofType, materials, quantities, extrusion geometry.
/// </summary>
public sealed class RoofConverter : IElementConverter
{
    public BuiltInCategory Category => BuiltInCategory.OST_Roofs;

    public void Convert(Element element, IfcModelContext ctx)
    {
        if (element is not RoofBase roof) return;

        // Storey from HostObject.LevelId, else the base-level parameter (matches native export).
        var levelId = roof.LevelId;
        if (levelId is null || levelId == ElementId.InvalidElementId)
            levelId = roof.get_Parameter(BuiltInParameter.ROOF_BASE_LEVEL_PARAM)?.AsElementId();
        if (levelId is null || levelId == ElementId.InvalidElementId ||
            !ctx.StoreyByLevel.TryGetValue(levelId, out var storey))
            return;   // roof on a level we have no storey for — skip for now

        var db = ctx.Db;

        // Origin: bounding-box min; body vertices relative to it.
        var bbox = roof.get_BoundingBox(null);
        var origin = bbox?.Min ?? XYZ.Zero;

        var placement = new IfcLocalPlacement(
            storey.ObjectPlacement,
            new IfcAxis2Placement3D(new IfcCartesianPoint(
                db, BRepBodyBuilder.Mm(origin.X), BRepBodyBuilder.Mm(origin.Y), BRepBodyBuilder.Mm(origin.Z))));

        // host = storey → ggifc creates the containment rel.
        var ifcRoof = new IfcRoof(storey, placement, null);
        ifcRoof.GlobalId = IfcGuidConverter.ForElement(roof);
        StableIds.StampContainment(ifcRoof);   // storey containment rel: stable GlobalId
        // IfcRoof.PredefinedType is read-only in ggifc 0.1.22 (stays NOTDEFINED, as native).

        var roofType = roof.Document.GetElement(roof.GetTypeId()) as ElementType;
        var family = roofType?.FamilyName ?? "Roof";
        var typeName = roofType?.Name ?? "Roof";
        ifcRoof.Name = $"{family}:{typeName}:{roof.Id.Value}";   // mirror slab naming
        ifcRoof.ObjectType = $"{family}:{typeName}";
        ifcRoof.Tag = roof.Id.Value.ToString();

        // BRep body, vertices local to the origin.
        var shape = BRepBodyBuilder.Build(db, ctx.BodyContext, roof, origin);
        if (shape is not null)
            ifcRoof.Representation = shape;

        AttachRoofCommonPset(db, ifcRoof);
    }

    private static void AttachRoofCommonPset(DatabaseIfc db, IfcRoof ifcRoof)
    {
        // IsExternal = true, no LoadBearing (matches native export).
        var isExternal = new IfcPropertySingleValue(db, "IsExternal",
            new IfcBoolean(true));
        StableIds.AttachPset(ifcRoof, "Pset_RoofCommon", isExternal);
    }
}
