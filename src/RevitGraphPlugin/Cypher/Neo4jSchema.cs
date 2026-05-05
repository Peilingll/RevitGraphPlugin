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
    /// ConMan2 (related-work.md §2.4) requires this for any node lookup —
    /// every <c>MERGE</c> in three-phase write goes through <c>(p21_id, timestamp)</c>.
    /// </summary>
    public const string CompositeIndexCypher =
        "CREATE INDEX generic_node_p21_timestamp IF NOT EXISTS " +
        "FOR (n:GenericNode) ON (n.p21_id, n.timestamp)";

    public static async Task EnsureAsync(IDriver driver)
    {
        await using var session = driver.AsyncSession();
        await session.ExecuteWriteAsync(async tx =>
        {
            var cursor = await tx.RunAsync(CompositeIndexCypher);
            return await cursor.ConsumeAsync();
        });
    }
}
