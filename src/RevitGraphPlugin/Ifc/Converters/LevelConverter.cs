using Autodesk.Revit.DB;
using GeometryGym.Ifc;

namespace RevitGraphPlugin.Ifc.Converters;

/// <summary>
/// Revit <see cref="Level"/> → <see cref="IfcBuildingStorey"/>. Registered FIRST so a
/// batch that adds a level and elements on it converts the storey before the elements
/// look it up in <see cref="IfcModelContext.StoreyByLevel"/>.
///
/// Two paths create storeys:
/// <list type="bullet">
/// <item>the baseline: <c>BoilerplateBuilder</c> creates every storey up front (with
///   Revit's deduplicated Pset value layout) and tags storey + pset + rel with the level
///   id, so this converter is a no-op for levels that already have a storey;</item>
/// <item>live sync: a level added after the baseline arrives here and gets a storey of
///   its own, with its own Pset values (nothing to deduplicate against).</item>
/// </list>
/// Both produce the same GlobalIds (level UniqueId; pset / rel seeded by StableIds).
/// Modify and remove of a level are handled by <c>LiveSyncSession</c> directly — a
/// storey is never rebuilt (its containment rel and every contained element point at
/// it), its properties are updated in place.
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

    /// <summary>
    /// The storey entity itself, as the boilerplate builds it: aggregated under
    /// <paramref name="building"/> (ggifc wires the IfcRelAggregates, seeded stable),
    /// GlobalId from the level, Name / LongName / Elevation / ObjectType as Revit's
    /// exporter writes them. Psets are the caller's business (layouts differ).
    /// </summary>
    internal static IfcBuildingStorey CreateStorey(Level level, IfcBuilding building, Document doc)
    {
        var storey = new IfcBuildingStorey(building, level.Name, ElevationMm(level));
        storey.GlobalId = IfcGuidConverter.ForElement(level);
        storey.CompositionType = IfcElementCompositionEnum.ELEMENT;
        StableIds.StampAggregates(storey);
        UpdateStorey(storey, level, doc);
        return storey;
    }

    /// <summary>
    /// Copy the level's current values onto an existing storey (a modify never rebuilds
    /// the storey object — elements and the containment rel point at it).
    /// </summary>
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
