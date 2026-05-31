using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using GeometryGym.Ifc;
using RevitGraphPlugin.Cypher;
using RevitGraphPlugin.Ifc;

namespace RevitGraphPlugin;

[Transaction(TransactionMode.ReadOnly)]
public class SyncCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var doc = commandData.Application.ActiveUIDocument?.Document;
        if (doc is null)
        {
            TaskDialog.Show("RevitGraphPlugin", "No active document.");
            return Result.Cancelled;
        }

        // Phase A — build the in-memory IFC tree on the UI thread (Revit API access).
        DatabaseIfc db;
        try
        {
            db = BoilerplateBuilder.Build(doc);
        }
        catch (Exception ex)
        {
            TaskDialog.Show("RevitGraphPlugin",
                $"Failed to build IFC boilerplate:\n{ex.GetBaseException().Message}");
            return Result.Failed;
        }

        // Phase B — hand the ggifc tree off to the Python bridge: it serialises the
        // tree to STEP, spawns snippet_to_cypher.py which parses via ifcopenshell and
        // emits Cypher following ConMan2 schema rules. See
        // doc_process/2026-05-29-architecture-revisit-ifc-snippets.md.
        IfcSnippetSink.SinkResult result;
        try
        {
            result = IfcSnippetSink.Run(db, action: "CREATE", timestamp: "plugin-2");
        }
        catch (Exception ex)
        {
            TaskDialog.Show("RevitGraphPlugin",
                $"IfcSnippetSink failed:\n{ex.GetBaseException().Message}");
            return Result.Failed;
        }

        TaskDialog.Show("RevitGraphPlugin",
            $"Sync done via Python bridge.\n\n" +
            $"Temp IFC: {result.TempIfcPath}\n\n" +
            $"--- script output ---\n{result.Stdout}");
        return Result.Succeeded;
    }
}
