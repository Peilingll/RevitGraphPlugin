using System.Reflection;
using Autodesk.Revit.UI;

namespace RevitGraphPlugin;

public class RevitGraphApp : IExternalApplication
{
    private const string RibbonTab = "RevitGraphPlugin";
    private const string RibbonPanel = "Sync";

    public Result OnStartup(UIControlledApplication application)
    {
        // Three buttons (see BuildRibbon): the temp-IFC bridge (SyncCommand), the
        // direct C# write (SyncDirectCommand), and the live incremental toggle
        // (LiveSyncToggleCommand). Drivers/processes are opened on demand.
        BuildRibbon(application);

        // Live incremental sync (plan 子步驟 4): DocumentChanged fires per committed
        // transaction; the manager no-ops unless a session is active for that document.
        application.ControlledApplication.DocumentChanged += LiveSyncManager.OnDocumentChanged;
        application.ControlledApplication.DocumentClosing += LiveSyncManager.OnDocumentClosing;
        return Result.Succeeded;
    }

    public Result OnShutdown(UIControlledApplication application)
    {
        application.ControlledApplication.DocumentChanged -= LiveSyncManager.OnDocumentChanged;
        application.ControlledApplication.DocumentClosing -= LiveSyncManager.OnDocumentClosing;
        LiveSyncManager.Shutdown();
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

        var liveButton = new PushButtonData(
            "LiveSyncToggle",
            "Live Sync\nOFF",
            assemblyPath,
            typeof(LiveSyncToggleCommand).FullName)
        {
            ToolTip = "Toggle LIVE incremental sync (direct pipeline): ON writes a "
                    + "baseline snapshot, then every committed change (add / modify / "
                    + "delete of supported elements) updates the graph immediately. "
                    + "Timestamp: plugin-live."
        };

        panel.AddItem(bridgeButton);
        panel.AddItem(directButton);
        LiveSyncManager.ToggleButton = panel.AddItem(liveButton) as PushButton;
    }
}
