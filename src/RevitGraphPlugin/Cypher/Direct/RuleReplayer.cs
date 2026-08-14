using Neo4j.Driver;

// ── Pipeline: DIRECT-WRITE, live incremental sync ──
// Rule persistence step 5 (doc_process/2026-08-02-plan-rule-persistence.md §7):
// consume the :Rule chain. Replay walks [:NEXT] from the newest (:Baseline) and applies
// each stored rule to a target graph; undo walks backwards from HEAD inverting each.
// Together they are the closed-loop acceptance of everything below them: replay(chain)
// over a baseline copy must reproduce the current-state graph, and undo(chain) must
// take the current-state graph back to its baseline. Both resolve context through the
// stored ContextRef strings (ContextResolver.FindAsync) — the same machinery a foreign
// host graph would use — falling back to the raw p21 literal only where storage did
// (the known first-geometry-element limitation).
namespace RevitGraphPlugin.Cypher;

public static class RuleReplayer
{
    private sealed record Member(long Seq, bool IsBaseline, string Op, long ElementId, string Ts);

    /// <summary>
    /// Re-apply every rule after the newest <c>(:Baseline)</c> anchor of
    /// <paramref name="chainTarget"/>'s chain onto the <paramref name="ontoTs"/> graph
    /// (which is expected to hold that baseline's content). One transaction — a partial
    /// replay would be a graph that matches no version. Returns the rules replayed.
    /// </summary>
    public static async Task<int> ReplayAsync(IDriver driver, string chainTarget, string ontoTs)
    {
        await using var session = driver.AsyncSession();
        return await session.ExecuteWriteAsync(async tx =>
        {
            var members = await MembersAsync(tx, chainTarget);
            var afterSeq = members.Where(m => m.IsBaseline).Select(m => m.Seq).DefaultIfEmpty(0).Max();
            var rules = members.Where(m => !m.IsBaseline && m.Seq > afterSeq).ToList();
            foreach (var rule in rules)
                await ApplyStoredAsync(tx, rule, ontoTs);
            return rules.Count;
        });
    }

    /// <summary>
    /// Invert the newest <paramref name="count"/> rules of the chain, newest first,
    /// against the <paramref name="ontoTs"/> graph. Stops at a <c>(:Baseline)</c> anchor —
    /// the graph was rebuilt there, so earlier rules cannot be unwound across it.
    /// The walk is stateless: the chain records history and undoing does not pop it, so
    /// a caller continuing a partial undo must say where it stopped via
    /// <paramref name="belowSeq"/> (only rules with a smaller seq are considered).
    /// </summary>
    public static async Task<int> UndoAsync(
        IDriver driver, string chainTarget, string ontoTs,
        int count = int.MaxValue, long belowSeq = long.MaxValue)
    {
        await using var session = driver.AsyncSession();
        return await session.ExecuteWriteAsync(async tx =>
        {
            var members = await MembersAsync(tx, chainTarget);
            var undone = 0;
            foreach (var member in members.Where(m => m.Seq < belowSeq).OrderByDescending(m => m.Seq))
            {
                if (member.IsBaseline || undone >= count) break;
                await UndoStoredAsync(tx, member, ontoTs);
                undone++;
            }
            return undone;
        });
    }

