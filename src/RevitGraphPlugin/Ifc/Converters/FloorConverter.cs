using Autodesk.Revit.DB;
using GeometryGym.Ifc;
using RevitGraphPlugin.Ifc.Geometry;

namespace RevitGraphPlugin.Ifc.Converters;

/// <summary>
/// Floor → IfcSlab sub-graph (Tier 1 / BRep skeleton).
///
/// Mirrors <see cref="WallConverter"/>'s proven shape: identity + placement +
/// spatial containment + BRep body (via the shared <see cref="BRepBodyBuilder"/>)
/// + Pset_SlabCommon. This is the minimal "insert slab" graphlet the ConMan2 diff
/// confirmed every element needs (IfcSlab → IfcLocalPlacement / IfcProductDefinitionShape,
/// IfcRelContainedInSpatialStructure → storey, IfcRelDefinesByProperties → Pset).
///
/// Deliberately DEFERRED to a later tier (native emits these, the diff lists them,
/// but they are not required for a structurally-correct, Solibri-openable slab):
///   - IfcSlabType + IfcRelDefinesByType
///   - IfcRelAssociatesMaterial (native floor: multi-layer IfcMaterialConstituentSet)
///   - IfcElementQuantity (area / volume / perimeter)
///   - native extrusion geometry (IfcExtrudedAreaSolid) instead of BRep
///   - surface styles / colours
///
/// TODO markers below flag the few Revit-API specifics to verify against a real
/// export (level param, placement origin, Pset source params).
/// </summary>
public sealed class FloorConverter : IElementConverter
{
    public BuiltInCategory Category => BuiltInCategory.OST_Floors;

    public void Convert(Element element, IfcModelContext ctx)
    {
        if (element is not Floor floor) return;

        // Anchor to the storey built from the floor's associated level.
        // TODO(verify): floors expose their level via Floor.LevelId (HostObject).
        // If a project uses a different level association, fall back to
        // get_Parameter(BuiltInParameter.LEVEL_PARAM)?.AsElementId().
        var levelId = floor.LevelId;
        if (levelId is null || levelId == ElementId.InvalidElementId ||
            !ctx.StoreyByLevel.TryGetValue(levelId, out var storey))
            return;   // floor on a level we have no storey for — skip for now

        var db = ctx.Db;

        // Placement origin: floors have no LocationCurve (unlike walls), so use the
        // element bounding-box min as the local origin. Body vertices are then emitted
        // relative to it (same scheme as WallConverter, keeping the point list small).
        // TODO(verify): confirm Z handling against native export — storey placement
        // already carries the level elevation.
        var bbox = floor.get_BoundingBox(null);
        var origin = bbox?.Min ?? XYZ.Zero;

        var placement = new IfcLocalPlacement(
            storey.ObjectPlacement,
            new IfcAxis2Placement3D(new IfcCartesianPoint(
                db, BRepBodyBuilder.Mm(origin.X), BRepBodyBuilder.Mm(origin.Y), BRepBodyBuilder.Mm(origin.Z))));

        // host = storey → ggifc creates the IfcRelContainedInSpatialStructure (the
        // "gluing" edge back to the preserved spatial context).
        var ifcSlab = new IfcSlab(storey, placement, null);
        ifcSlab.GlobalId = IfcGuidConverter.FromRevitUniqueId(floor.UniqueId);
        ifcSlab.PredefinedType = IfcSlabTypeEnum.FLOOR;

        var floorType = floor.FloorType;
        var family = floorType?.FamilyName ?? "Floor";
        var typeName = floorType?.Name ?? "Floor";
        ifcSlab.Name = $"{family}:{typeName}:{floor.Id.Value}";   // mirror wall naming
        ifcSlab.ObjectType = $"{family}:{typeName}";
        ifcSlab.Tag = floor.Id.Value.ToString();

        // BRep body via the shared builder (vertices local to origin, in mm).
        var shape = BRepBodyBuilder.Build(db, ctx.BodyContext, floor, origin);
        if (shape is not null)
            ifcSlab.Representation = shape;

        AttachSlabCommonPset(db, ifcSlab, floor);
    }

    private static void AttachSlabCommonPset(DatabaseIfc db, IfcSlab ifcSlab, Floor floor)
    {
        // TODO(verify): map IsExternal / LoadBearing from the right Revit params.
        // Floors are usually internal; LoadBearing follows the structural flag.
        var isExternal = new IfcPropertySingleValue(db, "IsExternal",
            new IfcBoolean(false));
        var loadBearing = new IfcPropertySingleValue(db, "LoadBearing",
            new IfcBoolean(IsLoadBearing(floor)));
        var pset = new IfcPropertySet("Pset_SlabCommon",
            new IfcProperty[] { isExternal, loadBearing });
        _ = new IfcRelDefinesByProperties(ifcSlab, pset);
    }

    private static bool IsLoadBearing(Floor floor)
    {
        // TODO(verify): the structural flag param id for floors.
        var p = floor.get_Parameter(BuiltInParameter.STRUCTURAL_FLOOR_ANALYZES_AS)
                ?? floor.get_Parameter(BuiltInParameter.INSTANCE_STRUCT_USAGE_PARAM);
        return p is not null && p.HasValue && p.AsInteger() != 0;
    }
}
