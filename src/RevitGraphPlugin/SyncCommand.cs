using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitGraphPlugin;

[Transaction(TransactionMode.ReadOnly)]
public class SyncCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var connector = RevitGraphApp.Connector;
        if (connector is null)
        {
            message = "Neo4j connector not initialised.";
            return Result.Failed;
        }

        // Driver calls can block; never run them on Revit's UI thread.
        var task = Task.Run(async () => await connector.Driver.VerifyConnectivityAsync());
        if (!task.Wait(TimeSpan.FromSeconds(10)))
        {
            message = "Neo4j connectivity check timed out after 10s.";
            return Result.Failed;
        }
        if (task.IsFaulted)
        {
            message = $"Neo4j connectivity failed: {task.Exception?.GetBaseException().Message}";
            return Result.Failed;
        }

        TaskDialog.Show("RevitGraphPlugin", "Neo4j connection OK.");
        return Result.Succeeded;
    }
}
