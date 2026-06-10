using Autodesk.Revit.DB;

namespace RevitGraphPlugin.Ifc.Converters;

/// <summary>
/// Converts one Revit element category into its IFC sub-graph, added to the
/// shared <see cref="IfcModelContext.Db"/>. Add a new implementation per element
/// type to extend coverage — the "dedicated library" 
/// </summary>
public interface IElementConverter
{
    /// <summary>The Revit category this converter handles.</summary>
    BuiltInCategory Category { get; }

    /// <summary>Build the IFC sub-graph for one element into <paramref name="ctx"/>.</summary>
    void Convert(Element element, IfcModelContext ctx);
}
