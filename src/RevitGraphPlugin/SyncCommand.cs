using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitGraphPlugin.Conversion;
using RevitGraphPlugin.Cypher;
using RevitGraphPlugin.Mapping;

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

        var connector = RevitGraphApp.Connector;
        if (connector is null)
        {
            message = "Neo4j connector not initialised.";
            return Result.Failed;
        }

        // Phase A — convert Revit elements to an IFC database (on UI thread,
        // because Revit API requires it).
        RevitToIfcExporter.Result export;
        try
        {
            export = new RevitToIfcExporter().Export(doc);
        }
        catch (Exception ex)
        {
            message = $"Revit→IFC export failed: {ex.Message}";
            return Result.Failed;
        }

        var batch = IfcGraphMapper.MapAll(export.Database);

        // Phase B — write to Neo4j off the UI thread, with a timeout (Stage 1
        // freeze fix carries over: never block the UI on the driver).
        var writeTask = Task.Run(async () =>
        {
            await Neo4jSchema.EnsureAsync(connector.Driver);
            await Neo4jGraphWriter.WriteAsync(connector.Driver, batch);
        });

        if (!writeTask.Wait(TimeSpan.FromSeconds(60)))
        {
            message = "Neo4j write timed out after 60s. Is the database running?";
            return Result.Failed;
        }
        if (writeTask.IsFaulted)
        {
            message = $"Neo4j write failed: {writeTask.Exception?.GetBaseException().Message}";
            return Result.Failed;
        }

        TaskDialog.Show("RevitGraphPlugin",
            $"Synced {export.WallCount} wall(s) and {export.WindowCount} window(s).\n" +
            $"Wrote {batch.Nodes.Count} nodes and {batch.Edges.Count} edges.");
        return Result.Succeeded;
    }
}
