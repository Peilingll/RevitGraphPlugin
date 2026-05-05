namespace RevitGraphPlugin.Sync;

/// <summary>
/// Stage 4 scope tag for Revit elements we know how to incrementally sync.
/// Stage 5+ may add Door / Roof / etc.; for now the deletion handler needs
/// to know which cascade pattern to apply.
/// </summary>
public enum ElementKind
{
    Wall,
    Window,
}
