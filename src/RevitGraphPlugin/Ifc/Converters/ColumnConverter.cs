using Autodesk.Revit.DB;
using GeometryGym.Ifc;
using RevitGraphPlugin.Ifc.Geometry;

namespace RevitGraphPlugin.Ifc.Converters;

/// <summary>
/// Column → IfcColumn: placement + BRep body + Pset_ColumnCommon + storey containment.
/// Not emitted: IfcColumnType (+ mapped geometry), materials, quantities, extrusion geometry.
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

        // Storey from FAMILY_BASE_LEVEL_PARAM (structural), else LevelId (architectural).
        var levelId = column.get_Parameter(BuiltInParameter.FAMILY_BASE_LEVEL_PARAM)?.AsElementId();
        if (levelId is null || levelId == ElementId.InvalidElementId)
            levelId = column.LevelId;
        if (levelId is null || levelId == ElementId.InvalidElementId ||
            !ctx.StoreyByLevel.TryGetValue(levelId, out var storey))
            return;   // column on a level we have no storey for — skip for now

        var db = ctx.Db;

        // Origin: LocationPoint.
        var origin = (column.Location as LocationPoint)?.Point ?? XYZ.Zero;

        var placement = new IfcLocalPlacement(
            storey.ObjectPlacement,
            new IfcAxis2Placement3D(new IfcCartesianPoint(
                db, BRepBodyBuilder.Mm(origin.X), BRepBodyBuilder.Mm(origin.Y), BRepBodyBuilder.Mm(origin.Z))));

        // host = storey → ggifc creates the containment rel.
        var ifcColumn = new IfcColumn(storey, placement, null);
        ifcColumn.GlobalId = IfcGuidConverter.ForElement(column);
        StableIds.StampContainment(ifcColumn);   // storey containment rel: stable GlobalId
        ifcColumn.PredefinedType = IfcColumnTypeEnum.COLUMN;

        var symbol = column.Symbol;                 // the column's FamilySymbol (type)
        var family = symbol?.FamilyName ?? "Column";
        var typeName = symbol?.Name ?? "Column";
        ifcColumn.Name = $"{family}:{typeName}:{column.Id.Value}";   // mirror native naming
        ifcColumn.ObjectType = $"{family}:{typeName}";
        ifcColumn.Tag = column.Id.Value.ToString();

        // BRep body (handles mapped family geometry).
        var shape = BRepBodyBuilder.Build(db, ctx.BodyContext, column, origin);
        if (shape is not null)
            ifcColumn.Representation = shape;

        AttachColumnCommonPset(db, ifcColumn, column);
    }

    /// <summary>Columns are internal; LoadBearing is written for structural columns only (matches native export).</summary>
    private static void AttachColumnCommonPset(DatabaseIfc db, IfcColumn ifcColumn, FamilyInstance column)
    {
        var props = new List<IfcProperty>
        {
            new IfcPropertySingleValue(db, "IsExternal", new IfcBoolean(false)),
        };
        if (PsetSources.ColumnLoadBearing(column) is { } loadBearing)
            props.Add(new IfcPropertySingleValue(db, "LoadBearing", new IfcBoolean(loadBearing)));
        StableIds.AttachPset(ifcColumn, "Pset_ColumnCommon", props.ToArray());
    }
}
