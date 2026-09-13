using Neo4j.Driver;

// ── Pipeline: DIRECT-WRITE, live incremental sync ──
// Consume the :Rule chain: replay applies stored rules forward from the newest
// (:Baseline), undo inverts them backwards from HEAD. Context is resolved through the
// stored ContextRef strings, falling back to a raw p21 only where storage did.
namespace RevitGraphPlugin.Cypher;

public static class RuleReplayer
{
    private sealed record Member(long Seq, bool IsBaseline, string Op, long ElementId, string Ts, bool Aligned);

    /// <summary>Re-apply every rule after the newest <c>(:Baseline)</c> onto <paramref name="ontoTs"/> (expected to hold that baseline). One transaction per rule. Returns the count.</summary>
    public static async Task<int> ReplayAsync(IDriver driver, string chainTarget, string ontoTs)
    {
        await using var session = driver.AsyncSession();
        var members = await session.ExecuteReadAsync(tx => MembersAsync(tx, chainTarget));
        var afterSeq = members.Where(m => m.IsBaseline).Select(m => m.Seq).DefaultIfEmpty(0).Max();
        var rules = members.Where(m => !m.IsBaseline && m.Seq > afterSeq).ToList();
        foreach (var rule in rules)
            await session.ExecuteWriteAsync(tx => ApplyStoredAsync(tx, rule, ontoTs));
        return rules.Count;
    }

    /// <summary>
    /// Invert the newest <paramref name="count"/> rules below <paramref name="belowSeq"/>,
    /// stopping at a <c>(:Baseline)</c>. Undoing does not pop the chain. One transaction per rule.
    /// </summary>
    public static async Task<int> UndoAsync(
        IDriver driver, string chainTarget, string ontoTs,
        int count = int.MaxValue, long belowSeq = long.MaxValue)
    {
        await using var session = driver.AsyncSession();
        var members = await session.ExecuteReadAsync(tx => MembersAsync(tx, chainTarget));
        var undone = 0;
        foreach (var member in members.Where(m => m.Seq < belowSeq).OrderByDescending(m => m.Seq))
        {
            if (member.IsBaseline || undone >= count) break;
            await session.ExecuteWriteAsync(tx => UndoStoredAsync(tx, member, ontoTs));
            undone++;
        }
        return undone;
    }

    /// <summary>
    /// Move <paramref name="ontoTs"/> to the state after chain member <paramref name="targetSeq"/>,
    /// walking from <c>checked_out_seq</c> one rule at a time so every rule is applied to
    /// the state it was recorded against. One transaction per step: a failure leaves the
    /// graph at a real version, and several steps in one transaction break Neo4j's index
    /// after a renumber-then-delete ("Node with id N has been deleted in this transaction").
    /// </summary>
    public static async Task<(long From, long To, int Steps)> CheckoutAsync(
        IDriver driver, string chainTarget, string ontoTs, long targetSeq)
    {
        await using var session = driver.AsyncSession();
        var (members, current) = await session.ExecuteReadAsync(async tx =>
        {
            var ms = await MembersAsync(tx, chainTarget);
            if (ms.Count == 0)
                throw new InvalidOperationException($"no chain for target '{chainTarget}'");
            if (ms.All(m => m.Seq != targetSeq))
                throw new InvalidOperationException($"seq {targetSeq} is not on the chain");

            var newestBaseline = ms.Where(m => m.IsBaseline).Select(m => m.Seq)
                                   .DefaultIfEmpty(0).Max();
            if (targetSeq < newestBaseline)
                throw new InvalidOperationException(
                    $"cannot check out below the newest baseline anchor (seq {newestBaseline}) — the graph was rebuilt there");

            var head = ms.Max(m => m.Seq);
            return (ms, await CurrentSeqAsync(tx, chainTarget) ?? head);
        });

        var steps = 0;
        if (targetSeq > current)
        {
            foreach (var m in members
                         .Where(m => !m.IsBaseline && m.Seq > current && m.Seq <= targetSeq)
                         .OrderBy(m => m.Seq))
            {
                await session.ExecuteWriteAsync(async tx =>
                {
                    await ApplyStoredAsync(tx, m, ontoTs);
                    await SetCurrentSeqAsync(tx, chainTarget, m.Seq);
                });
                steps++;
            }
        }
        else if (targetSeq < current)
        {
            foreach (var m in members
                         .Where(m => !m.IsBaseline && m.Seq > targetSeq && m.Seq <= current)
                         .OrderByDescending(m => m.Seq))
            {
                // After undoing m the graph stands at the member just below it.
                var below = members.Where(x => x.Seq < m.Seq).Max(x => x.Seq);
                await session.ExecuteWriteAsync(async tx =>
                {
                    await UndoStoredAsync(tx, m, ontoTs);
                    await SetCurrentSeqAsync(tx, chainTarget, below);
                });
                steps++;
            }
        }

        // Reaching a target with no rule between (e.g. head == target, or a baseline
        // right below) still records the position.
        await session.ExecuteWriteAsync(tx => SetCurrentSeqAsync(tx, chainTarget, targetSeq));
        return (current, targetSeq, steps);
    }

