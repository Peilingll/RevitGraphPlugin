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
            TaskDialog.Show("RevitGraphPlugin", "Neo4j connector not initialised.");
            return Result.Failed;
        }

        // Driver calls can block; never run them on Revit's UI thread.
        var task = Task.Run(async () => await connector.Driver.VerifyConnectivityAsync());
        try
        {
            if (!task.Wait(TimeSpan.FromSeconds(10)))
            {
                TaskDialog.Show("RevitGraphPlugin", "Neo4j connectivity check timed out after 10 s.");
                return Result.Failed;
            }
        }
        catch (Exception ex)
        {
            // Task.Wait rethrows the task's exception wrapped in AggregateException;
            // GetBaseException unwraps to the original driver error (auth / connection / etc.).
            TaskDialog.Show("RevitGraphPlugin",
                $"Neo4j connectivity failed:\n{ex.GetBaseException().Message}");
            return Result.Failed;
        }

        TaskDialog.Show("RevitGraphPlugin", "Neo4j connection OK.");
        return Result.Succeeded;
    }
}
