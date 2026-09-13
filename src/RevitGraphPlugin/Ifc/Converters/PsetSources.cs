using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;

namespace RevitGraphPlugin.Ifc.Converters;

/// <summary>
/// The Revit parameters behind <c>Pset_*Common.IsExternal</c> / <c>LoadBearing</c>,
/// matched against Revit's own IFC exporter (tools/python/compare_psets.py).
/// </summary>
internal static class PsetSources
{
    /// <summary>The type's <c>Function</c> parameter: true for Exterior, false for Interior, null when absent or unset.</summary>
    public static bool? TypeFunctionIsExterior(Element element)
    {
        var type = element.Document.GetElement(element.GetTypeId()) as ElementType;
        var p = type?.get_Parameter(BuiltInParameter.FUNCTION_PARAM);
        if (p is null || !p.HasValue) return null;
        return p.AsInteger() == (int)WallFunction.Exterior;   // same enum coding for every type
    }

    /// <summary>Walls and floors: exterior iff the type's Function is Exterior.</summary>
    public static bool IsExternal(HostObject hostObject)
        => TypeFunctionIsExterior(hostObject) ?? false;

    /// <summary>Doors and windows: the type's own Function when defined, else the host wall's IsExternal, else internal.</summary>
    public static bool IsExternal(FamilyInstance insert)
    {
        if (TypeFunctionIsExterior(insert) is { } fromType) return fromType;
        return insert.Host is Wall wall && IsExternal(wall);
    }

    /// <summary>Walls: the instance's "Structural" checkbox.</summary>
    public static bool IsLoadBearing(Wall wall)
    {
        var p = wall.get_Parameter(BuiltInParameter.WALL_STRUCTURAL_SIGNIFICANT);
        return p is not null && p.AsInteger() == 1;
    }

    /// <summary>Floors: the instance's "Structural" checkbox.</summary>
    public static bool IsLoadBearing(Floor floor)
    {
        var p = floor.get_Parameter(BuiltInParameter.FLOOR_PARAM_IS_STRUCTURAL);
        return p is not null && p.AsInteger() == 1;
    }

    /// <summary>Columns: true for structural, null (not written) for architectural.</summary>
    public static bool? ColumnLoadBearing(FamilyInstance column)
        => column.StructuralType == StructuralType.Column ? true : null;
}
