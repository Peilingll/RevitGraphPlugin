using Autodesk.Revit.DB;
using GeometryGym.Ifc;
using RevitGraphPlugin.Ifc.Geometry;

namespace RevitGraphPlugin.Ifc.Converters;

/// <summary>
/// Column → IfcColumn sub-graph (Tier 1 / BRep skeleton).
///
/// Same proven shape as <see cref="WallConverter"/> / <see cref="FloorConverter"/>:
/// identity + placement + spatial containment + BRep body (shared
/// <see cref="BRepBodyBuilder"/>) + Pset_ColumnCommon. The ConMan2 diff
/// (data/samples/diff/002_col_node_diff.json) confirms the minimal "insert column"
/// graphlet: IfcColumn → IfcLocalPlacement / IfcProductDefinitionShape,
/// IfcRelContainedInSpatialStructure → storey, IfcRelDefinesByProperties → Pset.
///
/// DEFERRED to a later tier (present in native + the diff, not required for a
/// structurally-correct, Solibri-openable column):
///   - IfcColumnType + IfcRelDefinesByType (+ mapped geometry via IfcRepresentationMap)
///   - IfcRelAssociatesMaterial
///   - IfcElementQuantity
///   - native extrusion geometry (IfcExtrudedAreaSolid) instead of BRep
/// </summary>
public sealed class ColumnConverter : IElementConverter
{
    private readonly BuiltInCategory _category;

    /// <summary>
    /// Columns come in two Revit categories that both export to IfcColumn:
    /// OST_StructuralColumns (structural) and OST_Columns (architectural). The
    /// registry registers one instance per category so both are covered.
    /// </summary>
    public ColumnConverter(BuiltInCategory category) => _category = category;

    public BuiltInCategory Category => _category;

    public void Convert(Element element, IfcModelContext ctx)
    {
        if (element is not FamilyInstance column) return;

        // Anchor to the storey built from the column's base level. Structural
        // columns expose it via FAMILY_BASE_LEVEL_PARAM; architectural columns
        // (OST_Columns) fall back to FamilyInstance.LevelId.
        var levelId = column.get_Parameter(BuiltInParameter.FAMILY_BASE_LEVEL_PARAM)?.AsElementId();
        if (levelId is null || levelId == ElementId.InvalidElementId)
            levelId = column.LevelId;
        if (levelId is null || levelId == ElementId.InvalidElementId ||
            !ctx.StoreyByLevel.TryGetValue(levelId, out var storey))
            return;   // column on a level we have no storey for — skip for now

        var db = ctx.Db;

        // Placement origin: columns are point-hosted, so use the LocationPoint.
        var origin = (column.Location as LocationPoint)?.Point ?? XYZ.Zero;

        var placement = new IfcLocalPlacement(
            storey.ObjectPlacement,
            new IfcAxis2Placement3D(new IfcCartesianPoint(
                db, BRepBodyBuilder.Mm(origin.X), BRepBodyBuilder.Mm(origin.Y), BRepBodyBuilder.Mm(origin.Z))));

        // host = storey → ggifc creates the IfcRelContainedInSpatialStructure (gluing
        // edge back to the preserved spatial context).
        var ifcColumn = new IfcColumn(storey, placement, null);
        ifcColumn.GlobalId = IfcGuidConverter.FromRevitUniqueId(column.UniqueId);
        ifcColumn.PredefinedType = IfcColumnTypeEnum.COLUMN;

        var symbol = column.Symbol;                 // the column's FamilySymbol (type)
        var family = symbol?.FamilyName ?? "Column";
        var typeName = symbol?.Name ?? "Column";
        ifcColumn.Name = $"{family}:{typeName}:{column.Id.Value}";   // mirror native naming
        ifcColumn.ObjectType = $"{family}:{typeName}";
        ifcColumn.Tag = column.Id.Value.ToString();

        // BRep body via shared builder (handles GeometryInstance / mapped family geo).
        var shape = BRepBodyBuilder.Build(db, ctx.BodyContext, column, origin);
        if (shape is not null)
            ifcColumn.Representation = shape;

        AttachColumnCommonPset(db, ifcColumn, column);
    }

    private static void AttachColumnCommonPset(DatabaseIfc db, IfcColumn ifcColumn, FamilyInstance column)
    {
        // TODO(verify): IsExternal / LoadBearing sources. Columns are usually internal;
        // structural columns are load-bearing by definition.
        var isExternal = new IfcPropertySingleValue(db, "IsExternal",
            new IfcBoolean(false));
        var loadBearing = new IfcPropertySingleValue(db, "LoadBearing",
            new IfcBoolean(true));
        var pset = new IfcPropertySet("Pset_ColumnCommon",
            new IfcProperty[] { isExternal, loadBearing });
        _ = new IfcRelDefinesByProperties(ifcColumn, pset);
    }
}
