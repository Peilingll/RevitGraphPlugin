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
            case "pingpong":
            {
                // Repeated-reversibility probe against a REAL chain (Esser 2022 §3.6:
                // reverse application must return the initial graph — here: N times).
                // Bounces between the newest baseline anchor and HEAD, comparing a
                // structure+value signature (identity columns masked) against the first
                // visit of each end; finishes back at the seq it started from. A failing
                // round prints round, direction and the drifted rows — the coordinates
                // "checkout sometimes fails" lacks.
                var rounds = int.Parse(Environment.GetEnvironmentVariable("CHAIN_ROUNDS") ?? "5");
                await PingPong(driver, target, onto, rounds);
                break;
            }
            default:
                Assert.Fail($"unknown CHAIN_TOOL '{tool}' (use undo | replay | checkout | pingpong)");
                break;
        }

        await using var session = driver.AsyncSession();
        var count2 = (await (await session.RunAsync(
            "MATCH (n {timestamp: $ts}) RETURN count(n) AS c", new { ts = onto })).ToListAsync())
            .Single()["c"].As<long>();
        _output.WriteLine($"'{onto}' now holds {count2} nodes");
    }

    // ── pingpong ─────────────────────────────────────────────────────────────────

    /// <summary>Same identity mask as GraphletDiff/PingPongTests — everything else must round-trip.</summary>
    private static readonly HashSet<string> MaskedKeys = new(StringComparer.Ordinal)
    {
        "p21_id", "timestamp", "revit_element_id", "GlobalId",
    };

    private async Task PingPong(IDriver driver, string target, string onto, int rounds)
    {
        long head, low, start;
        await using (var session = driver.AsyncSession())
        {
            var rows = await (await session.RunAsync(@"
MATCH (m) WHERE m.target_ts = $t AND (m:Rule OR m:Baseline)
RETURN m.seq AS seq, m:Baseline AS baseline ORDER BY m.seq", new { t = target })).ToListAsync();
            Assert.NotEmpty(rows);
            head = rows.Max(r => r["seq"].As<long>());
            low = rows.Where(r => r["baseline"].As<bool>()).Max(r => r["seq"].As<long>());
            start = await session.ExecuteReadAsync(
                async tx => await RuleReplayer.CurrentSeqAsync(tx, target)) ?? head;
        }
        _output.WriteLine($"pingpong '{target}' onto '{onto}': {low} <-> {head}, " +
                          $"{rounds} round(s), starting/ending at seq {start}");

        var sigs = new Dictionary<long, Dictionary<string, int>>();

        async Task Hop(int round, long seq, string direction)
        {
            try
            {
                var (from, to, steps) = await RuleReplayer.CheckoutAsync(driver, target, onto, seq);
                var sig = await Signature(driver, onto);
                _output.WriteLine($"  round {round}: {direction} {from} -> {to} " +
                                  $"({steps} step(s), {sig.Values.Sum()} sig rows)");
                if (!sigs.TryGetValue(seq, out var expected)) { sigs[seq] = sig; return; }

                var drift = expected.Keys.Union(sig.Keys)
                    .Select(k => (k, e: expected.GetValueOrDefault(k), a: sig.GetValueOrDefault(k)))
                    .Where(d => d.e != d.a).ToList();
                foreach (var (k, e, a) in drift)
                    _output.WriteLine($"    drift '{k}': expected {e}, got {a}");
                if (drift.Count > 0)
                    Assert.Fail($"round {round}, {direction} to seq {seq}: {drift.Count} drifted rows");
            }
            catch (Exception ex) when (ex is not Xunit.Sdk.FailException)
            {
                Assert.Fail($"round {round}, {direction} to seq {seq} THREW: {ex.Message}");
            }
        }

        if (start != head) await Hop(0, head, "warm-up to head");
        else sigs[head] = await Signature(driver, onto);

        for (var round = 1; round <= rounds; round++)
        {
            await Hop(round, low, "undo to");
            await Hop(round, head, "replay to");
        }
        if (start != head) await Hop(rounds + 1, start, "restore to");
        _output.WriteLine($"pingpong done — no drift in {rounds} round(s), back at seq {start}");
    }

    /// <summary>
    /// One multiset over structure AND values: node EntityType counts, edge triples,
    /// and every non-identity "EntityType|key=value" row.
    /// </summary>
    private static async Task<Dictionary<string, int>> Signature(IDriver driver, string ts)
    {
        await using var session = driver.AsyncSession();
        var sig = new Dictionary<string, int>();
        void Add(string row) => sig[row] = sig.GetValueOrDefault(row) + 1;

        foreach (var r in await (await session.RunAsync(
            "MATCH (n {timestamp: $ts}) RETURN n.EntityType AS t, properties(n) AS p",
            new { ts })).ToListAsync())
        {
            var t = r["t"].As<string>();
            Add($"node|{t}");
            foreach (var (key, value) in r["p"].As<IDictionary<string, object>>())
                if (!MaskedKeys.Contains(key))
                    Add($"value|{t}|{key}={value}");
        }

        foreach (var r in await (await session.RunAsync(
            @"MATCH (a {timestamp: $ts})-[e:rel]->(b {timestamp: $ts})
              RETURN a.EntityType + '|' + e.rel_type + '|' + toString(e.list_index) + '|' + b.EntityType AS k",
            new { ts })).ToListAsync())
            Add($"edge|{r["k"].As<string>()}");

        return sig;
    }
}
