using Autodesk.Revit.DB;
using GeometryGym.Ifc;
using RevitGraphPlugin.Ifc.Geometry;

namespace RevitGraphPlugin.Ifc.Converters;

/// <summary>
/// Beam / structural framing → IfcBeam sub-graph (Tier 1 / BRep skeleton).
///
/// Same proven shape as <see cref="ColumnConverter"/>: identity + placement +
/// spatial containment + BRep body (shared <see cref="BRepBodyBuilder"/>) +
/// Pset_BeamCommon. The only structural difference from a column is placement:
/// beams are *curve-hosted* (they run along a LocationCurve), so the local origin
/// comes from the curve start point rather than a LocationPoint.
///
/// DEFERRED to a later tier (present in native, not required for a
/// structurally-correct, Solibri-openable beam):
///   - IfcBeamType + IfcRelDefinesByType (+ mapped geometry via IfcRepresentationMap)
///   - IfcRelAssociatesMaterial (profile / material)
///   - IfcElementQuantity (length / volume)
///   - native swept-solid geometry (IfcExtrudedAreaSolid along the axis) instead of BRep
///
/// TODO markers flag the Revit-API specifics to verify against a real export.
/// </summary>
public sealed class BeamConverter : IElementConverter
{
    public BuiltInCategory Category => BuiltInCategory.OST_StructuralFraming;

    public void Convert(Element element, IfcModelContext ctx)
    {
        if (element is not FamilyInstance beam) return;

        // Anchor to the storey built from the beam's reference level. Structural
        // framing exposes it via INSTANCE_REFERENCE_LEVEL_PARAM ("Reference Level");
        // fall back to FamilyInstance.LevelId if unset.
        // TODO(verify): reference-level vs the two end levels for sloped beams.
        var levelId = beam.get_Parameter(BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM)?.AsElementId();
        if (levelId is null || levelId == ElementId.InvalidElementId)
            levelId = beam.LevelId;
        if (levelId is null || levelId == ElementId.InvalidElementId ||
            !ctx.StoreyByLevel.TryGetValue(levelId, out var storey))
            return;   // beam on a level we have no storey for — skip for now

        var db = ctx.Db;

        // Placement origin: beams are curve-hosted, so use the LocationCurve start
        // point; fall back to LocationPoint / bbox for atypical families.
        var origin = (beam.Location as LocationCurve)?.Curve?.GetEndPoint(0)
                     ?? (beam.Location as LocationPoint)?.Point
                     ?? beam.get_BoundingBox(null)?.Min
                     ?? XYZ.Zero;

        var placement = new IfcLocalPlacement(
            storey.ObjectPlacement,
            new IfcAxis2Placement3D(new IfcCartesianPoint(
                db, BRepBodyBuilder.Mm(origin.X), BRepBodyBuilder.Mm(origin.Y), BRepBodyBuilder.Mm(origin.Z))));

        // host = storey → ggifc creates the IfcRelContainedInSpatialStructure.
        var ifcBeam = new IfcBeam(storey, placement, null);
        ifcBeam.GlobalId = IfcGuidConverter.FromRevitUniqueId(beam.UniqueId);
        ifcBeam.PredefinedType = IfcBeamTypeEnum.BEAM;

        var symbol = beam.Symbol;                   // the beam's FamilySymbol (type)
        var family = symbol?.FamilyName ?? "Beam";
        var typeName = symbol?.Name ?? "Beam";
        ifcBeam.Name = $"{family}:{typeName}:{beam.Id.Value}";   // mirror native naming
        ifcBeam.ObjectType = $"{family}:{typeName}";
        ifcBeam.Tag = beam.Id.Value.ToString();

        // BRep body via shared builder (handles GeometryInstance / mapped family geo).
        var shape = BRepBodyBuilder.Build(db, ctx.BodyContext, beam, origin);
        if (shape is not null)
            ifcBeam.Representation = shape;

        AttachBeamCommonPset(db, ifcBeam);
    }

    private static void AttachBeamCommonPset(DatabaseIfc db, IfcBeam ifcBeam)
    {
        // TODO(verify): IsExternal / LoadBearing sources. Beams are usually internal
        // and load-bearing by definition (structural framing).
        var isExternal = new IfcPropertySingleValue(db, "IsExternal",
            new IfcBoolean(false));
        var loadBearing = new IfcPropertySingleValue(db, "LoadBearing",
            new IfcBoolean(true));
        var pset = new IfcPropertySet("Pset_BeamCommon",
            new IfcProperty[] { isExternal, loadBearing });
        _ = new IfcRelDefinesByProperties(ifcBeam, pset);
    }
}
