using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitGraphPlugin;

[Transaction(TransactionMode.ReadOnly)]
public class SyncCommand : IExternalCommand
{
    private const string MergeProjectCypher = @"
        MERGE (p:Project {RevitProjectId: $id})
        SET p.Name = $name,
            p.Number = $number,
            p.timestamp = 0";

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

        var info = doc.ProjectInformation;
        var revitProjectId = !string.IsNullOrEmpty(info?.UniqueId)
            ? info.UniqueId
            : doc.PathName;
        var name = info?.Name ?? "(unnamed)";
        var number = info?.Number ?? "";

        try
        {
            WriteProjectNodeAsync(connector.Driver, revitProjectId, name, number)
                .GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            message = $"Neo4j write failed: {ex.Message}";
            return Result.Failed;
        }

        TaskDialog.Show("RevitGraphPlugin", $"Project '{name}' synced to Neo4j.");
        return Result.Succeeded;
    }

    private static async Task WriteProjectNodeAsync(
        Neo4j.Driver.IDriver driver, string id, string name, string number)
    {
        await using var session = driver.AsyncSession();
        await session.ExecuteWriteAsync(async tx =>
        {
            var cursor = await tx.RunAsync(
                MergeProjectCypher,
                new { id, name, number });
            return await cursor.ConsumeAsync();
        });
    }
}
