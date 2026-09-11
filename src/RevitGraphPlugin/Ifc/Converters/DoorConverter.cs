using Autodesk.Revit.DB;
using GeometryGym.Ifc;
using RevitGraphPlugin.Ifc.Geometry;
using RevitGraphPlugin.Ifc.Hosting;

namespace RevitGraphPlugin.Ifc.Converters;

/// <summary>
/// Door → IfcDoor sub-graph (Tier 1b / BRep).
///
/// Doors are *hosted* elements: a door fills an opening cut into its host wall
/// (Wall ──voids──&gt; IfcOpeningElement ──fills──&gt; IfcDoor). Structurally identical
/// to <see cref="WindowConverter"/>; only the filler's IFC class, PredefinedType and
/// Pset differ. The void/fill machinery is shared via <see cref="OpeningBuilder"/>.
///
/// This converter emits:
///   - the door element itself: placement + BRep body + Pset_DoorCommon +
///     spatial containment in the storey
///   - the opening chain (Tier 1b): IfcOpeningElement + IfcRelVoidsElement (host wall →
///     opening) + IfcRelFillsElement (opening → this door)
///
/// The hole in the wall geometry comes "for free": the plugin tessellates
/// wall.get_Geometry(), which Revit already returns with the opening cut.
///
/// DEFERRED to Tier 2 (native has these, not required to be structurally valid):
///   - IfcDoorType + IfcRelDefinesByType, IfcDoorLiningProperties, IfcDoorPanelProperties
///   - material, quantities, surface styles
///   - a dedicated box representation for the opening (currently null — the host
///     wall's BRep already carries the actual hole)
///
/// TODO markers flag the Revit-API specifics to verify against a real export.
/// </summary>
public sealed class DoorConverter : IElementConverter
{
    public BuiltInCategory Category => BuiltInCategory.OST_Doors;

    public void Convert(Element element, IfcModelContext ctx)
    {
        if (element is not FamilyInstance door) return;

        // The host wall this door is cut into. Tier 1b needs it to wire
        // IfcRelVoidsElement back to the host IfcWall.
        var host = door.Host as Wall;

        // Anchor to the storey. A wall-hosted door's own LevelId may be unset, so
        // fall back to the host wall's base level.
        // TODO(verify): door.LevelId vs host wall WALL_BASE_CONSTRAINT.
        var levelId = door.LevelId;
        if (levelId is null || levelId == ElementId.InvalidElementId)
            levelId = host?.get_Parameter(BuiltInParameter.WALL_BASE_CONSTRAINT)?.AsElementId();
        if (levelId is null || levelId == ElementId.InvalidElementId ||
            !ctx.StoreyByLevel.TryGetValue(levelId, out var storey))
            return;   // door whose level has no storey — skip for now

        var db = ctx.Db;

        // Placement origin: doors are point-hosted (LocationPoint), fall back to bbox.
        var origin = (door.Location as LocationPoint)?.Point
                     ?? door.get_BoundingBox(null)?.Min
                     ?? XYZ.Zero;

        var placement = new IfcLocalPlacement(
            storey.ObjectPlacement,
            new IfcAxis2Placement3D(new IfcCartesianPoint(
                db, BRepBodyBuilder.Mm(origin.X), BRepBodyBuilder.Mm(origin.Y), BRepBodyBuilder.Mm(origin.Z))));

        // host = storey → ggifc creates IfcRelContainedInSpatialStructure (matches
        // native: doors are contained in the storey, not the wall).
        var ifcDoor = new IfcDoor(storey, placement, null);
        ifcDoor.GlobalId = IfcGuidConverter.FromRevitUniqueId(door.UniqueId);
        StableIds.StampContainment(ifcDoor);   // storey containment rel: stable GlobalId
        ifcDoor.PredefinedType = IfcDoorTypeEnum.DOOR;

        var symbol = door.Symbol;
        var family = symbol?.FamilyName ?? "Door";
        var typeName = symbol?.Name ?? "Door";
        ifcDoor.Name = $"{family}:{typeName}:{door.Id.Value}";
        ifcDoor.ObjectType = $"{family}:{typeName}";
        ifcDoor.Tag = door.Id.Value.ToString();

        // BRep body via shared builder (door's own family geometry).
        var shape = BRepBodyBuilder.Build(db, ctx.BodyContext, door, origin);
        if (shape is not null)
            ifcDoor.Representation = shape;

        AttachDoorCommonPset(db, ifcDoor);

        // Tier 1b: synthesise the opening and wire void/fill back to the host IfcWall.
        // Relies on WallConverter having run first (registry order) so the host wall is
        // already in ctx.ConvertedElements.
        if (host is not null &&
            ctx.ConvertedElements.TryGetValue(host.Id, out var hostElem) &&
            hostElem is IfcWall hostWall)
        {
            var openingPlacement = new IfcLocalPlacement(
                storey.ObjectPlacement,
                new IfcAxis2Placement3D(new IfcCartesianPoint(
                    db, BRepBodyBuilder.Mm(origin.X), BRepBodyBuilder.Mm(origin.Y), BRepBodyBuilder.Mm(origin.Z))));

            // TODO(Tier 2): give the opening its own box representation. For now the
            // host wall's BRep already carries the actual hole, so a null shape is
            // enough to establish the void/fill graph structure.
            OpeningBuilder.VoidAndFill(hostWall, ifcDoor, openingPlacement, null,
                door.UniqueId + ":Opening");
        }
    }

    private static void AttachDoorCommonPset(DatabaseIfc db, IfcDoor ifcDoor)
    {
        // TODO(verify): doors default to internal; refine from instance params
        // (e.g. the "IsExternal" / function parameter) against a real export.
        var isExternal = new IfcPropertySingleValue(db, "IsExternal",
            new IfcBoolean(false));
        StableIds.AttachPset(ifcDoor, "Pset_DoorCommon", isExternal);
    }
}
