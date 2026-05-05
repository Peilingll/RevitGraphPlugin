namespace RevitGraphPlugin.Graph;

/// <summary>
/// Output of <c>IfcGraphMapper.Map(...)</c>: the set of nodes + edges to write
/// in one Neo4j transaction. Three-phase write reads from this same batch.
/// </summary>
public sealed record GraphBatch(
    IReadOnlyList<GraphNode> Nodes,
    IReadOnlyList<GraphEdge> Edges);
