using System.Collections.Generic;
using Autodesk.Revit.DB;
using GeometryGym.Ifc;

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

    /// <summary>True if some registered converter handles this category.</summary>
    public bool Supports(BuiltInCategory category)
        => _converters.Any(c => c.Category == category);

    /// <summary>
    /// Conversion order of a category — its converter's registration index. The
    /// registration order intentionally puts hosts before hosted elements (walls
    /// before windows/doors), and the live path must respect the same order when a
    /// batch of changes arrives (a window's converter wires back to its host wall
    /// via ConvertedElements). Unsupported categories sort last.
    /// </summary>
    public int ConversionPriority(BuiltInCategory category)
    {
        for (var i = 0; i < _converters.Count; i++)
            if (_converters[i].Category == category) return i;
        return int.MaxValue;
    }

    /// <summary>
    /// Convert a single element through its category's converter (ownership-tagged,
    /// same as the full pass) — the live incremental path's entry point. Returns
    /// false when no converter covers the element's category.
    /// </summary>
    public bool TryConvertOne(Element element, IfcModelContext ctx)
    {
        if (element.Category is null) return false;
        var category = element.Category.BuiltInCategory;
        var converter = _converters.FirstOrDefault(c => c.Category == category);
        if (converter is null) return false;

        ConvertOne(converter, element, ctx);
        return true;
    }

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
        {
            // Shared context entities (e.g. the storey's containment rel, which ggifc
            // creates while converting the FIRST element on that storey) belong to no
            // single element — removal of their creator must not tear them out.
            if (ctx.Db[stepId] is { } created
                && Cypher.GraphRule.SharedResourceTypes.Contains(created.GetType().Name))
                continue;

            ctx.OwnerByStepId[stepId] = element.Id.Value;
        }

        // Register the element's principal IFC product so the live path can find it for
        // modify (Replace vs Insert), remove (ggifc-side detach), and hosted-element
        // host lookup. Every converter sets product.GlobalId = FromRevitUniqueId(UniqueId),
        // so match on that — one place, so a new converter cannot forget to register
        // (the missing registration made every non-wall modify duplicate instead of replace).
        string? guid = null;
        try { guid = IfcGuidConverter.FromRevitUniqueId(element.UniqueId); }
        catch { /* no derivable GUID → leave unregistered */ }
        if (guid is not null && FindProduct(ctx.Db, before, after, guid) is { } product)
            ctx.ConvertedElements[element.Id] = product;
    }

    /// <summary>
    /// The IfcElement created in the watermark range (<paramref name="before"/>,
    /// <paramref name="after"/>] whose GlobalId matches <paramref name="globalId"/> —
    /// i.e. the Revit element's principal IFC product (not its placement, geometry, or
    /// opening). Null if the converter produced no such element.
    /// </summary>
    public static IfcElement? FindProduct(DatabaseIfc db, int before, int after, string globalId)
    {
        for (var id = before + 1; id <= after; id++)
            if (db[id] is IfcElement e && e.GlobalId == globalId)
                return e;
        return null;
    }
}
