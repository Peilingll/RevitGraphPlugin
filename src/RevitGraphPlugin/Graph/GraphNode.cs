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
/// Stage 5 — selects which Neo4j property is used as the MERGE key when
/// writing the node. Stable identifiers (Tag for IfcElement-derived, GlobalId
/// for IfcRelationship-derived) eliminate the p21_id-collision corruption
/// documented in <c>doc/known-issues.md</c>.
/// </summary>
public enum MergeStrategy
{
    /// <summary>Stage 2 fixture nodes, IfcInline, and Secondary fallback.</summary>
    ByP21Id,
    /// <summary>Primary IfcElement-derived nodes carrying a stable Revit UniqueId.</summary>
    ByTag,
    /// <summary>Connection / IfcRelationship nodes — GlobalId is fresh per export but unique within a write batch.</summary>
    ByGlobalId,
}

/// <summary>
/// One node of the in-memory graph batch produced by <c>IfcGraphMapper</c>.
/// Mirrors ConMan2's <c>GenericNode</c> shape; identity in Neo4j is determined
/// by <see cref="MergeStrategy"/> rather than always by <c>(p21_id, timestamp)</c>.
/// </summary>
public sealed record GraphNode(
    GraphNodeKind Kind,
    string EntityType,
    int P21Id,
    int Timestamp,
    string? GlobalId,
    IReadOnlyDictionary<string, object?> Properties)
{
    public string? Tag =>
        Properties.TryGetValue("Tag", out var v) && v is string s && s.Length > 0 ? s : null;

    public MergeStrategy MergeStrategy => (Kind, Tag, GlobalId) switch
    {
        (GraphNodeKind.Primary, { Length: > 0 }, _) => MergeStrategy.ByTag,
        (GraphNodeKind.Connection, _, { Length: > 0 }) => MergeStrategy.ByGlobalId,
        _ => MergeStrategy.ByP21Id,
    };
}
