using Autodesk.Revit.DB;
using GeometryGym.Ifc;

namespace RevitGraphPlugin.Ifc.Converters;

/// <summary>
/// Revit <see cref="Level"/> → <see cref="IfcBuildingStorey"/>. Registered first so a
/// batch converts the storey before the elements on it. The baseline builds every storey
/// up front (<c>BoilerplateBuilder</c>), so this only handles levels added live; modify and
/// remove are handled in place by <c>LiveSyncSession</c> (a storey is never rebuilt).
/// </summary>
public sealed class LevelConverter : IElementConverter
{
    public BuiltInCategory Category => BuiltInCategory.OST_Levels;

    public void Convert(Element element, IfcModelContext ctx)
    {
        if (element is not Level level) return;
        if (ctx.StoreyByLevel.ContainsKey(level.Id)) return;   // baseline already built it

        var storey = CreateStorey(level, ctx.Building, level.Document);
        var levelType = level.Document.GetElement(level.GetTypeId());
        StableIds.AttachPset(storey, "Pset_BuildingStoreyCommon",
            new IfcPropertySingleValue(ctx.Db, "Reference",
                new IfcIdentifier(levelType?.Name ?? "Level")),
            new IfcPropertySingleValue(ctx.Db, "AboveGround",
                new IfcLogical(IfcLogicalEnum.UNKNOWN)));
        ctx.StoreyByLevel[level.Id] = storey;
    }

    /// <summary>The storey entity, aggregated under <paramref name="building"/>, attributes as Revit's exporter writes them. Psets are the caller's business.</summary>
    internal static IfcBuildingStorey CreateStorey(Level level, IfcBuilding building, Document doc)
    {
        var storey = new IfcBuildingStorey(building, level.Name, ElevationMm(level));
        storey.GlobalId = IfcGuidConverter.ForElement(level);
        storey.CompositionType = IfcElementCompositionEnum.ELEMENT;
        StableIds.StampAggregates(storey);
        UpdateStorey(storey, level, doc);
        return storey;
    }

    /// <summary>Copy the level's current values onto an existing storey.</summary>
    internal static void UpdateStorey(IfcBuildingStorey storey, Level level, Document doc)
    {
        storey.Name = level.Name;
        storey.LongName = level.Name;          // native mirrors Name into LongName
        storey.Elevation = ElevationMm(level);
        var levelType = doc.GetElement(level.GetTypeId());
        if (levelType != null)
            storey.ObjectType = "Level:" + levelType.Name;
    }

    private static double ElevationMm(Level level)
        => UnitUtils.ConvertFromInternalUnits(level.Elevation, UnitTypeId.Millimeters);
}
