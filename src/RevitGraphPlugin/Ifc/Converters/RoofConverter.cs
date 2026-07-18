using Autodesk.Revit.DB;
using GeometryGym.Ifc;
using RevitGraphPlugin.Ifc.Geometry;

namespace RevitGraphPlugin.Ifc.Converters;

/// <summary>
/// Roof → IfcRoof sub-graph (Tier 1 / BRep skeleton).
///
/// Mirrors <see cref="FloorConverter"/>'s proven shape: identity + placement +
/// spatial containment + BRep body (shared <see cref="BRepBodyBuilder"/>) +
/// Pset_RoofCommon. A roof is emitted here as a single IfcRoof carrying the BRep
/// body directly.
///
/// DEFERRED to a later tier (native emits these, not required for a
/// structurally-correct, Solibri-openable roof):
///   - IfcRoof as an aggregate (IfcRelAggregates → child IfcSlab per roof face),
///     which is how Revit's native exporter decomposes a multi-slope roof
///   - IfcRoofType + IfcRelDefinesByType
///   - IfcRelAssociatesMaterial (multi-layer constituent set)
///   - IfcElementQuantity (area / volume)
///   - native extrusion geometry instead of BRep
///
/// TODO markers flag the Revit-API specifics to verify against a real export.
/// </summary>
public sealed class RoofConverter : IElementConverter
{
    public BuiltInCategory Category => BuiltInCategory.OST_Roofs;

    public void Convert(Element element, IfcModelContext ctx)
    {
        if (element is not RoofBase roof) return;

        // Anchor to the storey built from the roof's base level (HostObject.LevelId);
        // fall back to the roof base-level parameter if unset.
        // TODO(verify): ROOF_BASE_LEVEL_PARAM vs LevelId across FootPrint/Extrusion roofs.
        var levelId = roof.LevelId;
        if (levelId is null || levelId == ElementId.InvalidElementId)
            levelId = roof.get_Parameter(BuiltInParameter.ROOF_BASE_LEVEL_PARAM)?.AsElementId();
        if (levelId is null || levelId == ElementId.InvalidElementId ||
            !ctx.StoreyByLevel.TryGetValue(levelId, out var storey))
            return;   // roof on a level we have no storey for — skip for now

        var db = ctx.Db;

        // Placement origin: like floors, roofs have no LocationCurve, so use the
        // element bounding-box min as the local origin (body vertices relative to it).
        var bbox = roof.get_BoundingBox(null);
        var origin = bbox?.Min ?? XYZ.Zero;

        var placement = new IfcLocalPlacement(
            storey.ObjectPlacement,
            new IfcAxis2Placement3D(new IfcCartesianPoint(
                db, BRepBodyBuilder.Mm(origin.X), BRepBodyBuilder.Mm(origin.Y), BRepBodyBuilder.Mm(origin.Z))));

        // host = storey → ggifc creates the IfcRelContainedInSpatialStructure.
        var ifcRoof = new IfcRoof(storey, placement, null);
        ifcRoof.GlobalId = IfcGuidConverter.FromRevitUniqueId(roof.UniqueId);
        // IfcRoof.PredefinedType is read-only in ggifc 0.1.22 (internal mPredefinedType,
        // defaults to NOTDEFINED). We carry the actual geometry as a BRep body rather
        // than classifying the roof form, so NOTDEFINED matches native generic-roof
        // export. TODO(verify) against a real export; revisit if a form must be set.

        var roofType = roof.Document.GetElement(roof.GetTypeId()) as ElementType;
        var family = roofType?.FamilyName ?? "Roof";
        var typeName = roofType?.Name ?? "Roof";
        ifcRoof.Name = $"{family}:{typeName}:{roof.Id.Value}";   // mirror slab naming
        ifcRoof.ObjectType = $"{family}:{typeName}";
        ifcRoof.Tag = roof.Id.Value.ToString();

        // BRep body via the shared builder (vertices local to origin, in mm).
        var shape = BRepBodyBuilder.Build(db, ctx.BodyContext, roof, origin);
        if (shape is not null)
            ifcRoof.Representation = shape;

        AttachRoofCommonPset(db, ifcRoof);
    }

    private static void AttachRoofCommonPset(DatabaseIfc db, IfcRoof ifcRoof)
    {
        // TODO(verify): roofs are external by default; refine from instance params.
        var isExternal = new IfcPropertySingleValue(db, "IsExternal",
            new IfcBoolean(true));
        var pset = new IfcPropertySet("Pset_RoofCommon",
            new IfcProperty[] { isExternal });
        _ = new IfcRelDefinesByProperties(ifcRoof, pset);
    }
}
