using System.Reflection;
using Autodesk.Revit.UI;

namespace RevitGraphPlugin;

public class RevitGraphApp : IExternalApplication
{
    private const string RibbonTab = "RevitGraphPlugin";
    private const string RibbonPanel = "Sync";

    internal static Neo4jConnector? Connector { get; private set; }

    public Result OnStartup(UIControlledApplication application)
    {
        try
        {
            Connector = Neo4jConnector.FromEnvironment();
        }
        catch (Exception ex)
        {
            TaskDialog.Show("RevitGraphPlugin", $"Neo4j init failed:\n{ex.Message}");
            return Result.Failed;
        }

        BuildRibbon(application);
        return Result.Succeeded;
    }

    public Result OnShutdown(UIControlledApplication application)
    {
        Connector?.Dispose();
        Connector = null;
        return Result.Succeeded;
    }

    private static void BuildRibbon(UIControlledApplication application)
    {
        try { application.CreateRibbonTab(RibbonTab); }
        catch (Autodesk.Revit.Exceptions.ArgumentException) { /* tab already exists */ }

        var panel = application.CreateRibbonPanel(RibbonTab, RibbonPanel);
        var assemblyPath = Assembly.GetExecutingAssembly().Location;

        var button = new PushButtonData(
            "SyncCurrentDocument",
            "Sync\ncurrent doc",
            assemblyPath,
            typeof(SyncCommand).FullName)
        {
            ToolTip = "Skeleton: verifies Neo4j connectivity. Sync logic to be implemented."
        };
        panel.AddItem(button);
    }
}
