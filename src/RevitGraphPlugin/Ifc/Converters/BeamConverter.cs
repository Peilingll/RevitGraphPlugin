using Autodesk.Revit.DB;
using GeometryGym.Ifc;
using RevitGraphPlugin.Ifc.Geometry;

namespace RevitGraphPlugin.Ifc.Converters;

/// <summary>
/// Beam / structural framing → IfcBeam: placement (from the LocationCurve start) + BRep
/// body + Pset_BeamCommon + storey containment. Not emitted: IfcBeamType, materials,
/// quantities, swept-solid geometry.
/// </summary>
public sealed class BeamConverter : IElementConverter
{
    public BuiltInCategory Category => BuiltInCategory.OST_StructuralFraming;

    public void Convert(Element element, IfcModelContext ctx)
    {
        if (element is not FamilyInstance beam) return;

        // Storey from INSTANCE_REFERENCE_LEVEL_PARAM, else LevelId (matches native export).
        var levelId = beam.get_Parameter(BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM)?.AsElementId();
        if (levelId is null || levelId == ElementId.InvalidElementId)
            levelId = beam.LevelId;
        if (levelId is null || levelId == ElementId.InvalidElementId ||
            !ctx.StoreyByLevel.TryGetValue(levelId, out var storey))
            return;   // beam on a level we have no storey for — skip for now

        var db = ctx.Db;

        // Origin: LocationCurve start, else LocationPoint / bounding box.
        var origin = (beam.Location as LocationCurve)?.Curve?.GetEndPoint(0)
                     ?? (beam.Location as LocationPoint)?.Point
                     ?? beam.get_BoundingBox(null)?.Min
                     ?? XYZ.Zero;

        var placement = new IfcLocalPlacement(
            storey.ObjectPlacement,
            new IfcAxis2Placement3D(new IfcCartesianPoint(
                db, BRepBodyBuilder.Mm(origin.X), BRepBodyBuilder.Mm(origin.Y), BRepBodyBuilder.Mm(origin.Z))));

        // host = storey → ggifc creates the containment rel.
        var ifcBeam = new IfcBeam(storey, placement, null);
        ifcBeam.GlobalId = IfcGuidConverter.ForElement(beam);
        StableIds.StampContainment(ifcBeam);   // storey containment rel: stable GlobalId
        ifcBeam.PredefinedType = IfcBeamTypeEnum.BEAM;

        var symbol = beam.Symbol;                   // the beam's FamilySymbol (type)
        var family = symbol?.FamilyName ?? "Beam";
        var typeName = symbol?.Name ?? "Beam";
        ifcBeam.Name = $"{family}:{typeName}:{beam.Id.Value}";   // mirror native naming
        ifcBeam.ObjectType = $"{family}:{typeName}";
        ifcBeam.Tag = beam.Id.Value.ToString();

        // BRep body (handles mapped family geometry).
        var shape = BRepBodyBuilder.Build(db, ctx.BodyContext, beam, origin);
        if (shape is not null)
            ifcBeam.Representation = shape;

        AttachBeamCommonPset(db, ifcBeam);
    }

    private static void AttachBeamCommonPset(DatabaseIfc db, IfcBeam ifcBeam)
    {
        // Structural framing: internal and load-bearing (matches native export).
        var isExternal = new IfcPropertySingleValue(db, "IsExternal",
            new IfcBoolean(false));
        var loadBearing = new IfcPropertySingleValue(db, "LoadBearing",
            new IfcBoolean(true));
        StableIds.AttachPset(ifcBeam, "Pset_BeamCommon", isExternal, loadBearing);
    }
}
