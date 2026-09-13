using Neo4j.Driver;
using RevitGraphPlugin;
using RevitGraphPlugin.Cypher;

namespace RevitGraphPlugin.Tools.RuleChainCli;

/// <summary>
/// Command-line front end for <see cref="RuleReplayer"/>: list the rule chain and move
/// the current-state graph along it. Connection settings come from NEO4J_LOCAL_*.
/// </summary>
internal static class Program
{
    private const string Usage = @"usage:
  rulechain list                              [--target TS]
  rulechain checkout <seq|head>               [--target TS] [--onto TS]
  rulechain undo    [--count N] [--below SEQ] [--target TS] [--onto TS]
  rulechain replay                            [--target TS] [--onto TS]
  rulechain pingpong [--rounds N]             [--target TS] [--onto TS]

  --target  chain to read (default plugin-live)
  --onto    graph to move (default = --target)";

    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            Console.WriteLine(Usage);
            return args.Length == 0 ? 2 : 0;
        }

        var opts = ParseOptions(args.Skip(1));
        var target = opts.GetValueOrDefault("target") ?? "plugin-live";
        var onto = opts.GetValueOrDefault("onto") ?? target;

        var (uri, user, password) = Neo4jConfig.Resolve();
        await using var driver = GraphDatabase.Driver(uri, AuthTokens.Basic(user, password));

        try
        {
            switch (args[0])
            {
                case "list":
                    await ListAsync(driver, target);
                    break;

                case "checkout":
                {
                    var arg = Positional(args) ?? throw new ArgumentException("checkout needs <seq|head>");
                    var chain = await ChainAsync(driver, target);
                    var seq = arg == "head" ? chain.Head : long.Parse(arg);
                    if (!chain.Seqs.Contains(seq))
                        throw new ArgumentException($"seq {seq} is not on the '{target}' chain");
                    if (seq < chain.NewestBaseline)
                        throw new ArgumentException(
                            $"cannot check out below the newest baseline (seq {chain.NewestBaseline}): the graph was rebuilt there");
                    var (from, to, steps) = await RuleReplayer.CheckoutAsync(driver, target, onto, seq);
                    Console.WriteLine($"checkout {from} -> {to} ({steps} step(s)) on '{onto}'");
                    break;
                }

                case "undo":
                {
                    var count = int.Parse(opts.GetValueOrDefault("count") ?? "1");
                    var below = long.Parse(opts.GetValueOrDefault("below") ?? long.MaxValue.ToString());
                    var undone = await RuleReplayer.UndoAsync(driver, target, onto, count, below);
                    Console.WriteLine($"undone {undone} rule(s) of '{target}' against '{onto}'");
                    break;
                }

                case "replay":
                {
                    var replayed = await RuleReplayer.ReplayAsync(driver, target, onto);
                    Console.WriteLine($"replayed {replayed} rule(s) of '{target}' onto '{onto}'");
                    break;
                }

                case "pingpong":
                {
                    var rounds = int.Parse(opts.GetValueOrDefault("rounds") ?? "5");
                    if (!await PingPongAsync(driver, target, onto, rounds)) return 1;
                    break;
                }

                default:
                    Console.Error.WriteLine($"unknown command '{args[0]}'\n{Usage}");
                    return 2;
            }

            Console.WriteLine($"'{onto}' now holds {await NodeCountAsync(driver, onto)} nodes");
            return 0;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidOperationException)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    // ── argument parsing ─────────────────────────────────────────────────────────

    private static Dictionary<string, string> ParseOptions(IEnumerable<string> args)
    {
        var opts = new Dictionary<string, string>(StringComparer.Ordinal);
        string? key = null;
        foreach (var a in args)
        {
            if (a.StartsWith("--", StringComparison.Ordinal)) { key = a[2..]; opts[key] = ""; }
            else if (key is not null) { opts[key] = a; key = null; }
        }
        return opts;
    }

    /// <summary>The first argument after the command that is not an option or an option value.</summary>
    private static string? Positional(string[] args)
    {
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal)) { i++; continue; }
            return args[i];
        }
        return null;
    }

    // ── chain queries ────────────────────────────────────────────────────────────

    private sealed record Chain(List<(long Seq, bool IsBaseline, string? Op)> Members, long? Current)
    {
        public HashSet<long> Seqs => Members.Select(m => m.Seq).ToHashSet();
        public long Head => Members.Max(m => m.Seq);
        public long NewestBaseline => Members.Where(m => m.IsBaseline).Max(m => m.Seq);
    }

    private static async Task<Chain> ChainAsync(IDriver driver, string target)
    {
        await using var session = driver.AsyncSession();
        var rows = await (await session.RunAsync(@"
MATCH (m) WHERE m.target_ts = $t AND (m:Rule OR m:Baseline)
RETURN m.seq AS seq, m:Baseline AS baseline, m.op AS op ORDER BY m.seq", new { t = target })).ToListAsync();
        if (rows.Count == 0) throw new InvalidOperationException($"no chain found for target '{target}'");
        var members = rows
            .Select(r => (r["seq"].As<long>(), r["baseline"].As<bool>(), r["op"]?.As<string>()))
            .ToList();
        var current = await session.ExecuteReadAsync(tx => RuleReplayer.CurrentSeqAsync(tx, target));
        return new Chain(members, current);
    }

    private static async Task ListAsync(IDriver driver, string target)
    {
        var chain = await ChainAsync(driver, target);
        var current = chain.Current ?? chain.Head;
        Console.WriteLine($"chain '{target}': {chain.Members.Count} member(s), graph at seq {current}");
        foreach (var (seq, isBaseline, op) in chain.Members)
        {
            var kind = isBaseline ? "Baseline" : $"Rule {op}";
            var here = seq == current ? "  <- current" : "";
            Console.WriteLine($"  seq {seq,-4} {kind}{here}");
        }
    }

    private static async Task<long> NodeCountAsync(IDriver driver, string ts)
    {
        await using var session = driver.AsyncSession();
        return (await (await session.RunAsync(
            "MATCH (n {timestamp: $ts}) RETURN count(n) AS c", new { ts })).ToListAsync())
            .Single()["c"].As<long>();
    }

    // ── pingpong ─────────────────────────────────────────────────────────────────

    /// <summary>Identity columns that legitimately churn on rebuild (same mask as GraphletDiff).</summary>
    private static readonly HashSet<string> MaskedKeys = new(StringComparer.Ordinal)
    {
        "p21_id", "timestamp", "revit_element_id", "GlobalId",
    };

    /// <summary>
    /// Bounce the graph between the newest baseline and HEAD <paramref name="rounds"/> times,
    /// comparing every visit of each end with the first. Ends where it started. False on drift.
    /// </summary>
    private static async Task<bool> PingPongAsync(IDriver driver, string target, string onto, int rounds)
    {
        var chain = await ChainAsync(driver, target);
        var head = chain.Head;
        var low = chain.NewestBaseline;
        var start = chain.Current ?? head;
        Console.WriteLine($"pingpong '{target}' onto '{onto}': {low} <-> {head}, {rounds} round(s), starting at seq {start}");

        var sigs = new Dictionary<long, Dictionary<string, int>>();
        var ok = true;

        async Task Hop(int round, long seq, string direction)
        {
            var (from, to, steps) = await RuleReplayer.CheckoutAsync(driver, target, onto, seq);
            var sig = await SignatureAsync(driver, onto);
            Console.WriteLine($"  round {round}: {direction} {from} -> {to} ({steps} step(s), {sig.Values.Sum()} sig rows)");
            if (!sigs.TryGetValue(seq, out var expected)) { sigs[seq] = sig; return; }

            var drift = expected.Keys.Union(sig.Keys)
                .Select(k => (k, e: expected.GetValueOrDefault(k), a: sig.GetValueOrDefault(k)))
                .Where(d => d.e != d.a).ToList();
            foreach (var (k, e, a) in drift)
                Console.WriteLine($"    drift '{k}': expected {e}, got {a}");
            if (drift.Count > 0) ok = false;
        }

        if (start != head) await Hop(0, head, "warm-up to head");
        else sigs[head] = await SignatureAsync(driver, onto);

        for (var round = 1; round <= rounds && ok; round++)
        {
            await Hop(round, low, "undo to");
            await Hop(round, head, "replay to");
        }
        if (start != head) await Hop(rounds + 1, start, "restore to");

        Console.WriteLine(ok
            ? $"pingpong done: no drift in {rounds} round(s), back at seq {start}"
            : "pingpong FAILED: drift detected (see above)");
        return ok;
    }

    /// <summary>One multiset over structure and values: node types, edge triples, non-identity property values.</summary>
    private static async Task<Dictionary<string, int>> SignatureAsync(IDriver driver, string ts)
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
