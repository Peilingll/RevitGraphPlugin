using Autodesk.Revit.DB;

namespace RevitGraphPlugin.Sync;

/// <summary>
/// Session-scoped map of <see cref="ElementId"/> → (Revit <c>UniqueId</c>,
/// <see cref="ElementKind"/>). Populated when an element is added or
/// modified; consulted on delete events because <c>doc.GetElement</c>
/// returns null for already-deleted ids.
/// </summary>
public sealed class ElementSyncState
{
    private readonly Dictionary<long, (string UniqueId, ElementKind Kind)> _byElementId = new();

    public void Record(ElementId id, string uniqueId, ElementKind kind) =>
        _byElementId[id.Value] = (uniqueId, kind);

    public bool TryResolve(ElementId id, out string uniqueId, out ElementKind kind)
    {
        if (_byElementId.TryGetValue(id.Value, out var entry))
        {
            uniqueId = entry.UniqueId;
            kind = entry.Kind;
            return true;
        }
        uniqueId = string.Empty;
        kind = default;
        return false;
    }

    public void Forget(ElementId id) => _byElementId.Remove(id.Value);

    public void Clear() => _byElementId.Clear();
}
