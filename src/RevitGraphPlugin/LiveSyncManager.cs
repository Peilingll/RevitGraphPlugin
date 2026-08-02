using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;

namespace RevitGraphPlugin;

/// <summary>
/// Holds the (single) active <see cref="LiveSyncSession"/> and routes Revit events
/// into it (plan step 4). DocumentChanged fires once per committed transaction with
/// the added / deleted / modified element ids; each supported element becomes one
/// GraphRule applied to the live graph. On any failure the session is disposed and
/// live sync turns itself off (fail loud + stop writing rather than desync silently).
/// </summary>
public static class LiveSyncManager
{
    private static LiveSyncSession? _session;

    /// <summary>The ribbon toggle button; text is updated to reflect state.</summary>
    internal static PushButton? ToggleButton { get; set; }

    public static bool IsActive => _session is not null;

    /// <summary>
    /// Flip live sync for <paramref name="doc"/>. Enabling runs the baseline snapshot
    /// (wipe + rewrite of <see cref="LiveSyncSession.Timestamp"/>); disabling disposes
    /// the session. Returns a user-facing status message.
    /// </summary>
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
            $"DocumentChanged: doc='{doc.Title}' refMatch={ReferenceEquals(doc, _session.Document)} "
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
            // Deletions first: a delete+re-add of the same element id within one
            // transaction must not remove the fresh graphlet. Bodies are gone, so
            // ids are all we get — and all the session needs.
            foreach (var id in deleted)
                LiveSyncLog.Write($"  remove {id.Value}: applied={_session.ApplyRemoved(id)}");

            // Log every added element (category + supported) so an unsupported filter is
            // distinguishable from a supported-but-failed apply.
            foreach (var el in added.Select(doc.GetElement).Where(el => el is not null))
                LiveSyncLog.Write($"  added candidate {el.Id.Value}: category='{el.Category?.Name}' "
                    + $"builtin={(el.Category is null ? "null" : el.Category.BuiltInCategory.ToString())} "
                    + $"supported={_session.Supports(el)}");

            foreach (var element in SupportedByPriority(doc, added))
                LiveSyncLog.Write(
                    $"  add {element.Id.Value} ({element.Category?.Name}): applied={_session.ApplyAdded(element)}");

            // Hosts before hosted (walls before their windows/doors): when a moved
            // wall drags its windows along, the wall's graphlet must be rebuilt
            // before the window converter wires the opening back into it.
            foreach (var element in SupportedByPriority(doc, ExpandTypesToInstances(doc, modified)))
                LiveSyncLog.Write(
                    $"  modify {element.Id.Value} ({element.Category?.Name}): applied={_session.ApplyModified(element)}");
        }
        catch (Exception ex)
        {
            // Never show UI from a DocumentChanged handler (Revit forbids it). Log the
            // failure, disable live sync (the graph may now be stale), flip the button.
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
    /// A modified ElementType means "every instance using it changed": editing a type
    /// parameter (WallType Function → IsExternal, a type rename, a type thickness)
    /// reports only the TYPE as modified — the instances whose converted output depends
    /// on it are never mentioned, so without expansion the graph keeps their stale
    /// values. Each type in <paramref name="ids"/> is therefore replaced by the
    /// instances using it (the type itself is filtered out of conversion by
    /// <see cref="SupportedByPriority"/>). A set, because one transaction can report a
    /// type and some of its instances together — each instance must re-sync once.
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
        // Types never convert: a type reaching a converter would apply an empty rule
        // (the converter's instance-cast early-returns) and log a bogus success.
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
