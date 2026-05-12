using GeometryGym.Ifc;

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
}
