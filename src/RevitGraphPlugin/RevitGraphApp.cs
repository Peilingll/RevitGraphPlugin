using Autodesk.Revit.UI;

namespace RevitGraphPlugin;

public class RevitGraphApp : IExternalApplication
{
    public Result OnStartup(UIControlledApplication application) => Result.Succeeded;

    public Result OnShutdown(UIControlledApplication application) => Result.Succeeded;
}
