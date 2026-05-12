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

        var connector = RevitGraphApp.Connector;
        if (connector is null)
        {
            TaskDialog.Show("RevitGraphPlugin", "Neo4j connector not initialised.");
            return Result.Failed;
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

        // Phase B — emit Cypher off the UI thread (driver calls can block).
        var task = Task.Run(async () => await CypherEmitter.WriteAsync(connector.Driver, db));
        try
        {
            if (!task.Wait(TimeSpan.FromSeconds(60)))
            {
                TaskDialog.Show("RevitGraphPlugin", "Neo4j write timed out after 60 s.");
                return Result.Failed;
            }
        }
        catch (Exception ex)
        {
            TaskDialog.Show("RevitGraphPlugin",
                $"Neo4j write failed:\n{ex.GetBaseException().Message}");
            return Result.Failed;
        }

        var stats = task.Result;
        TaskDialog.Show("RevitGraphPlugin",
            $"Wrote empty-project boilerplate to Neo4j.\n\n" +
            $"Primary nodes:    {stats.PrimaryNodes}\n" +
            $"Connection nodes: {stats.ConnectionNodes}\n" +
            $"Secondary nodes:  {stats.SecondaryNodes}\n" +
            $"Edges:            {stats.Edges}");
        return Result.Succeeded;
    }
}
