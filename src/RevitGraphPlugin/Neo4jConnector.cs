using Neo4j.Driver;

namespace RevitGraphPlugin;

internal sealed class Neo4jConnector : IDisposable
{
    private readonly IDriver _driver;

    private Neo4jConnector(IDriver driver) => _driver = driver;

    public IDriver Driver => _driver;

    public static Neo4jConnector FromEnvironment()
    {
        var uri = Environment.GetEnvironmentVariable("NEO4J_URI") ?? "neo4j://127.0.0.1:7687";
        var user = Environment.GetEnvironmentVariable("NEO4J_USER") ?? "neo4j";
        var password = Environment.GetEnvironmentVariable("NEO4J_PASSWORD")
            ?? throw new InvalidOperationException(
                "NEO4J_PASSWORD environment variable is required. " +
                "Set it as a User environment variable so Revit inherits it.");

        var driver = GraphDatabase.Driver(uri, AuthTokens.Basic(user, password));
        return new Neo4jConnector(driver);
    }

    public void Dispose() => _driver.Dispose();
}
