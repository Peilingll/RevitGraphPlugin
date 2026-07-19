namespace RevitGraphPlugin;

/// <summary>
/// Neo4j connection settings, mirroring ConMan2's Neo4jConnection env-var convention
/// (NEO4J_LOCAL_*) so every sink — bridge, direct snapshot, live incremental — hits
/// the same database with the same credentials.
/// </summary>
internal static class Neo4jConfig
{
    public static (string uri, string user, string password) Resolve()
    {
        var user = Environment.GetEnvironmentVariable("NEO4J_LOCAL_USERNAME") ?? "neo4j";
        var password = Environment.GetEnvironmentVariable("NEO4J_LOCAL_PASSWORD") ?? "password";
        var host = Environment.GetEnvironmentVariable("NEO4J_LOCAL_HOSTNAME") ?? "127.0.0.1";
        var port = Environment.GetEnvironmentVariable("NEO4J_LOCAL_PORT") ?? "7687";
        if (host == "localhost") host = "127.0.0.1";   // force IPv4 (matches ConMan2)
        return ($"bolt://{host}:{port}", user, password);
    }
}
