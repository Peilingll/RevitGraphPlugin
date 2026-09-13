using Autodesk.Revit.DB;
using RevitGraphPlugin.Ifc.Converters;

namespace RevitGraphPlugin.Ifc;

/// <summary>
/// Phase A of every sync: build the in-memory ggifc tree from the Revit document —
/// boilerplate skeleton, then every element through its converter.
/// </summary>
public static class ModelAssembler
{
    public static IfcModelContext Build(Document doc)
    {
        var ctx = BoilerplateBuilder.Build(doc);
        new ElementConverterRegistry().ConvertAll(doc, ctx);
        return ctx;
    }
}
