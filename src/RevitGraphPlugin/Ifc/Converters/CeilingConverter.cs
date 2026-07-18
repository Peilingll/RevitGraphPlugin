using Autodesk.Revit.DB;
using GeometryGym.Ifc;
using RevitGraphPlugin.Ifc.Geometry;

namespace RevitGraphPlugin.Ifc.Converters;

/// <summary>
/// Ceiling → IfcCovering (PredefinedType=CEILING) sub-graph (Tier 1 / BRep skeleton).
///
/// Mirrors <see cref="FloorConverter"/>'s proven shape: identity + placement +
/// spatial containment + BRep body (shared <see cref="BRepBodyBuilder"/>) +
/// Pset_CoveringCommon. Native Revit exports ceilings as IfcCovering with the
/// CEILING predefined type — the same node/edge graphlet as a slab, only the IFC
/// class and Pset differ.
///
/// DEFERRED to a later tier (present in native, not required for a
/// structurally-correct, Solibri-openable covering):
///   - IfcCoveringType + IfcRelDefinesByType
///   - IfcRelAssociatesMaterial (finish layers)
///   - IfcElementQuantity (area)
///   - IfcRelCoversSpaces (covering ↔ bounded space), once IfcSpace exists
///
/// TODO markers flag the Revit-API specifics to verify against a real export.
/// </summary>
public sealed class CeilingConverter : IElementConverter
{
    public BuiltInCategory Category => BuiltInCategory.OST_Ceilings;

    public void Convert(Element element, IfcModelContext ctx)
    {
        if (element is not Ceiling ceiling) return;

        // Anchor to the storey built from the ceiling's associated level
        // (HostObject.LevelId); fall back to the level parameter if unset.
        // TODO(verify): ceiling.LevelId vs LEVEL_PARAM for hosted ceilings.
        var levelId = ceiling.LevelId;
        if (levelId is null || levelId == ElementId.InvalidElementId)
            levelId = ceiling.get_Parameter(BuiltInParameter.LEVEL_PARAM)?.AsElementId();
        if (levelId is null || levelId == ElementId.InvalidElementId ||
            !ctx.StoreyByLevel.TryGetValue(levelId, out var storey))
            return;   // ceiling on a level we have no storey for — skip for now

        var db = ctx.Db;

        // Placement origin: like floors, ceilings have no LocationCurve, so use the
        // element bounding-box min as the local origin (body vertices relative to it).
        var bbox = ceiling.get_BoundingBox(null);
        var origin = bbox?.Min ?? XYZ.Zero;

        var placement = new IfcLocalPlacement(
            storey.ObjectPlacement,
            new IfcAxis2Placement3D(new IfcCartesianPoint(
                db, BRepBodyBuilder.Mm(origin.X), BRepBodyBuilder.Mm(origin.Y), BRepBodyBuilder.Mm(origin.Z))));

        // host = storey → ggifc creates the IfcRelContainedInSpatialStructure.
        var ifcCovering = new IfcCovering(storey, placement, null);
        ifcCovering.GlobalId = IfcGuidConverter.FromRevitUniqueId(ceiling.UniqueId);
        ifcCovering.PredefinedType = IfcCoveringTypeEnum.CEILING;

        var ceilingType = ceiling.Document.GetElement(ceiling.GetTypeId()) as ElementType;
        var family = ceilingType?.FamilyName ?? "Ceiling";
        var typeName = ceilingType?.Name ?? "Ceiling";
        ifcCovering.Name = $"{family}:{typeName}:{ceiling.Id.Value}";   // mirror slab naming
        ifcCovering.ObjectType = $"{family}:{typeName}";
        ifcCovering.Tag = ceiling.Id.Value.ToString();

        // BRep body via the shared builder (vertices local to origin, in mm).
        var shape = BRepBodyBuilder.Build(db, ctx.BodyContext, ceiling, origin);
        if (shape is not null)
            ifcCovering.Representation = shape;

        AttachCoveringCommonPset(db, ifcCovering);
    }

    private static void AttachCoveringCommonPset(DatabaseIfc db, IfcCovering ifcCovering)
    {
        // TODO(verify): ceilings are internal by default; refine from instance params.
        var isExternal = new IfcPropertySingleValue(db, "IsExternal",
            new IfcBoolean(false));
        var pset = new IfcPropertySet("Pset_CoveringCommon",
            new IfcProperty[] { isExternal });
        _ = new IfcRelDefinesByProperties(ifcCovering, pset);
    }
}
