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
        new BeamConverter(),
        new RoofConverter(),
        new CeilingConverter(),
        new WindowConverter(),
        new DoorConverter(),
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
                ConvertOne(converter, element, ctx);
        }
    }

    /// <summary>
    /// Convert one element, recording ownership of every ggifc entity the converter
    /// creates: StepIds are allocated monotonically, so the ids minted during the call
    /// are exactly (before, after] between two <see cref="StepIdWatermark"/> reads.
    /// The tags feed <see cref="IfcModelContext.OwnerByStepId"/> → the graph's
    /// <c>revit_element_id</c> property, which incremental sync uses to locate an
    /// element's graphlet (remove/replace without touching shared boilerplate).
    /// </summary>
    private static void ConvertOne(IElementConverter converter, Element element, IfcModelContext ctx)
    {
        var before = StepIdWatermark.Current(ctx.Db);
        converter.Convert(element, ctx);
        var after = StepIdWatermark.Current(ctx.Db);

        for (var stepId = before + 1; stepId <= after; stepId++)
            ctx.OwnerByStepId[stepId] = element.Id.Value;
    }
}
