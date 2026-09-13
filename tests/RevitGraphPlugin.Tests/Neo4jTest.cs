using Neo4j.Driver;
using Xunit.Abstractions;

namespace RevitGraphPlugin.Tests;

/// <summary>Shared Neo4j connection for the graph tests: the plugin's NEO4J_LOCAL_* settings, or null when unreachable.</summary>
internal static class Neo4jTest
{
    public const string SkipReason = "Neo4j not reachable (set NEO4J_LOCAL_PASSWORD, start the instance)";

    public static IDriver? TryConnect(ITestOutputHelper? output = null)
    {
        var (uri, user, password) = Neo4jConfig.Resolve();
        try
        {
            var driver = GraphDatabase.Driver(uri, AuthTokens.Basic(user, password));
            driver.VerifyConnectivityAsync().GetAwaiter().GetResult();
            return driver;
        }
        catch (Exception ex)
        {
            output?.WriteLine($"Neo4j unreachable at {uri}: {ex.Message}");
            return null;
        }
    }
}
