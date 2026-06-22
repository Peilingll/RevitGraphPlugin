using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace RevitGraphPlugin.Ifc.Converters;

/// <summary>
/// Dispatches Revit elements to the matching <see cref="IElementConverter"/>.
/// Register new converters in <see cref="_converters"/> to extend element-type
/// coverage (stage 2 of the professor's two-stage methodology).
/// </summary>
public sealed class ElementConverterRegistry
{
    private readonly IReadOnlyList<IElementConverter> _converters = new IElementConverter[]
    {
        new WallConverter(),
        new FloorConverter(),
        new ColumnConverter(BuiltInCategory.OST_StructuralColumns),
        new ColumnConverter(BuiltInCategory.OST_Columns),
        new WindowConverter(),
    };

    /// <summary>Run every registered converter over the model's elements.</summary>
    public void ConvertAll(Document doc, IfcModelContext ctx)
    {
        foreach (var converter in _converters)
        {
            var elements = new FilteredElementCollector(doc)
                .OfCategory(converter.Category)
                .WhereElementIsNotElementType()
                .ToElements();

            foreach (var element in elements)
                converter.Convert(element, ctx);
        }
    }
}
