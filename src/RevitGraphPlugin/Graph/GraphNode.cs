namespace RevitGraphPlugin.Graph;

/// <summary>
/// Node taxonomy from ConMan2 (related-work.md §2.1):
/// PrimaryNode    — IfcObjectDefinition / IfcPropertyDefinition (carries GlobalId)
/// SecondaryNode  — other IFC entities (no GlobalId)
/// ConnectionNode — IfcRelationship (carries GlobalId)
/// InlineNode     — STEP entities with id == 0 (anonymous inline values)
/// </summary>
public enum GraphNodeKind
{
    Primary,
    Secondary,
    Connection,
    Inline,
}

/// <summary>
/// One node of the in-memory graph batch produced by <c>IfcGraphMapper</c>.
/// Mirrors ConMan2's <c>GenericNode</c> shape; <c>EntityType</c> + <c>P21Id</c> + <c>Timestamp</c>
/// uniquely identify the node.
/// </summary>
public sealed record GraphNode(
    GraphNodeKind Kind,
    string EntityType,
    int P21Id,
    int Timestamp,
    string? GlobalId,
    IReadOnlyDictionary<string, object?> Properties);
