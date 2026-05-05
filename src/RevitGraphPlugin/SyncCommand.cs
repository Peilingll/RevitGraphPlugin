using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitGraphPlugin.Conversion;
using RevitGraphPlugin.Cypher;
using RevitGraphPlugin.Mapping;
using RevitGraphPlugin.Sync;

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

        // Populate the incremental-sync state map so subsequent
        // DocumentChanged delete events can resolve ElementId → UniqueId/Kind.
        RevitGraphApp.SyncState.Clear();
        foreach (var wall in new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Wall>())
            RevitGraphApp.SyncState.Record(wall.Id, wall.UniqueId, ElementKind.Wall);
        foreach (var window in new FilteredElementCollector(doc)
            .OfCategory(BuiltInCategory.OST_Windows).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>())
            RevitGraphApp.SyncState.Record(window.Id, window.UniqueId, ElementKind.Window);

        TaskDialog.Show("RevitGraphPlugin",
            $"Synced {export.WallCount} wall(s) and {export.WindowCount} window(s).\n" +
            $"Wrote {batch.Nodes.Count} nodes and {batch.Edges.Count} edges.");
        return Result.Succeeded;
    }
}
