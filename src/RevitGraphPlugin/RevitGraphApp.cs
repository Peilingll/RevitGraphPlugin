using System.Reflection;
using Autodesk.Revit.UI;

namespace RevitGraphPlugin;

public class RevitGraphApp : IExternalApplication
{
    private const string RibbonTab = "RevitGraphPlugin";
    private const string RibbonPanel = "Sync";

    public Result OnStartup(UIControlledApplication application)
    {
        // Neo4j is written by the Python bridge (ConMan2's Neo4jConnection), so the
        // plugin no longer opens a C# driver here. SyncCommand spawns the bridge on
        // demand. See doc_process/2026-05-29-architecture-revisit-ifc-snippets.md.
        BuildRibbon(application);
        return Result.Succeeded;
    }

    public Result OnShutdown(UIControlledApplication application)
    {
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
