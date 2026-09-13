using System.Collections.Generic;
using Autodesk.Revit.DB;
using GeometryGym.Ifc;

namespace RevitGraphPlugin.Ifc.Converters;

/// <summary>
/// Dispatches Revit elements to their <see cref="IElementConverter"/> and tags every
/// entity a conversion creates with its Revit element id. Register new converters in
/// <see cref="_converters"/>; order matters (hosts before hosted).
/// </summary>
public sealed class ElementConverterRegistry
{
    private readonly IReadOnlyList<IElementConverter> _converters = new IElementConverter[]
    {
        new LevelConverter(),        // storeys before anything that is contained in one
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

    /// <summary>Every category a registered converter handles.</summary>
    public IEnumerable<BuiltInCategory> Categories => _converters.Select(c => c.Category);

    /// <summary>Conversion order of a category (registration index; hosts before hosted). Unsupported categories sort last.</summary>
    public int ConversionPriority(BuiltInCategory category)
    {
        for (var i = 0; i < _converters.Count; i++)
            if (_converters[i].Category == category) return i;
        return int.MaxValue;
    }

    /// <summary>Convert one element (the live path's entry point). False if no converter covers its category.</summary>
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
    /// Convert one element and record ownership of every entity it created (the StepIds
    /// in (before, after] around the call) in <see cref="IfcModelContext.OwnerByStepId"/>.
    /// </summary>
    private static void ConvertOne(IElementConverter converter, Element element, IfcModelContext ctx)
    {
        var before = StepIdWatermark.Current(ctx.Db);
        converter.Convert(element, ctx);
        var after = StepIdWatermark.Current(ctx.Db);

        for (var stepId = before + 1; stepId <= after; stepId++)
        {
            // Shared context (e.g. the storey's containment rel) belongs to no single element.
            if (ctx.Db[stepId] is { } created
                && Cypher.GraphRule.SharedResourceTypes.Contains(created.GetType().Name))
                continue;

            ctx.OwnerByStepId[stepId] = element.Id.Value;
        }

        // Register the principal product (matched by GlobalId) so the live path can find
        // it for modify / remove and hosted elements can find their host.
        string? guid = null;
        try { guid = IfcGuidConverter.ForElement(element); }
        catch { /* no derivable GUID → leave unregistered */ }
        if (guid is not null && FindProduct(ctx.Db, before, after, guid) is { } product)
        {
            ctx.ConvertedElements[element.Id] = product;
            // One log line per element: graph GlobalId vs Revit's IfcGUID parameter.
            LiveSyncLog.Write($"    guid {element.Id.Value}: uid={element.UniqueId} -> {guid}");
        }
    }

    /// <summary>The IfcElement in (<paramref name="before"/>, <paramref name="after"/>] whose GlobalId is <paramref name="globalId"/> — the principal product. Null if none.</summary>
    public static IfcElement? FindProduct(DatabaseIfc db, int before, int after, string globalId)
    {
        for (var id = before + 1; id <= after; id++)
            if (db[id] is IfcElement e && e.GlobalId == globalId)
                return e;
        return null;
    }
}
