using Neo4j.Driver;

namespace RevitGraphPlugin.Cypher;

/// <summary>
/// Schema-setup statements run once per database lifetime. Idempotent —
/// safe to call on every Revit startup once Stage 4 wires it in.
/// </summary>
public static class Neo4jSchema
{
    /// <summary>
    /// Composite index per design.md §4 Stage 2 ("Mandatory composite index").
    /// Hot paths in Phase 1/2/3 of <see cref="Neo4jGraphWriter"/> still go
    /// through <c>(p21_id, timestamp)</c> for Secondary nodes and edge endpoint
    /// lookups.
    /// </summary>
    public const string CompositeIndexCypher =
        "CREATE INDEX generic_node_p21_timestamp IF NOT EXISTS " +
        "FOR (n:GenericNode) ON (n.p21_id, n.timestamp)";

    /// <summary>
    /// Stage 5 — supports Tag-keyed MERGE for IfcElement-derived primaries.
    /// </summary>
    public const string TagIndexCypher =
        "CREATE INDEX generic_node_tag_timestamp IF NOT EXISTS " +
        "FOR (n:GenericNode) ON (n.Tag, n.timestamp)";

    /// <summary>
    /// Stage 5 — supports GlobalId-keyed MERGE for IfcRelationship-derived
    /// connections.
    /// </summary>
    public const string GlobalIdIndexCypher =
        "CREATE INDEX generic_node_globalid_timestamp IF NOT EXISTS " +
        "FOR (n:GenericNode) ON (n.GlobalId, n.timestamp)";

    public static async Task EnsureAsync(IDriver driver)
    {
        await using var session = driver.AsyncSession();
        await session.ExecuteWriteAsync(async tx =>
        {
            foreach (var cypher in new[] { CompositeIndexCypher, TagIndexCypher, GlobalIdIndexCypher })
            {
                var cursor = await tx.RunAsync(cypher);
                await cursor.ConsumeAsync();
            }
            return 0;
        });
    }
}
