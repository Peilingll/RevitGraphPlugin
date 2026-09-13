using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;

namespace RevitGraphPlugin;

/// <summary>
/// Holds the single active <see cref="LiveSyncSession"/> and routes DocumentChanged
/// into it. Any failure disposes the session and turns live sync off: stop writing
/// rather than desync silently.
/// </summary>
public static class LiveSyncManager
{
    private static LiveSyncSession? _session;

    /// <summary>The ribbon toggle button; text is updated to reflect state.</summary>
    internal static PushButton? ToggleButton { get; set; }

    /// <summary>Enable (baseline snapshot) or disable live sync for <paramref name="doc"/>; returns a status message.</summary>
    public static string Toggle(Document doc)
    {
        if (_session is not null)
        {
            _session.Dispose();
            _session = null;
            UpdateButton();
            LiveSyncLog.Write("Toggle OFF");
            return "Live sync OFF.";
        }

        LiveSyncLog.Write($"Toggle ON: baselining doc='{doc.Title}'");
        var session = LiveSyncSession.Start(doc);
        _session = session;
        UpdateButton();
        LiveSyncLog.Write($"Toggle ON done: primary={session.BaselineStats.PrimaryNodes} "
            + $"edges={session.BaselineStats.Edges}, sessionDoc='{session.Document.Title}'");
        var s = session.BaselineStats;
        return "Live sync ON — baseline written.\n\n"
             + $"Timestamp: {LiveSyncSession.Timestamp}\n"
             + $"Primary: {s.PrimaryNodes}   Connection: {s.ConnectionNodes}\n"
             + $"Secondary: {s.SecondaryNodes}   Inline: {s.InlineNodes}\n"
             + $"Edges: {s.Edges}";
    }

    public static void OnDocumentChanged(object? sender, DocumentChangedEventArgs e)
    {
        if (_session is null) return;
        var doc = e.GetDocument();

        var added = e.GetAddedElementIds();
        var deleted = e.GetDeletedElementIds();
        var modified = e.GetModifiedElementIds();
        LiveSyncLog.Write(
            $"DocumentChanged: doc='{doc.Title}' op={e.Operation} refMatch={ReferenceEquals(doc, _session.Document)} "
            + $"eqMatch={doc.Equals(_session.Document)} "
            + $"added={added.Count} deleted={deleted.Count} modified={modified.Count}");

        // Match by document equality (Revit does not guarantee the same managed Document
        // reference between ActiveUIDocument.Document and the event's GetDocument()).
        if (!doc.Equals(_session.Document))
        {
            LiveSyncLog.Write("  -> skipped: document does not match the live session");
            return;
        }

        try
        {
            // Deletes first (a delete + re-add of the same id in one transaction must keep
            // the fresh graphlet); levels last (Revit deletes a level's elements with it).
            foreach (var id in deleted.OrderBy(id => _session.IsLevel(id) ? 1 : 0))
                LiveSyncLog.Write($"  remove {id.Value}: applied={_session.ApplyRemoved(id)}");

            // Log every candidate so "unsupported" is distinguishable from "failed".
            foreach (var el in added.Select(doc.GetElement).Where(el => el is not null))
                LiveSyncLog.Write($"  added candidate {el.Id.Value}: category='{el.Category?.Name}' "
                    + $"builtin={(el.Category is null ? "null" : el.Category.BuiltInCategory.ToString())} "
                    + $"supported={_session.Supports(el)}");

            foreach (var element in SupportedByPriority(doc, added))
                LiveSyncLog.Write(
                    $"  add {element.Id.Value} ({element.Category?.Name}): applied={_session.ApplyAdded(element)}");

            // Hosts before hosted: a window's converter wires its opening into the host
            // wall's (rebuilt) graphlet.
            foreach (var element in SupportedByPriority(doc, ExpandTypesToInstances(doc, modified)))
                LiveSyncLog.Write(
                    $"  modify {element.Id.Value} ({element.Category?.Name}): applied={_session.ApplyModified(element)}");

            // Undo / redo / rollback carry no ids: roll-call on every event, full
            // re-conversion only when the operation was not a plain commit.
            var gone = _session.ReconcileVanished();
            if (gone.Count > 0)
                LiveSyncLog.Write($"  reconcile: {gone.Count} tracked element(s) no longer in the document, removed: {string.Join(",", gone.Select(id => id.Value))}");
            if (e.Operation != UndoOperation.TransactionCommitted)
            {
                var (modified2, inserted) = _session.ReconcileAll();
                LiveSyncLog.Write($"  reconcile after {e.Operation}: re-converted {modified2}, inserted {inserted}");
            }
        }
        catch (Exception ex)
        {
            // Revit forbids UI inside DocumentChanged: log, disable live sync, flip the button.
            LiveSyncLog.Write($"  !! FAILED: {ex}");
            _session.Dispose();
            _session = null;
            UpdateButton();
        }
    }

    /// <summary>Closing the mirrored document ends its session.</summary>
    public static void OnDocumentClosing(object? sender, DocumentClosingEventArgs e)
    {
        if (_session is null || !ReferenceEquals(e.Document, _session.Document)) return;
        _session.Dispose();
        _session = null;
        UpdateButton();
    }

    public static void Shutdown()
    {
        _session?.Dispose();
        _session = null;
    }

    /// <summary>
    /// A modified ElementType is reported without its instances, yet their converted
    /// output depends on it (e.g. WallType Function → IsExternal). Replace each type in
    /// <paramref name="ids"/> by the instances using it; the type itself never converts.
    /// </summary>
    private static ICollection<ElementId> ExpandTypesToInstances(Document doc, ICollection<ElementId> ids)
    {
        var expanded = new HashSet<ElementId>(ids);
        foreach (var type in ids.Select(doc.GetElement).OfType<ElementType>())
        {
            if (type.Category is null) continue;
            var instances = new FilteredElementCollector(doc)
                .OfCategoryId(type.Category.Id)
                .WhereElementIsNotElementType()
                .Where(el => el.GetTypeId() == type.Id)
                .ToList();
            LiveSyncLog.Write(
                $"  type {type.Id.Value} ({type.Category.Name} '{type.Name}') -> {instances.Count} instance(s)");
            foreach (var el in instances) expanded.Add(el.Id);
        }
        return expanded;
    }

    private static IEnumerable<Element> SupportedByPriority(Document doc, ICollection<ElementId> ids)
    {
        // Types never convert (a converter's instance cast would early-return and log a bogus success).
        return ids
            .Select(doc.GetElement)
            .Where(el => el is not null and not ElementType && _session!.Supports(el))
            .OrderBy(el => _session!.Priority(el));
    }

    private static void UpdateButton()
    {
        if (ToggleButton is not null)
            ToggleButton.ItemText = _session is null ? "Live Sync\nOFF" : "Live Sync\nON";
    }
}
