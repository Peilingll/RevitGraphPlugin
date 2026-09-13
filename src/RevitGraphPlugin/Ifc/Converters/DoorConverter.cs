using Autodesk.Revit.DB;
using GeometryGym.Ifc;
using RevitGraphPlugin.Ifc.Geometry;
using RevitGraphPlugin.Ifc.Hosting;

namespace RevitGraphPlugin.Ifc.Converters;

/// <summary>
/// Door → IfcDoor: same shape as <see cref="WindowConverter"/> (placement + BRep body +
/// Pset_DoorCommon + storey containment + opening chain via <see cref="OpeningBuilder"/>);
/// only the IFC class, PredefinedType and Pset differ. Not emitted: IfcDoorType, lining /
/// panel properties, materials, quantities, a representation for the opening.
/// </summary>
public sealed class DoorConverter : IElementConverter
{
    public BuiltInCategory Category => BuiltInCategory.OST_Doors;

    public void Convert(Element element, IfcModelContext ctx)
    {
        if (element is not FamilyInstance door) return;

        // The host wall this door is cut into.
        var host = door.Host as Wall;

        // Storey from the door's LevelId, else the host wall's base level (matches native export).
        var levelId = door.LevelId;
        if (levelId is null || levelId == ElementId.InvalidElementId)
            levelId = host?.get_Parameter(BuiltInParameter.WALL_BASE_CONSTRAINT)?.AsElementId();
        if (levelId is null || levelId == ElementId.InvalidElementId ||
            !ctx.StoreyByLevel.TryGetValue(levelId, out var storey))
            return;   // door whose level has no storey — skip for now

        var db = ctx.Db;

        // Origin: LocationPoint, else bounding box.
        var origin = (door.Location as LocationPoint)?.Point
                     ?? door.get_BoundingBox(null)?.Min
                     ?? XYZ.Zero;

        var placement = new IfcLocalPlacement(
            storey.ObjectPlacement,
            new IfcAxis2Placement3D(new IfcCartesianPoint(
                db, BRepBodyBuilder.Mm(origin.X), BRepBodyBuilder.Mm(origin.Y), BRepBodyBuilder.Mm(origin.Z))));

        // Contained in the storey, not the wall (as native).
        var ifcDoor = new IfcDoor(storey, placement, null);
        ifcDoor.GlobalId = IfcGuidConverter.ForElement(door);
        StableIds.StampContainment(ifcDoor);   // storey containment rel: stable GlobalId
        ifcDoor.PredefinedType = IfcDoorTypeEnum.DOOR;

        var symbol = door.Symbol;
        var family = symbol?.FamilyName ?? "Door";
        var typeName = symbol?.Name ?? "Door";
        ifcDoor.Name = $"{family}:{typeName}:{door.Id.Value}";
        ifcDoor.ObjectType = $"{family}:{typeName}";
        ifcDoor.Tag = door.Id.Value.ToString();

        // BRep body from the door's own family geometry.
        var shape = BRepBodyBuilder.Build(db, ctx.BodyContext, door, origin);
        if (shape is not null)
            ifcDoor.Representation = shape;

        AttachDoorCommonPset(db, ifcDoor, door);

        // Opening chain into the host wall (already converted: registry order).
        if (host is not null &&
            ctx.ConvertedElements.TryGetValue(host.Id, out var hostElem) &&
            hostElem is IfcWall hostWall)
        {
            var openingPlacement = new IfcLocalPlacement(
                storey.ObjectPlacement,
                new IfcAxis2Placement3D(new IfcCartesianPoint(
                    db, BRepBodyBuilder.Mm(origin.X), BRepBodyBuilder.Mm(origin.Y), BRepBodyBuilder.Mm(origin.Z))));

            // TODO: the opening has no representation of its own (the host BRep already carries the hole).
            OpeningBuilder.VoidAndFill(hostWall, ifcDoor, openingPlacement, null,
                door.UniqueId + ":Opening");
        }
    }

    /// <summary>The type's Function parameter when the family defines it, else the host wall's IsExternal (matches native export).</summary>
    private static void AttachDoorCommonPset(DatabaseIfc db, IfcDoor ifcDoor, FamilyInstance door)
    {
        var isExternal = new IfcPropertySingleValue(db, "IsExternal",
            new IfcBoolean(PsetSources.IsExternal(door)));
        StableIds.AttachPset(ifcDoor, "Pset_DoorCommon", isExternal);
    }
}
