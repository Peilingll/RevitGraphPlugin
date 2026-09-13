using Autodesk.Revit.DB;
using GeometryGym.Ifc;
using RevitGraphPlugin.Ifc.Geometry;
using RevitGraphPlugin.Ifc.Hosting;

namespace RevitGraphPlugin.Ifc.Converters;

/// <summary>
/// Window → IfcWindow: placement + BRep body + Pset_WindowCommon + storey containment,
/// plus the opening chain into the host wall (<see cref="OpeningBuilder"/>). The hole in
/// the wall comes from Revit's own geometry. Not emitted: IfcWindowType, lining
/// properties, materials, quantities, a representation for the opening.
/// </summary>
public sealed class WindowConverter : IElementConverter
{
    public BuiltInCategory Category => BuiltInCategory.OST_Windows;

    public void Convert(Element element, IfcModelContext ctx)
    {
        if (element is not FamilyInstance window) return;

        // The host wall this window is cut into.
        var host = window.Host as Wall;

        // Storey from the window's LevelId, else the host wall's base level (matches native export).
        var levelId = window.LevelId;
        if (levelId is null || levelId == ElementId.InvalidElementId)
            levelId = host?.get_Parameter(BuiltInParameter.WALL_BASE_CONSTRAINT)?.AsElementId();
        if (levelId is null || levelId == ElementId.InvalidElementId ||
            !ctx.StoreyByLevel.TryGetValue(levelId, out var storey))
            return;   // window whose level has no storey — skip for now

        var db = ctx.Db;

        // Origin: LocationPoint, else bounding box.
        var origin = (window.Location as LocationPoint)?.Point
                     ?? window.get_BoundingBox(null)?.Min
                     ?? XYZ.Zero;

        var placement = new IfcLocalPlacement(
            storey.ObjectPlacement,
            new IfcAxis2Placement3D(new IfcCartesianPoint(
                db, BRepBodyBuilder.Mm(origin.X), BRepBodyBuilder.Mm(origin.Y), BRepBodyBuilder.Mm(origin.Z))));

        // Contained in the storey, not the wall (as native).
        var ifcWindow = new IfcWindow(storey, placement, null);
        ifcWindow.GlobalId = IfcGuidConverter.ForElement(window);
        StableIds.StampContainment(ifcWindow);   // storey containment rel: stable GlobalId
        ifcWindow.PredefinedType = IfcWindowTypeEnum.WINDOW;

        var symbol = window.Symbol;
        var family = symbol?.FamilyName ?? "Window";
        var typeName = symbol?.Name ?? "Window";
        ifcWindow.Name = $"{family}:{typeName}:{window.Id.Value}";
        ifcWindow.ObjectType = $"{family}:{typeName}";
        ifcWindow.Tag = window.Id.Value.ToString();

        // BRep body from the window's own family geometry.
        var shape = BRepBodyBuilder.Build(db, ctx.BodyContext, window, origin);
        if (shape is not null)
            ifcWindow.Representation = shape;

        AttachWindowCommonPset(db, ifcWindow, window);

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
            OpeningBuilder.VoidAndFill(hostWall, ifcWindow, openingPlacement, null,
                window.UniqueId + ":Opening");
        }
    }

    /// <summary>The type's Function parameter when the family defines it, else the host wall's IsExternal (matches native export).</summary>
    private static void AttachWindowCommonPset(DatabaseIfc db, IfcWindow ifcWindow, FamilyInstance window)
    {
        var isExternal = new IfcPropertySingleValue(db, "IsExternal",
            new IfcBoolean(PsetSources.IsExternal(window)));
        StableIds.AttachPset(ifcWindow, "Pset_WindowCommon", isExternal);
    }
}
