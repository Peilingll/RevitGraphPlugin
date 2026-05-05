namespace RevitGraphPlugin.Graph;

/// <summary>
/// One <c>[:rel {rel_type, list_index}]</c> edge — ConMan2's polymorphic edge type
/// (related-work.md §2.2). <paramref name="ListIndex"/> is <c>-1</c> when the
/// source attribute is a singleton, otherwise the position within the source list.
/// </summary>
public sealed record GraphEdge(
    int FromP21Id,
    int ToP21Id,
    string RelType,
    int ListIndex);
