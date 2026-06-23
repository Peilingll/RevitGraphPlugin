using Autodesk.Revit.DB;
using RevitGraphPlugin.Ifc.Converters;

namespace RevitGraphPlugin.Ifc;

/// <summary>
/// Phase A of a sync: assemble the in-memory IFC model (ggifc entity tree) from the
/// Revit document — boilerplate skeleton (stage 1) then per-element converters
/// (stage 2). Shared by BOTH sinks so the IFC tree is built identically regardless
/// of how it is later written to Neo4j:
///   - <see cref="SyncCommand"/>       → temp-IFC bridge (Cypher/IfcSnippetSink)
///   - <see cref="SyncDirectCommand"/> → direct write   (Cypher/Direct/CypherEmitter)
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
