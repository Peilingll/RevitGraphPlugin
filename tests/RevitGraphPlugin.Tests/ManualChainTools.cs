using Neo4j.Driver;
using RevitGraphPlugin.Cypher;
using Xunit;
using Xunit.Abstractions;

namespace RevitGraphPlugin.Tests;

/// <summary>
/// Opt-in command-line harness for driving <see cref="RuleReplayer"/> against REAL data
/// (e.g. the plugin-live chain a Revit session produced) — RuleReplayer has no UI yet,
/// and the automated closed loops (RuleReplayTests) clean up after themselves, so this
/// is how a human watches undo/replay happen in Neo4j Browser / graph2ifc.
///
/// Inert under a normal <c>dotnet test</c>: it runs only when CHAIN_TOOL is set.
///
///   CHAIN_TOOL   = undo | replay          (required)
///   CHAIN_TARGET = chain's target_ts      (default plugin-live)
///   CHAIN_ONTO   = graph to mutate        (default = CHAIN_TARGET)
///   CHAIN_COUNT  = undo: rules to undo    (default 1)
///   CHAIN_BELOW  = undo: only seq below   (default unlimited — continue a partial undo)
///
///   $env:CHAIN_TOOL='undo'; dotnet test --filter ManualChainTools
/// </summary>
public sealed class ManualChainTools
{
    private readonly ITestOutputHelper _output;

    public ManualChainTools(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Run()
    {
        var tool = Environment.GetEnvironmentVariable("CHAIN_TOOL");
        if (string.IsNullOrEmpty(tool))
        {
            _output.WriteLine("CHAIN_TOOL not set — tool run skipped.");
            return;
        }

        var target = Environment.GetEnvironmentVariable("CHAIN_TARGET") ?? "plugin-live";
        var onto = Environment.GetEnvironmentVariable("CHAIN_ONTO") ?? target;
        var password = Environment.GetEnvironmentVariable("NEO4J_LOCAL_PASSWORD") ?? "password";

        var driver = GraphDatabase.Driver("bolt://127.0.0.1:7687", AuthTokens.Basic("neo4j", password));
        await using var _ = driver;

        switch (tool)
        {
            case "undo":
            {
                var count = int.Parse(Environment.GetEnvironmentVariable("CHAIN_COUNT") ?? "1");
                var below = long.Parse(Environment.GetEnvironmentVariable("CHAIN_BELOW")
                                       ?? long.MaxValue.ToString());
                var undone = await RuleReplayer.UndoAsync(driver, target, onto, count, below);
                _output.WriteLine($"undone {undone} rule(s) of '{target}' chain against '{onto}'");
                break;
            }
            case "replay":
            {
                var replayed = await RuleReplayer.ReplayAsync(driver, target, onto);
                _output.WriteLine($"replayed {replayed} rule(s) of '{target}' chain onto '{onto}'");
                break;
            }
            case "checkout":
            {
                var seq = long.Parse(Environment.GetEnvironmentVariable("CHAIN_SEQ")
                                     ?? throw new InvalidOperationException("CHAIN_SEQ not set"));
                var (from, to, steps) = await RuleReplayer.CheckoutAsync(driver, target, onto, seq);
                _output.WriteLine($"checkout {from} -> {to} ({steps} step(s)) on '{onto}'");
                break;
            }
            default:
                Assert.Fail($"unknown CHAIN_TOOL '{tool}' (use undo | replay)");
                break;
        }

        await using var session = driver.AsyncSession();
        var count2 = (await (await session.RunAsync(
            "MATCH (n {timestamp: $ts}) RETURN count(n) AS c", new { ts = onto })).ToListAsync())
            .Single()["c"].As<long>();
        _output.WriteLine($"'{onto}' now holds {count2} nodes");
    }
}
