using Neo4j.Driver;

namespace RevitGraphPlugin.Tools.Neo4jSmokeTest;

internal static class Program
{
    private static async Task<int> Main()
    {
        // Same NEO4J_LOCAL_* variables the plugin and the Python tools read.
        var host = Environment.GetEnvironmentVariable("NEO4J_LOCAL_HOSTNAME") ?? "127.0.0.1";
        var port = Environment.GetEnvironmentVariable("NEO4J_LOCAL_PORT") ?? "7687";
        var uri = $"neo4j://{host}:{port}";
        var user = Environment.GetEnvironmentVariable("NEO4J_LOCAL_USERNAME") ?? "neo4j";
        var password = Environment.GetEnvironmentVariable("NEO4J_LOCAL_PASSWORD");

        if (string.IsNullOrEmpty(password))
        {
            Console.Error.WriteLine("NEO4J_LOCAL_PASSWORD environment variable is required.");
            return 2;
        }

        Console.WriteLine($"Connecting to {uri} as {user} ...");

        await using var driver = GraphDatabase.Driver(uri, AuthTokens.Basic(user, password));
        try
        {
            await driver.VerifyConnectivityAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Connectivity check failed: {ex.Message}");
            return 1;
        }

        await using var session = driver.AsyncSession();
        var hello = await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync("RETURN 1 AS hello");
            var record = await cursor.SingleAsync();
            return record["hello"].As<long>();
        });

        Console.WriteLine($"hello = {hello}");
        return hello == 1L ? 0 : 1;
    }
}
