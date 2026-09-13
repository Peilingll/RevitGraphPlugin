using Autodesk.Revit.DB;
using GeometryGym.Ifc;
using RevitGraphPlugin.Ifc.Geometry;

namespace RevitGraphPlugin.Ifc.Converters;

/// <summary>
/// Ceiling → IfcCovering (PredefinedType = CEILING, as native): placement + BRep body +
/// Pset_CoveringCommon + storey containment. Not emitted: IfcCoveringType, materials,
/// quantities, IfcRelCoversSpaces.
/// </summary>
public sealed class CeilingConverter : IElementConverter
{
    public BuiltInCategory Category => BuiltInCategory.OST_Ceilings;

    public void Convert(Element element, IfcModelContext ctx)
    {
        if (element is not Ceiling ceiling) return;

        // Storey from HostObject.LevelId, else the level parameter (matches native export).
        var levelId = ceiling.LevelId;
        if (levelId is null || levelId == ElementId.InvalidElementId)
            levelId = ceiling.get_Parameter(BuiltInParameter.LEVEL_PARAM)?.AsElementId();
        if (levelId is null || levelId == ElementId.InvalidElementId ||
            !ctx.StoreyByLevel.TryGetValue(levelId, out var storey))
            return;   // ceiling on a level we have no storey for — skip for now

        var db = ctx.Db;

        // Origin: bounding-box min; body vertices relative to it.
        var bbox = ceiling.get_BoundingBox(null);
        var origin = bbox?.Min ?? XYZ.Zero;

        var placement = new IfcLocalPlacement(
            storey.ObjectPlacement,
            new IfcAxis2Placement3D(new IfcCartesianPoint(
                db, BRepBodyBuilder.Mm(origin.X), BRepBodyBuilder.Mm(origin.Y), BRepBodyBuilder.Mm(origin.Z))));

        // host = storey → ggifc creates the containment rel.
        var ifcCovering = new IfcCovering(storey, placement, null);
        ifcCovering.GlobalId = IfcGuidConverter.ForElement(ceiling);
        StableIds.StampContainment(ifcCovering);   // storey containment rel: stable GlobalId
        ifcCovering.PredefinedType = IfcCoveringTypeEnum.CEILING;

        var ceilingType = ceiling.Document.GetElement(ceiling.GetTypeId()) as ElementType;
        var family = ceilingType?.FamilyName ?? "Ceiling";
        var typeName = ceilingType?.Name ?? "Ceiling";
        ifcCovering.Name = $"{family}:{typeName}:{ceiling.Id.Value}";   // mirror slab naming
        ifcCovering.ObjectType = $"{family}:{typeName}";
        ifcCovering.Tag = ceiling.Id.Value.ToString();

        // BRep body, vertices local to the origin.
        var shape = BRepBodyBuilder.Build(db, ctx.BodyContext, ceiling, origin);
        if (shape is not null)
            ifcCovering.Representation = shape;

        AttachCoveringCommonPset(db, ifcCovering);
    }

    private static void AttachCoveringCommonPset(DatabaseIfc db, IfcCovering ifcCovering)
    {
        // IsExternal = false, no LoadBearing (matches native export).
        var isExternal = new IfcPropertySingleValue(db, "IsExternal",
            new IfcBoolean(false));
        StableIds.AttachPset(ifcCovering, "Pset_CoveringCommon", isExternal);
    }
}
