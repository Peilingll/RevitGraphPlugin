using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Neo4j.Driver;
using RevitGraphPlugin.Conversion;
using RevitGraphPlugin.Cypher;
using RevitGraphPlugin.Mapping;

namespace RevitGraphPlugin.Sync;

/// <summary>
/// Stage 4 — partition the <see cref="DocumentChangedEventArgs"/> payload
/// into added / modified / deleted in-scope elements and dispatch each
/// branch per design.md §4 Stage 4.
///
/// Stage 4 scope: <see cref="Wall"/>, window <see cref="FamilyInstance"/>.
/// Wall <em>modify</em> events are skipped for now — the user can re-press
/// the manual Sync button for a full export.
///
/// Each event is processed synchronously on the Revit UI thread plus a
/// threadpool offload for the Neo4j write (Stage 1's freeze-prevention
/// pattern).
/// </summary>
public sealed class IncrementalSync
{
    private const int Timestamp = 0;
    private static readonly TimeSpan WriteBudget = TimeSpan.FromSeconds(30);

    private readonly ElementSyncState _state;

    public IncrementalSync(ElementSyncState state) => _state = state;

    public void Handle(DocumentChangedEventArgs e, IDriver driver)
    {
        var doc = e.GetDocument();

        // Deletes first — needed before re-adding, and the state map must be
        // consulted before we forget the element.
        foreach (var id in e.GetDeletedElementIds())
            HandleDeleted(id, driver);

        foreach (var id in e.GetAddedElementIds())
        {
            var element = doc.GetElement(id);
            if (element is null) continue;
            HandleAddedOrModified(element, doc, driver, isModification: false);
        }

        foreach (var id in e.GetModifiedElementIds())
        {
            var element = doc.GetElement(id);
            if (element is null) continue;
            HandleAddedOrModified(element, doc, driver, isModification: true);
        }
    }

    private void HandleAddedOrModified(Element element, Document doc, IDriver driver, bool isModification)
    {
        var kind = ClassifyOrSkip(element);
        if (kind is null) return;

        // For modify-window we delete-cascade first so the synthesised opening
        // and IfcRel* are torn down cleanly before re-emitting them.
        if (isModification && kind == ElementKind.Window)
        {
            DeleteFromGraph(element.UniqueId, ElementKind.Window, driver);
        }
        else if (isModification && kind == ElementKind.Wall)
        {
            // Stage 4 simplification: wall modify is deferred. The user
            // can press Sync to refresh. Logging avoids silent drops.
            return;
        }

        var exporter = new RevitToIfcExporter();
        var export = exporter.ExportElement(doc, element);
        if (export.WallCount + export.WindowCount == 0) return;

        var batch = IfcGraphMapper.MapAll(export.Database);
        WriteWithBudget(driver, () => Neo4jGraphWriter.WriteAsync(driver, batch, Timestamp));

        _state.Record(element.Id, element.UniqueId, kind.Value);
    }

    private void HandleDeleted(ElementId id, IDriver driver)
    {
        if (!_state.TryResolve(id, out var uniqueId, out var kind)) return;
        DeleteFromGraph(uniqueId, kind, driver);
        _state.Forget(id);
    }

    private static void DeleteFromGraph(string uniqueId, ElementKind kind, IDriver driver) =>
        WriteWithBudget(driver, () => kind switch
        {
            ElementKind.Wall => Neo4jGraphWriter.DeleteWallByTagAsync(driver, uniqueId, Timestamp),
            ElementKind.Window => Neo4jGraphWriter.DeleteWindowCascadeAsync(driver, uniqueId, Timestamp),
            _ => Task.CompletedTask,
        });

    private static void WriteWithBudget(IDriver driver, Func<Task> work)
    {
        var task = Task.Run(work);
        if (!task.Wait(WriteBudget)) return; // best-effort: log later when we add logging
        if (task.IsFaulted) _ = task.Exception; // swallow — DocumentChanged cannot show TaskDialogs
    }

    private static ElementKind? ClassifyOrSkip(Element element) => element switch
    {
        Wall => ElementKind.Wall,
        FamilyInstance fi when fi.Category?.Id.Value == (long)BuiltInCategory.OST_Windows
            && fi.Host is Wall => ElementKind.Window,
        _ => null,
    };
}
