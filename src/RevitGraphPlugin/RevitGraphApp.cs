using System.Reflection;
using Autodesk.Revit.UI;

namespace RevitGraphPlugin;

public class RevitGraphApp : IExternalApplication
{
    private const string RibbonTab = "RevitGraphPlugin";
    private const string RibbonPanel = "Sync";

    public Result OnStartup(UIControlledApplication application)
    {
        // Two sinks, two buttons (see BuildRibbon): the temp-IFC bridge (SyncCommand)
        // and the direct C# write (SyncDirectCommand). Drivers/processes are opened on
        // demand inside each command, not here.
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

        var bridgeButton = new PushButtonData(
            "SyncBridge",
            "Sync\n(bridge)",
            assemblyPath,
            typeof(SyncCommand).FullName)
        {
            ToolTip = "Write the current model to Neo4j via the TEMP-IFC bridge "
                    + "(ggifc → .ifc → ConMan2). Timestamp: plugin-bridge."
        };

        var directButton = new PushButtonData(
            "SyncDirect",
            "Sync\n(direct)",
            assemblyPath,
            typeof(SyncDirectCommand).FullName)
        {
            ToolTip = "Write the current model to Neo4j directly (no temp IFC) via "
                    + "CypherEmitter. Timestamp: plugin-direct."
        };

        panel.AddItem(bridgeButton);
        panel.AddItem(directButton);
    }
}
