using GeometryGym.Ifc;

// ── Pipeline: DIRECT-WRITE (Revit → ggifc tree → Cypher → Neo4j; no temp IFC) ──
// Opt-in. Default sink is the temp-IFC bridge (Cypher/IfcSnippetSink.cs).
namespace RevitGraphPlugin.Cypher;

/// <summary>
/// ConMan2's node-kind classification:
/// - PrimaryNode    : subclass of IfcObjectDefinition or IfcPropertyDefinition
/// - ConnectionNode : subclass of IfcRelationship
/// - SecondaryNode  : all other STEP entities (id != 0)
/// - InlineNode     : STEP entities with id == 0 (wrapped value types)
/// </summary>
public enum NodeKind
{
    Primary,
    Connection,
    Secondary,
    Inline,
}

public static class NodeClassifier
{
    public static NodeKind Classify(BaseClassIfc entity)
    {
        if (entity.StepId == 0) return NodeKind.Inline;
        if (entity is IfcObjectDefinition or IfcPropertyDefinition) return NodeKind.Primary;
        if (entity is IfcRelationship) return NodeKind.Connection;
        return NodeKind.Secondary;
    }

    public static string LabelExpression(NodeKind kind) => kind switch
    {
        NodeKind.Primary    => "PrimaryNode:GenericNode:Node",
        NodeKind.Connection => "ConnectionNode:GenericNode:Node",
        NodeKind.Secondary  => "SecondaryNode:GenericNode:Node",
        NodeKind.Inline     => "InlineNode:Node",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    /// <summary>
    /// Inverse of <see cref="LabelExpression"/>: recover the kind of a node read back
    /// out of Neo4j from its labels (<see cref="GraphletReader"/> needs this to rebuild
    /// <see cref="EntityData"/> for a rule's L side; the ggifc entity it came from is
    /// long gone by then).
    /// </summary>
    public static NodeKind KindFromLabels(IEnumerable<string> labels)
    {
        foreach (var label in labels)
        {
            switch (label)
            {
                case "PrimaryNode":    return NodeKind.Primary;
                case "ConnectionNode": return NodeKind.Connection;
                case "SecondaryNode":  return NodeKind.Secondary;
                case "InlineNode":     return NodeKind.Inline;
            }
        }
        throw new ArgumentException(
            $"No node-kind label among [{string.Join(", ", labels)}].", nameof(labels));
    }
}
