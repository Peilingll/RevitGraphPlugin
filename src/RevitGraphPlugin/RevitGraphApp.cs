using System.Reflection;
using Autodesk.Revit.DB.Events;
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

        application.ControlledApplication.DocumentChanged += OnDocumentChanged;
        BuildRibbon(application);
        return Result.Succeeded;
    }

    public Result OnShutdown(UIControlledApplication application)
    {
        application.ControlledApplication.DocumentChanged -= OnDocumentChanged;
        Connector?.Dispose();
        Connector = null;
        return Result.Succeeded;
    }

    private static void OnDocumentChanged(object? sender, DocumentChangedEventArgs e)
    {
        // Stage 4 will partition added / modified / deleted ElementIds here.
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
