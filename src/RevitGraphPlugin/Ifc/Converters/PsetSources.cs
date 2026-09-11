using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;

namespace RevitGraphPlugin.Ifc.Converters;

/// <summary>
/// Where the values of <c>Pset_*Common.IsExternal</c> / <c>LoadBearing</c> come from in
/// Revit, matched against Revit's own IFC exporter on 2026-09-11 (a model with every
/// element type, exported natively, compared element by element with the graph — see
/// doc/log/2026-09-11_pset-sources.md). Kept in one place so every converter reads the
/// same parameters the exporter does.
/// </summary>
internal static class PsetSources
{
    /// <summary>
    /// The type-level <c>Function</c> parameter (Interior / Exterior) as the exporter reads
    /// it: <c>true</c> for Exterior, <c>false</c> for Interior, <c>null</c> when the type
    /// has no such parameter or it is unset. Walls, floors, and door / window families that
    /// define it carry it under Construction; window families typically do not.
    /// </summary>
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

    /// <summary>
    /// Doors and windows, in the exporter's order: the type's own Function parameter when
    /// the family defines it (an "ExtDbl" door family says Exterior regardless of the wall
    /// it sits in), otherwise the host wall's IsExternal, otherwise internal.
    /// </summary>
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

    /// <summary>
    /// Columns: the exporter writes <c>LoadBearing = true</c> for structural columns and
    /// omits the property for architectural ones (Pset_ColumnCommon has no LoadBearing on
    /// an architectural column in a native export). Null means "do not write it".
    /// </summary>
    public static bool? ColumnLoadBearing(FamilyInstance column)
        => column.StructuralType == StructuralType.Column ? true : null;
}
