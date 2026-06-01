using Neo4j.Driver;

namespace RevitGraphPlugin.Tools.Neo4jSmokeTest;

internal static class Program
{
    private static async Task<int> Main()
    {
        // Prefer the NEO4J_LOCAL_* names the Python bridge (ConMan2's Neo4jConnection)
        // actually reads, so this smoke test verifies the same credentials the live
        // pipeline uses. Fall back to the legacy NEO4J_* names for older setups.
        var host = Environment.GetEnvironmentVariable("NEO4J_LOCAL_HOSTNAME") ?? "127.0.0.1";
        var port = Environment.GetEnvironmentVariable("NEO4J_LOCAL_PORT") ?? "7687";
        var uri = Environment.GetEnvironmentVariable("NEO4J_URI") ?? $"neo4j://{host}:{port}";
        var user = Environment.GetEnvironmentVariable("NEO4J_LOCAL_USERNAME")
            ?? Environment.GetEnvironmentVariable("NEO4J_USER") ?? "neo4j";
        var password = Environment.GetEnvironmentVariable("NEO4J_LOCAL_PASSWORD")
            ?? Environment.GetEnvironmentVariable("NEO4J_PASSWORD");

        if (string.IsNullOrEmpty(password))
        {
            Console.Error.WriteLine(
                "NEO4J_LOCAL_PASSWORD (or legacy NEO4J_PASSWORD) environment variable is required.");
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