    /// <summary>Where the tracked graph currently stands, or null if never checked out.</summary>
    public static async Task<long?> CurrentSeqAsync(IAsyncQueryRunner tx, string target)
    {
        var rows = await (await tx.RunAsync(
            "MATCH (c:RuleChain {target_ts: $t}) RETURN c.checked_out_seq AS seq",
            new { t = target })).ToListAsync();
        return rows.Count == 0 ? null : rows[0]["seq"]?.As<long?>();
    }

    internal static Task SetCurrentSeqAsync(IAsyncQueryRunner tx, string target, long seq)
        => tx.RunAsync(
            "MATCH (c:RuleChain {target_ts: $t}) SET c.checked_out_seq = $seq",
            new { t = target, seq });

    private static async Task<List<Member>> MembersAsync(IAsyncQueryRunner tx, string target)
    {
        var rows = await (await tx.RunAsync(@"
MATCH (m) WHERE m.target_ts = $target AND (m:Rule OR m:Baseline)
RETURN m.seq AS seq, m:Baseline AS baseline, m.op AS op,
       m.revit_element_id AS eid, m.timestamp AS ts,
       coalesce(m.aligned, false) AS aligned
ORDER BY m.seq", new { target })).ToListAsync();

        return rows.Select(r => new Member(
            r["seq"].As<long>(),
            r["baseline"].As<bool>(),
            r["op"]?.As<string>() ?? string.Empty,
            r["eid"]?.As<long?>() ?? 0,
            r["ts"].As<string>(),
            r["aligned"].As<bool>())).ToList();
    }

    // ── forward ──────────────────────────────────────────────────────────────────

    private static async Task ApplyStoredAsync(IAsyncQueryRunner tx, Member rule, string ontoTs)
    {
        switch (rule.Op)
        {
            case "Insert":
                await MergeCopiesAsync(tx, rule.Ts + "-R", ontoTs);
                await ApplyGlueAsync(tx, rule.Ts, "R", ontoTs);
                break;

            // Aligned Replace: copies are the pushout only; the interface is renumbered.
            case "Replace" when rule.Aligned:
                await DeleteByCopyP21sAsync(tx, rule.Ts + "-L", ontoTs);
                await MergeCopiesAsync(tx, rule.Ts + "-R", ontoTs);
                await ApplyGlueAsync(tx, rule.Ts, "R", ontoTs);
                await ApplyChangesAsync(tx, rule.Ts, ontoTs, reverse: false);
                await RenumberAsync(tx, rule.Ts, ontoTs, reverse: false);
                break;

            case "Replace":
                await DeleteOwnedAsync(tx, ontoTs, rule.ElementId);
                await MergeCopiesAsync(tx, rule.Ts + "-R", ontoTs);
                await ApplyGlueAsync(tx, rule.Ts, "R", ontoTs);
                break;

            case "Modify":
                await ApplyChangesAsync(tx, rule.Ts, ontoTs, reverse: false);
                await RenumberAsync(tx, rule.Ts, ontoTs, reverse: false);
                break;

            case "Remove":
                await DeleteOwnedAsync(tx, ontoTs, rule.ElementId);
                await DeleteSharedAsync(tx, rule.Ts, ontoTs);
                break;

            default:
                throw new InvalidOperationException($"unknown stored op '{rule.Op}' (seq {rule.Seq})");
        }
    }

    // ── backward ─────────────────────────────────────────────────────────────────

    private static async Task UndoStoredAsync(IAsyncQueryRunner tx, Member rule, string ontoTs)
    {
        switch (rule.Op)
        {
            // Delete by copy p21s (catches untagged riders such as a first-element
            // containment rel) and by element id (catches nodes rebuilt under new p21s).
            // p21 first: it must run while the nodes are still alive in this transaction.
            case "Insert":
                await DeleteByCopyP21sAsync(tx, rule.Ts + "-R", ontoTs);
                await DeleteOwnedAsync(tx, ontoTs, rule.ElementId);
                break;

            case "Replace" when rule.Aligned:
                await DeleteByCopyP21sAsync(tx, rule.Ts + "-R", ontoTs);
                await MergeCopiesAsync(tx, rule.Ts + "-L", ontoTs);
                await ApplyGlueAsync(tx, rule.Ts, "L", ontoTs);
                await ApplyChangesAsync(tx, rule.Ts, ontoTs, reverse: true);
                await RenumberAsync(tx, rule.Ts, ontoTs, reverse: true);
                break;

            case "Replace":
                await DeleteByCopyP21sAsync(tx, rule.Ts + "-R", ontoTs);   // p21 first, see Insert
                await DeleteOwnedAsync(tx, ontoTs, rule.ElementId);
                await MergeCopiesAsync(tx, rule.Ts + "-L", ontoTs);
                await ApplyGlueAsync(tx, rule.Ts, "L", ontoTs);
                break;

            case "Modify":
                await ApplyChangesAsync(tx, rule.Ts, ontoTs, reverse: true);
                await RenumberAsync(tx, rule.Ts, ontoTs, reverse: true);
                break;

            case "Remove":
                await MergeCopiesAsync(tx, rule.Ts + "-L", ontoTs);
                await ApplyGlueAsync(tx, rule.Ts, "L", ontoTs);
                break;

            default:
                throw new InvalidOperationException($"unknown stored op '{rule.Op}' (seq {rule.Seq})");
        }
    }

    // ── shared mechanics ─────────────────────────────────────────────────────────

    /// <summary>Load a copy namespace and merge it under <paramref name="ontoTs"/> (p21s preserved).</summary>
    private static async Task MergeCopiesAsync(IAsyncQueryRunner tx, string copyTs, string ontoTs)
    {
        var copies = await GraphletReader.ReadTimestampAsync(tx, copyTs);
        if (copies.Count == 0) return;

        var stamped = copies
            .Select(d => d with
            {
                Properties = new Dictionary<string, object>(d.Properties) { ["timestamp"] = ontoTs },
            })
            .ToList();

        foreach (var kind in new[] { NodeKind.Primary, NodeKind.Connection, NodeKind.Secondary })
            await CypherEmitter.BulkMergeNodes(tx, kind, stamped.Where(d => d.Kind == kind).ToList());
        await CypherEmitter.BulkMergeEdges(tx, stamped.SelectMany(d => d.Edges).ToList(), ontoTs);

        // Inline children have no merge key: clear the old ones before re-creating.
        await Run(tx, @"
UNWIND $p21s AS p
MATCH (n:GenericNode {timestamp: $ts, p21_id: p})-[:rel]->(i:InlineNode {timestamp: $ts})
DETACH DELETE i",
            new { ts = ontoTs, p21s = stamped.Select(d => $"#{d.P21}").ToList() });
        await CypherEmitter.BulkCreateInlines(tx, stamped.SelectMany(d => d.Inlines).ToList(), ontoTs);
    }

    /// <summary>Delete an element's graphlet (inline children carry the ownership tag too).</summary>
    private static Task DeleteOwnedAsync(IAsyncQueryRunner tx, string ontoTs, long elementId)
        => tx.RunAsync(
            "MATCH (n {timestamp: $ts, revit_element_id: $eid}) DETACH DELETE n",
            new { ts = ontoTs, eid = elementId });

    /// <summary>Delete the nodes a copy namespace lists, by p21, with their inline children.</summary>
    private static async Task DeleteByCopyP21sAsync(IAsyncQueryRunner tx, string copyTs, string ontoTs)
    {
        await Run(tx, @"
MATCH (c:GenericNode {timestamp: $copyTs})
MATCH (n:GenericNode {timestamp: $ontoTs, p21_id: c.p21_id})
OPTIONAL MATCH (n)-[:rel]->(i:InlineNode {timestamp: $ontoTs})
DETACH DELETE i, n",
            new { copyTs, ontoTs });
    }

    /// <summary>Resolve and drop the rule's <c>shared_delete</c> nodes in the target graph.</summary>
    private static async Task DeleteSharedAsync(IAsyncQueryRunner tx, string ruleTs, string ontoTs)
    {
        var refs = (await (await tx.RunAsync(
            "MATCH (r:Rule {timestamp: $ts}) RETURN r.shared_delete AS refs",
            new { ts = ruleTs })).ToListAsync())
            .Single()["refs"]?.As<List<string>>() ?? new List<string>();

        foreach (var contextString in refs)
        {
            var p21 = await ResolveContextAsync(tx, ontoTs, contextString);
            await Run(tx, 
                "MATCH (n:GenericNode {timestamp: $ts, p21_id: $p21}) DETACH DELETE n",
                new { ts = ontoTs, p21 = $"#{p21}" });
        }
    }

    private static async Task ApplyChangesAsync(
        IAsyncQueryRunner tx, string ruleTs, string ontoTs, bool reverse)
    {
        var rows = await (await tx.RunAsync(@"
MATCH (:Rule {timestamp: $ts})-[:SETS]->(c:Change)
RETURN c.path AS path, c.path_after AS path_after,
       c.p21_before AS p21_before, c.p21_after AS p21_after,
       c.key AS key, c.list_index AS list_index,
       c.before AS before, c.after AS after, c.inline AS inline",
            new { ts = ruleTs })).ToListAsync();

        foreach (var row in rows)
        {
            // Forward prefers the before-name, undo the after-name (they differ only on
            // chains recorded before StableIds).
            var (primary, secondary) = reverse
                ? (row["path_after"]?.As<string>(), row["path"].As<string>())
                : (row["path"].As<string>(), row["path_after"]?.As<string>());
            // Same-database fallback: the local id on the side we are moving from.
            var localP21 = (reverse ? row["p21_after"] : row["p21_before"])?.As<string>();
            var p21 = await ResolveEitherAsync(tx, ontoTs, primary, secondary, localP21);
            var key = row["key"].As<string>();
            var value = reverse ? row["before"].As<object>() : row["after"].As<object>();

            if (row["inline"].As<bool>())
            {
                await Run(tx, @"
MATCH (n:GenericNode {timestamp: $ts, p21_id: $p21})
      -[e:rel {rel_type: $key, list_index: $li}]->(v:InlineNode {timestamp: $ts})
SET v.wrappedValue = $value",
                    new { ts = ontoTs, p21 = $"#{p21}", key, li = row["list_index"].As<int>(), value });
            }
            else
            {
                await Run(tx, 
                    "MATCH (n:GenericNode {timestamp: $ts, p21_id: $p21}) SET n += $props",
                    new { ts = ontoTs, p21 = $"#{p21}", props = new Dictionary<string, object> { [key] = value } });
            }
        }
    }

    /// <summary>
    /// Replay an aligned rule's interface renumbering (from→to forward, to→from on undo).
    /// A "from" id absent from the graph is skipped: a later unstored NoChange modify
    /// already renumbered it.
    /// </summary>
    private static async Task RenumberAsync(
        IAsyncQueryRunner tx, string ruleTs, string ontoTs, bool reverse)
    {
        await Run(tx, @"
MATCH (r:Rule {timestamp: $ruleTs})
WITH coalesce(r.renumber_from, []) AS f, coalesce(r.renumber_to, []) AS t
UNWIND range(0, size(f) - 1) AS i
WITH CASE WHEN $reverse THEN t[i] ELSE f[i] END AS from,
     CASE WHEN $reverse THEN f[i] ELSE t[i] END AS to
MATCH (n:GenericNode {timestamp: $ts, p21_id: from})
SET n.p21_id = to",
            new { ruleTs, ts = ontoTs, reverse });
    }

    /// <summary>Recreate one side's boundary edges from the stored :Glue rows.</summary>
    private static async Task ApplyGlueAsync(
        IAsyncQueryRunner tx, string ruleTs, string side, string ontoTs)
    {
        var rows = await (await tx.RunAsync(@"
MATCH (:Rule {timestamp: $ts})-[:GLUE]->(g:Glue {side: $side})
RETURN g.context AS context, g.rel_type AS rel_type, g.list_index AS list_index,
       g.local_p21 AS local, g.direction AS direction",
            new { ts = ruleTs, side })).ToListAsync();

        foreach (var row in rows)
        {
            var contextP21 = await ResolveContextAsync(tx, ontoTs, row["context"].As<string>());
            var (fromP21, toP21) = row["direction"].As<string>() == "in"
                ? ($"#{contextP21}", row["local"].As<string>())
                : (row["local"].As<string>(), $"#{contextP21}");

            await Run(tx, @"
MATCH (a:GenericNode {timestamp: $ts, p21_id: $from})
MATCH (b:GenericNode {timestamp: $ts, p21_id: $to})
MERGE (a)-[:rel {rel_type: $relType, list_index: $listIndex}]->(b)",
                new
                {
                    ts = ontoTs, from = fromP21, to = toP21,
                    relType = row["rel_type"].As<string>(),
                    listIndex = row["list_index"].As<int>(),
                });
        }
    }

    /// <summary>A stored context string back to a p21: portable refs via <see cref="ContextResolver.FindAsync"/>, raw "#n" literally (same database only).</summary>
    private static async Task<int> ResolveContextAsync(
        IAsyncQueryRunner tx, string ontoTs, string contextString)
        => await TryResolveContextAsync(tx, ontoTs, contextString)
           ?? throw new InvalidOperationException(
               $"context not found in '{ontoTs}': {contextString}");

    private static async Task<int> ResolveEitherAsync(
        IAsyncQueryRunner tx, string ontoTs, string? primary, string? secondary,
        string? localP21 = null)
    {
        if (primary is not null && await TryResolveContextAsync(tx, ontoTs, primary) is { } p21)
            return p21;
        if (secondary is not null && await TryResolveContextAsync(tx, ontoTs, secondary) is { } fallback)
            return fallback;

        // Last resort, same database only: the node's local id on the side we came from.
        if (localP21 is not null && P21Id.TryParse(localP21, out var local))
        {
            var exists = await (await tx.RunAsync(
                "MATCH (n:GenericNode {timestamp: $ts, p21_id: $p21}) RETURN count(n) AS c",
                new { ts = ontoTs, p21 = localP21 })).ToListAsync();
            if (exists.Single()["c"].As<int>() > 0) return local;
        }

        throw new InvalidOperationException(
            $"context not found in '{ontoTs}': {primary} (nor {secondary}, nor {localP21})");
    }

    private static async Task<int?> TryResolveContextAsync(
        IAsyncQueryRunner tx, string ontoTs, string contextString)
    {
        if (ContextRef.TryParse(contextString, out var contextRef))
            return await ContextResolver.FindAsync(tx, ontoTs, contextRef!);
        if (P21Id.TryParse(contextString, out var p21)) return p21;
        throw new InvalidOperationException($"unreadable context reference: {contextString}");
    }

    /// <summary>Run a write and consume its result now, so a server-side error surfaces at the statement, not at commit.</summary>
    private static async Task Run(IAsyncQueryRunner tx, string query, object parameters)
        => await (await tx.RunAsync(query, parameters)).ConsumeAsync();
}
