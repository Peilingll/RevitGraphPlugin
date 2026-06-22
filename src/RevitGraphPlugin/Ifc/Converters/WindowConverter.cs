using Autodesk.Revit.DB;
using GeometryGym.Ifc;
using RevitGraphPlugin.Ifc.Geometry;
using RevitGraphPlugin.Ifc.Hosting;

namespace RevitGraphPlugin.Ifc.Converters;

/// <summary>
/// Window → IfcWindow sub-graph (Tier 1a / BRep skeleton — element only, NO opening yet).
///
/// Windows are *hosted* elements: in native IFC a window fills an opening cut into
/// its host wall (Wall ──voids──&gt; IfcOpeningElement ──fills──&gt; IfcWindow). See the
/// ground-truth subgraph in data/samples/cypher/04_window_vs_wall_node_diff.json.
///
/// This Tier 1a pass emits ONLY the window element itself (placement + BRep body +
/// Pset_WindowCommon + spatial containment in the storey) — enough for the window to
/// appear in the right place. The hole in the wall comes "for free": the plugin
/// tessellates wall.get_Geometry(), which Revit already returns with the opening cut.
///
/// DEFERRED to Tier 1b (the proper hosted semantics, to be built on a shared
/// OpeningBuilder helper reused by DoorConverter):
///   - IfcOpeningElement (synthesised — Revit has no standalone opening element)
///   - IfcRelVoidsElement   : host IfcWall ──&gt; opening
///   - IfcRelFillsElement   : opening ──&gt; this window
/// Also Tier 2 (native has, not required to be valid): IfcWindowType + RelDefinesByType,
/// material, quantities, IfcWindowLiningProperties, surface styles.
///
/// TODO markers flag the Revit-API specifics to verify against a real export.
/// </summary>
public sealed class WindowConverter : IElementConverter
{
    public BuiltInCategory Category => BuiltInCategory.OST_Windows;

    public void Convert(Element element, IfcModelContext ctx)
    {
        if (element is not FamilyInstance window) return;

        // The host wall this window is cut into. Tier 1a doesn't use it yet, but
        // Tier 1b needs it to wire IfcRelVoidsElement back to the host IfcWall.
        var host = window.Host as Wall;

        // Anchor to the storey. A wall-hosted window's own LevelId may be unset, so
        // fall back to the host wall's base level.
        // TODO(verify): window.LevelId vs host wall WALL_BASE_CONSTRAINT.
        var levelId = window.LevelId;
        if (levelId is null || levelId == ElementId.InvalidElementId)
            levelId = host?.get_Parameter(BuiltInParameter.WALL_BASE_CONSTRAINT)?.AsElementId();
        if (levelId is null || levelId == ElementId.InvalidElementId ||
            !ctx.StoreyByLevel.TryGetValue(levelId, out var storey))
            return;   // window whose level has no storey — skip for now

        var db = ctx.Db;

        // Placement origin: windows are point-hosted (LocationPoint), fall back to bbox.
        var origin = (window.Location as LocationPoint)?.Point
                     ?? window.get_BoundingBox(null)?.Min
                     ?? XYZ.Zero;

        var placement = new IfcLocalPlacement(
            storey.ObjectPlacement,
            new IfcAxis2Placement3D(new IfcCartesianPoint(
                db, BRepBodyBuilder.Mm(origin.X), BRepBodyBuilder.Mm(origin.Y), BRepBodyBuilder.Mm(origin.Z))));

        // host = storey → ggifc creates IfcRelContainedInSpatialStructure (matches
        // native: windows are contained in the storey, not the wall).
        var ifcWindow = new IfcWindow(storey, placement, null);
        ifcWindow.GlobalId = IfcGuidConverter.FromRevitUniqueId(window.UniqueId);
        ifcWindow.PredefinedType = IfcWindowTypeEnum.WINDOW;

        var symbol = window.Symbol;
        var family = symbol?.FamilyName ?? "Window";
        var typeName = symbol?.Name ?? "Window";
        ifcWindow.Name = $"{family}:{typeName}:{window.Id.Value}";
        ifcWindow.ObjectType = $"{family}:{typeName}";
        ifcWindow.Tag = window.Id.Value.ToString();

        // BRep body via shared builder (window's own family geometry).
        var shape = BRepBodyBuilder.Build(db, ctx.BodyContext, window, origin);
        if (shape is not null)
            ifcWindow.Representation = shape;

        AttachWindowCommonPset(db, ifcWindow);

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
            OpeningBuilder.VoidAndFill(hostWall, ifcWindow, openingPlacement, null,
                window.UniqueId + ":Opening");
        }
    }

    private static void AttachWindowCommonPset(DatabaseIfc db, IfcWindow ifcWindow)
    {
        // TODO(verify): windows are external by default; refine from instance params.
        var isExternal = new IfcPropertySingleValue(db, "IsExternal",
            new IfcBoolean(true));
        var pset = new IfcPropertySet("Pset_WindowCommon",
            new IfcProperty[] { isExternal });
        _ = new IfcRelDefinesByProperties(ifcWindow, pset);
    }
}
