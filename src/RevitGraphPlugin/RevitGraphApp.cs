using System.Reflection;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using RevitGraphPlugin.Sync;

namespace RevitGraphPlugin;

public class RevitGraphApp : IExternalApplication
{
    private const string RibbonTab = "RevitGraphPlugin";
    private const string RibbonPanel = "Sync";

    internal static Neo4jConnector? Connector { get; private set; }
    internal static ElementSyncState SyncState { get; } = new();
    private static IncrementalSync? _incremental;

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

        _incremental = new IncrementalSync(SyncState);
        application.ControlledApplication.DocumentChanged += OnDocumentChanged;
        BuildRibbon(application);
        return Result.Succeeded;
    }

    public Result OnShutdown(UIControlledApplication application)
    {
        application.ControlledApplication.DocumentChanged -= OnDocumentChanged;
        Connector?.Dispose();
        Connector = null;
        _incremental = null;
        SyncState.Clear();
        return Result.Succeeded;
    }

    private static void OnDocumentChanged(object? sender, DocumentChangedEventArgs e)
    {
        if (_incremental is null || Connector is null) return;
        try
        {
            _incremental.Handle(e, Connector.Driver);
        }
        catch
        {
            // DocumentChanged runs on the Revit UI thread; throwing here can
            // destabilise the document. Swallow until proper logging lands.
        }
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
            ToolTip = "Write a Project node for the active document into Neo4j."
        };
        panel.AddItem(button);
    }
}
