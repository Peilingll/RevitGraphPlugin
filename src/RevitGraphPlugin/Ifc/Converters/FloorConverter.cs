using Autodesk.Revit.DB;
using GeometryGym.Ifc;
using RevitGraphPlugin.Ifc.Geometry;

namespace RevitGraphPlugin.Ifc.Converters;

/// <summary>
/// Floor → IfcSlab: placement + BRep body + Pset_SlabCommon + storey containment (same
/// shape as <see cref="WallConverter"/>). Not emitted: IfcSlabType, materials,
/// quantities, extrusion geometry, surface styles.
/// </summary>
public sealed class FloorConverter : IElementConverter
{
    public BuiltInCategory Category => BuiltInCategory.OST_Floors;

    public void Convert(Element element, IfcModelContext ctx)
    {
        if (element is not Floor floor) return;

        // Storey from the floor's LevelId (matches native export).
        var levelId = floor.LevelId;
        if (levelId is null || levelId == ElementId.InvalidElementId ||
            !ctx.StoreyByLevel.TryGetValue(levelId, out var storey))
            return;   // floor on a level we have no storey for — skip for now

        var db = ctx.Db;

        // Origin: bounding-box min (floors have no LocationCurve); body vertices are
        // relative to it. The elevation lives here, not in the storey placement as in
        // native — a known convention difference (same world coordinates).
        var bbox = floor.get_BoundingBox(null);
        var origin = bbox?.Min ?? XYZ.Zero;

        var placement = new IfcLocalPlacement(
            storey.ObjectPlacement,
            new IfcAxis2Placement3D(new IfcCartesianPoint(
                db, BRepBodyBuilder.Mm(origin.X), BRepBodyBuilder.Mm(origin.Y), BRepBodyBuilder.Mm(origin.Z))));

        // host = storey → ggifc creates the containment rel.
        var ifcSlab = new IfcSlab(storey, placement, null);
        ifcSlab.GlobalId = IfcGuidConverter.ForElement(floor);
        StableIds.StampContainment(ifcSlab);   // storey containment rel: stable GlobalId
        ifcSlab.PredefinedType = IfcSlabTypeEnum.FLOOR;

        var floorType = floor.FloorType;
        var family = floorType?.FamilyName ?? "Floor";
        var typeName = floorType?.Name ?? "Floor";
        ifcSlab.Name = $"{family}:{typeName}:{floor.Id.Value}";   // mirror wall naming
        ifcSlab.ObjectType = $"{family}:{typeName}";
        ifcSlab.Tag = floor.Id.Value.ToString();

        // BRep body, vertices local to the origin.
        var shape = BRepBodyBuilder.Build(db, ctx.BodyContext, floor, origin);
        if (shape is not null)
            ifcSlab.Representation = shape;

        AttachSlabCommonPset(db, ifcSlab, floor);
    }

    /// <summary>IsExternal from the type's Function, LoadBearing from the instance's Structural checkbox (matches native export).</summary>
    private static void AttachSlabCommonPset(DatabaseIfc db, IfcSlab ifcSlab, Floor floor)
    {
        var isExternal = new IfcPropertySingleValue(db, "IsExternal",
            new IfcBoolean(PsetSources.IsExternal(floor)));
        var loadBearing = new IfcPropertySingleValue(db, "LoadBearing",
            new IfcBoolean(PsetSources.IsLoadBearing(floor)));
        StableIds.AttachPset(ifcSlab, "Pset_SlabCommon", isExternal, loadBearing);
    }
}