    /// <summary>
    /// Move <paramref name="ontoTs"/> to the state right after chain member
    /// <paramref name="targetSeq"/>, going whichever way is needed from where it
    /// currently stands (tracked on the chain node as <c>checked_out_seq</c>; a chain
    /// that has never been checked out sits at HEAD, where a live session leaves it).
    /// <para>
    /// Direction matters: a rule may only be applied to — or inverted from — the state
    /// it was recorded against. Its stored context refs are anchored on GlobalIds that
    /// ggifc regenerates whenever an element is re-converted, so replaying an old rule
    /// onto a much later state can find no anchor at all. Walking one step at a time in
    /// the right direction keeps every rule on the state it knows.
    /// </para>
    /// </summary>
    public static async Task<(long From, long To, int Steps)> CheckoutAsync(
        IDriver driver, string chainTarget, string ontoTs, long targetSeq)
    {
        await using var session = driver.AsyncSession();
        return await session.ExecuteWriteAsync(async tx =>
        {
            var members = await MembersAsync(tx, chainTarget);
            if (members.Count == 0)
                throw new InvalidOperationException($"no chain for target '{chainTarget}'");
            if (members.All(m => m.Seq != targetSeq))
                throw new InvalidOperationException($"seq {targetSeq} is not on the chain");

            var newestBaseline = members.Where(m => m.IsBaseline).Select(m => m.Seq)
                                        .DefaultIfEmpty(0).Max();
            if (targetSeq < newestBaseline)
                throw new InvalidOperationException(
                    $"cannot check out below the newest baseline anchor (seq {newestBaseline}) — the graph was rebuilt there");

            var head = members.Max(m => m.Seq);
            var current = await CurrentSeqAsync(tx, chainTarget) ?? head;
            var steps = 0;

            if (targetSeq > current)
            {
                foreach (var m in members
                             .Where(m => !m.IsBaseline && m.Seq > current && m.Seq <= targetSeq)
                             .OrderBy(m => m.Seq))
                {
                    await ApplyStoredAsync(tx, m, ontoTs);
                    steps++;
                }
            }
            else if (targetSeq < current)
            {
                foreach (var m in members
                             .Where(m => !m.IsBaseline && m.Seq > targetSeq && m.Seq <= current)
                             .OrderByDescending(m => m.Seq))
                {
                    await UndoStoredAsync(tx, m, ontoTs);
                    steps++;
                }
            }

            await SetCurrentSeqAsync(tx, chainTarget, targetSeq);
            return (current, targetSeq, steps);
        });
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
       m.revit_element_id AS eid, m.timestamp AS ts
ORDER BY m.seq", new { target })).ToListAsync();

        return rows.Select(r => new Member(
            r["seq"].As<long>(),
            r["baseline"].As<bool>(),
            r["op"]?.As<string>() ?? string.Empty,
            r["eid"]?.As<long?>() ?? 0,
            r["ts"].As<string>())).ToList();
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

            case "Replace":
                await DeleteOwnedAsync(tx, ontoTs, rule.ElementId);
                await MergeCopiesAsync(tx, rule.Ts + "-R", ontoTs);
                await ApplyGlueAsync(tx, rule.Ts, "R", ontoTs);
                break;

            case "Modify":
                await ApplyChangesAsync(tx, rule.Ts, ontoTs, reverse: false);
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
            // Undoing an insertion deletes BOTH by element id and by the copies' p21s:
            // a later Modify-stored rule was still APPLIED as delete+rebuild, so the
            // live graph may hold renumbered p21s (id deletion catches those), while an
            // untagged rider — a first-element containment rel — has no element id and
            // is caught by its (stable, never-reused) p21.
            case "Insert":
                await DeleteOwnedAsync(tx, ontoTs, rule.ElementId);
                await DeleteByCopyP21sAsync(tx, rule.Ts + "-R", ontoTs);
                break;

            case "Replace":
                await DeleteOwnedAsync(tx, ontoTs, rule.ElementId);
                await DeleteByCopyP21sAsync(tx, rule.Ts + "-R", ontoTs);
                await MergeCopiesAsync(tx, rule.Ts + "-L", ontoTs);
                await ApplyGlueAsync(tx, rule.Ts, "L", ontoTs);
                break;

            case "Modify":
                await ApplyChangesAsync(tx, rule.Ts, ontoTs, reverse: true);
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

        // Inline children are CREATEd (no merge key), so restoring over an existing node
        // must clear its old inline children first or they would double up.
        await tx.RunAsync(@"
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

    /// <summary>
    /// Delete exactly the nodes a stored copy namespace lists, by their preserved p21s —
    /// how an Insert is undone. Reaches untagged riders (a first-element containment rel)
    /// that element-id deletion would miss, plus their inline children (which have no p21).
    /// </summary>
    private static async Task DeleteByCopyP21sAsync(IAsyncQueryRunner tx, string copyTs, string ontoTs)
    {
        await tx.RunAsync(@"
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
            await tx.RunAsync(
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
            // The same node wears different ggifc-generated GlobalIds in different
            // graphs: the L name matches pre-modify / replayed graphs, the R name
            // matches the live graph the shallow apply rebuilt. Forward prefers the
            // before-name, undo the after-name; either falls back to the other.
            var (primary, secondary) = reverse
                ? (row["path_after"]?.As<string>(), row["path"].As<string>())
                : (row["path"].As<string>(), row["path_after"]?.As<string>());
            // Same-database fallback: the local id on the side we are moving from. A rule
            // is only ever applied to the state it was recorded against, so that id is
            // exact here even when no portable name survives (see PropertyChange).
            var localP21 = (reverse ? row["p21_after"] : row["p21_before"])?.As<string>();
            var p21 = await ResolveEitherAsync(tx, ontoTs, primary, secondary, localP21);
            var key = row["key"].As<string>();
            var value = reverse ? row["before"].As<object>() : row["after"].As<object>();

            if (row["inline"].As<bool>())
            {
                await tx.RunAsync(@"
MATCH (n:GenericNode {timestamp: $ts, p21_id: $p21})
      -[e:rel {rel_type: $key, list_index: $li}]->(v:InlineNode {timestamp: $ts})
SET v.wrappedValue = $value",
                    new { ts = ontoTs, p21 = $"#{p21}", key, li = row["list_index"].As<int>(), value });
            }
            else
            {
                await tx.RunAsync(
                    "MATCH (n:GenericNode {timestamp: $ts, p21_id: $p21}) SET n += $props",
                    new { ts = ontoTs, p21 = $"#{p21}", props = new Dictionary<string, object> { [key] = value } });
            }
        }
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

            await tx.RunAsync(@"
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

    /// <summary>
    /// A stored context string back to a p21 in the target graph: portable refs go
    /// through <see cref="ContextResolver.FindAsync"/>; raw "#n" fallbacks (the known
    /// first-geometry-element limitation) resolve literally — valid in the same
    /// database, meaningless on a foreign host.
    /// </summary>
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
}
