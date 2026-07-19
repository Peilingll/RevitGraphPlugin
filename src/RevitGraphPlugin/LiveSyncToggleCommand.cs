using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitGraphPlugin;

/// <summary>
/// Ribbon toggle for live incremental sync (direct pipeline): ON runs the baseline
/// snapshot and starts mirroring every committed change; OFF stops the session.
/// </summary>
[Transaction(TransactionMode.ReadOnly)]
public class LiveSyncToggleCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var doc = commandData.Application.ActiveUIDocument?.Document;
        if (doc is null)
        {
            TaskDialog.Show("RevitGraphPlugin", "No active document.");
            return Result.Cancelled;
        }

        try
        {
            var status = LiveSyncManager.Toggle(doc);
            TaskDialog.Show("RevitGraphPlugin", status);
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            TaskDialog.Show("RevitGraphPlugin",
                $"Live sync failed to start:\n{ex.GetBaseException().Message}");
            return Result.Failed;
        }
    }
}
