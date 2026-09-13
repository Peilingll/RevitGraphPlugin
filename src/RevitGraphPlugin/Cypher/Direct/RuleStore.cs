using Neo4j.Driver;

// ── Pipeline: DIRECT-WRITE, live incremental sync ──
// Write the completed rule into the :Rule chain, inside ApplyRuleAsync's transaction.
//
// Storage layout:
//   (:RuleChain:Node {target_ts, next_seq, checked_out_seq})-[:HEAD]->(newest member)
//   (:Baseline:Node {timestamp:"<target>-baseline-<seq>", seq, target_ts, applied_at, …})
//   (:Rule:Node {timestamp:"<target>-rule-<seq>", seq, op, revit_element_id, target_ts,
//                deletes, inserts, changes, shared_delete, applied_at,
//                aligned, renumber_from, renumber_to})
//   chain members are linked in application order by -[:NEXT]->
//      -[:DELETES]-> L-side copies   in namespace "<target>-rule-<seq>-L"
//      -[:INSERTS]-> R-side copies   in namespace "<target>-rule-<seq>-R"
//      -[:SETS]->    (:Change {path, path_after, p21_before, p21_after, key, list_index,
//                              before, after, inline})
//      -[:GLUE]->    (:Glue {context, rel_type, list_index, local_p21, side, direction})
// Every stored node lives under a rule timestamp, never the target timestamp: the
// current-state graph stays pure ConMan2 schema and a re-baseline wipe cannot reach the chain.
namespace RevitGraphPlugin.Cypher;

public static class RuleStore
{
    /// <summary>What persistence recorded for one applied rule (null = nothing stored).</summary>
    public sealed record StoredRule(long Seq, string Op, string RuleTimestamp);

    /// <summary>
    /// Persist one completed rule. A Replace stores what its diff says: NoChange → nothing
    /// (returns null), PropertyOnly → Modify (Change rows), Partial → pushout copies +
    /// changes + renumbering, Structural → both whole graphlets.
    /// </summary>
    public static async Task<StoredRule?> PersistAsync(IAsyncQueryRunner tx, GraphRule rule)
    {
        IReadOnlyList<EntityData> deletes = Array.Empty<EntityData>();
        IReadOnlyList<EntityData> inserts = Array.Empty<EntityData>();
        IReadOnlyList<PropertyChange> changes = Array.Empty<PropertyChange>();
        GraphletDiffOutcome? aligned = null;
        string op;

        switch (rule.Op)
        {
            case RuleOp.Insert:
                op = "Insert";
                inserts = rule.Graphlet;
                break;

            case RuleOp.Remove:
                op = "Remove";
                deletes = LSide(rule);
                break;

            default:
                // Aligned rules store only what moved; a rule without a diff is a full Replace.
                var diff = rule.Diff ?? GraphletDiffOutcome.Structural;
                switch (diff.Kind)
                {
                    case GraphletDiffKind.NoChange:
                        return null;
                    case GraphletDiffKind.PropertyOnly:
                        op = "Modify";
                        changes = diff.Changes;
                        aligned = diff;
                        break;
                    case GraphletDiffKind.Partial:
                        op = "Replace";
                        var pl = diff.PushoutL.ToHashSet();
                        var pr = diff.PushoutR.ToHashSet();
                        deletes = LSide(rule).Where(d => pl.Contains(d.P21)).ToList();
                        inserts = rule.Graphlet.Where(d => pr.Contains(d.P21)).ToList();
                        changes = diff.Changes;
                        aligned = diff;
                        break;
                    default:
                        op = "Replace";
                        deletes = LSide(rule);
                        inserts = rule.Graphlet;
                        break;
                }
                break;
        }

        // Portable names of the shared nodes this rule drops (unowned, so not findable by element id).
        var sharedDelete = rule.SharedDelete
            .Select(p21Id => P21Id.TryParse(p21Id, out var p21)
                             && rule.ContextRefs.TryGetValue(p21, out var r) ? r.Path : p21Id)
            .ToList();

        var seq = await NextSeqAsync(tx, rule.Timestamp);
        var ruleTs = $"{rule.Timestamp}-rule-{seq}";

        // Graphlet copies, one namespace per DPO side; edges to context are stored as :Glue rows.
        await WriteCopies(tx, deletes, ruleTs + "-L");
        await WriteCopies(tx, inserts, ruleTs + "-R");

        // Interface renumbering (aligned rules): two parallel arrays, Neo4j properties cannot nest.
        var renumberFrom = aligned?.Match.Select(kv => P21Id.Of(kv.Key)).ToList() ?? new List<string>();
        var renumberTo   = aligned?.Match.Select(kv => P21Id.Of(kv.Value)).ToList() ?? new List<string>();

        await tx.RunAsync(@"
CREATE (r:Rule:Node {timestamp: $ts, seq: $seq, op: $op, revit_element_id: $eid,
                     target_ts: $target, deletes: $deletes, inserts: $inserts,
                     changes: $changes, shared_delete: $sharedDelete, applied_at: $at,
                     aligned: $isAligned, renumber_from: $renumberFrom, renumber_to: $renumberTo})",
            new
            {
                ts = ruleTs, seq, op, eid = rule.RevitElementId, target = rule.Timestamp,
                deletes = deletes.Count, inserts = inserts.Count, changes = changes.Count,
                sharedDelete, at = DateTime.UtcNow.ToString("o"),
                isAligned = aligned is not null, renumberFrom, renumberTo,
            });

        await Link(tx, ruleTs, ruleTs + "-L", "DELETES");
        await Link(tx, ruleTs, ruleTs + "-R", "INSERTS");

        if (changes.Count > 0)
        {
            await tx.RunAsync(@"
MATCH (r:Rule {timestamp: $ts})
UNWIND $changes AS c
CREATE (ch:Change:Node {timestamp: $ts, path: c.path, path_after: c.path_after,
                        p21_before: c.p21_before, p21_after: c.p21_after,
                        key: c.key, list_index: c.list_index,
                        before: c.before, after: c.after, inline: c.inline})
CREATE (r)-[:SETS]->(ch)",
                new
                {
                    ts = ruleTs,
                    changes = changes.Select(c => (object)new Dictionary<string, object?>
                    {
                        ["path"] = c.Node.Path,
                        ["path_after"] = c.NodeAfter.Path,
                        ["p21_before"] = P21Id.Of(c.P21Before),
                        ["p21_after"] = P21Id.Of(c.P21After),
                        ["key"] = c.Key,
                        ["list_index"] = c.ListIndex,
                        ["before"] = c.Before,
                        ["after"] = c.After,
                        ["inline"] = c.Inline,
                    }).ToList(),
                });
        }

        var glue = aligned is not null
            ? CollectAlignedGlue(rule, aligned, deletes, inserts)
            : CollectGlue(rule, deletes, inserts);
        if (glue.Count > 0)
        {
            await tx.RunAsync(@"
MATCH (r:Rule {timestamp: $ts})
UNWIND $glue AS g
CREATE (x:Glue:Node {timestamp: $ts, context: g.context, rel_type: g.rel_type,
                     list_index: g.list_index, local_p21: g.local_p21,
                     side: g.side, direction: g.direction})
CREATE (r)-[:GLUE]->(x)",
                new { ts = ruleTs, glue });
        }

        await AppendToChainAsync(tx, rule.Timestamp, "Rule", ruleTs);

        return new StoredRule(seq, op, ruleTs);
    }

    /// <summary>What a re-baseline anchored into the chain.</summary>
    public sealed record BaselineAnchor(long Seq, string Timestamp);

    /// <summary>Record a re-baseline as a <c>(:Baseline)</c> chain member: the graph was rebuilt here, so replay starts from the newest anchor.</summary>
    public static async Task<BaselineAnchor> RecordBaselineAsync(
        IDriver driver, string targetTs, CypherEmitter.EmitStats stats)
    {
        await using var session = driver.AsyncSession();
        return await session.ExecuteWriteAsync(async tx =>
        {
            var seq = await NextSeqAsync(tx, targetTs);
            var ts = $"{targetTs}-baseline-{seq}";
            await tx.RunAsync(@"
CREATE (b:Baseline:Node {timestamp: $ts, seq: $seq, target_ts: $target, applied_at: $at,
                         primary_nodes: $primary, edges: $edges})",
                new
                {
                    ts, seq, target = targetTs, at = DateTime.UtcNow.ToString("o"),
                    primary = stats.PrimaryNodes, edges = stats.Edges,
                });
            await AppendToChainAsync(tx, targetTs, "Baseline", ts);
            return new BaselineAnchor(seq, ts);
        });
    }

    /// <summary>Next chain sequence number from the <c>(:RuleChain)</c> counter.</summary>
    private static async Task<long> NextSeqAsync(IAsyncQueryRunner tx, string target)
    {
        return (await (await tx.RunAsync(@"
MERGE (c:RuleChain:Node {target_ts: $target})
ON CREATE SET c.timestamp = $target + '-rules', c.next_seq = 1
SET c.next_seq = c.next_seq + 1
RETURN c.next_seq - 1 AS seq",
            new { target })).ToListAsync()).Single()["seq"].As<long>();
    }

    /// <summary>Append a member (:Rule or :Baseline) to the chain: move <c>[:HEAD]</c>, link the previous head via <c>[:NEXT]</c>, set <c>checked_out_seq</c>.</summary>
    private static async Task AppendToChainAsync(
        IAsyncQueryRunner tx, string target, string label, string memberTs)
    {
        await tx.RunAsync($@"
MATCH (c:RuleChain {{target_ts: $target}})
MATCH (m:{label} {{timestamp: $memberTs}})
SET c.checked_out_seq = m.seq
OPTIONAL MATCH (c)-[h:HEAD]->(prev)
DELETE h
CREATE (c)-[:HEAD]->(m)
WITH m, prev
WHERE prev IS NOT NULL
CREATE (prev)-[:NEXT]->(m)",
            new { target, memberTs });
    }

    /// <summary>The full DPO L side: the element-owned capture plus the shared nodes the rule drops.</summary>
    private static IReadOnlyList<EntityData> LSide(GraphRule rule)
    {
        if (rule.BeforeGraphlet is not { } captured) return Array.Empty<EntityData>();
        return captured.SharedDeletedOrEmpty.Count == 0
            ? captured.Nodes
            : captured.Nodes.Concat(captured.SharedDeletedOrEmpty).ToList();
    }

    private static async Task WriteCopies(
        IAsyncQueryRunner tx, IReadOnlyList<EntityData> nodes, string ts)
    {
        if (nodes.Count == 0) return;

        var stamped = nodes
            .Select(d => d with
            {
                Properties = new Dictionary<string, object>(d.Properties) { ["timestamp"] = ts },
            })
            .ToList();

        foreach (var kind in new[] { NodeKind.Primary, NodeKind.Connection, NodeKind.Secondary })
            await CypherEmitter.BulkMergeNodes(tx, kind, stamped.Where(d => d.Kind == kind).ToList());
        await CypherEmitter.BulkMergeEdges(tx, stamped.SelectMany(d => d.Edges).ToList(), ts);
        await CypherEmitter.BulkCreateInlines(tx, stamped.SelectMany(d => d.Inlines).ToList(), ts);
    }

    private static async Task Link(IAsyncQueryRunner tx, string ruleTs, string copyTs, string relType)
    {
        // Inline copies hang off their parent node copy.
        await tx.RunAsync($@"
MATCH (r:Rule {{timestamp: $ruleTs}})
MATCH (n:GenericNode {{timestamp: $copyTs}})
MERGE (r)-[:{relType}]->(n)",
            new { ruleTs, copyTs });
    }

    /// <summary>Glue of an aligned rule: every edge crossing the pushout boundary. Context (including interface nodes) is named by L p21, so R-side interface ends are translated first.</summary>
    private static List<object> CollectAlignedGlue(
        GraphRule rule, GraphletDiffOutcome diff,
        IReadOnlyList<EntityData> deletes, IReadOnlyList<EntityData> inserts)
    {
        var rows = new List<object>();
        var toL = diff.Match.ToDictionary(kv => kv.Value, kv => kv.Key);
        var pushoutL = diff.PushoutL.ToHashSet();
        var pushoutR = diff.PushoutR.ToHashSet();
        int GraphP21(int rp21) => toL.TryGetValue(rp21, out var lp21) ? lp21 : rp21;

        string Context(int graphP21)
            => rule.ContextRefs.TryGetValue(graphP21, out var r) ? r.Path : $"#{graphP21}";

        void Row(int contextGraphP21, string relType, int listIndex, int localP21, string side, string direction)
            => rows.Add(new Dictionary<string, object>
            {
                ["context"] = Context(contextGraphP21),
                ["rel_type"] = relType,
                ["list_index"] = listIndex,
                ["local_p21"] = $"#{localP21}",
                ["side"] = side,
                ["direction"] = direction,
            });

        // L side: pushout → outside (out); outside → pushout (in).
        foreach (var d in deletes)
            foreach (var e in d.Edges)
                if (!pushoutL.Contains(e.TargetP21))
                    Row(e.TargetP21, e.RelType, e.ListIndex, e.SourceP21, "L", "out");
        if (rule.BeforeGraphlet is { } captured)
        {
            foreach (var d in captured.Nodes)
                if (!pushoutL.Contains(d.P21))
                    foreach (var e in d.Edges)
                        if (pushoutL.Contains(e.TargetP21))
                            Row(d.P21, e.RelType, e.ListIndex, e.TargetP21, "L", "in");
            foreach (var e in captured.IncomingGlue)
                if (pushoutL.Contains(e.TargetP21))
                    Row(e.SourceP21, e.RelType, e.ListIndex, e.TargetP21, "L", "in");
        }

        // R side: same, interface ends translated to the graph's p21.
        foreach (var d in inserts)
            foreach (var e in d.Edges)
                if (!pushoutR.Contains(e.TargetP21))
                    Row(GraphP21(e.TargetP21), e.RelType, e.ListIndex, e.SourceP21, "R", "out");
        foreach (var d in rule.Graphlet)
            if (!pushoutR.Contains(d.P21))
                foreach (var e in d.Edges)
                    if (pushoutR.Contains(e.TargetP21))
                        Row(GraphP21(d.P21), e.RelType, e.ListIndex, e.TargetP21, "R", "in");
        foreach (var shared in rule.SharedRefresh)
            foreach (var e in shared.Edges)
                if (pushoutR.Contains(e.TargetP21))
                    Row(e.SourceP21, e.RelType, e.ListIndex, e.TargetP21, "R", "in");

        return rows;
    }

    /// <summary>Glue of an unaligned rule: every edge with exactly one end inside a stored copy; <c>context</c> is the external end's <see cref="ContextRef"/>, <c>local_p21</c> the inside end.</summary>
    private static List<object> CollectGlue(
        GraphRule rule, IReadOnlyList<EntityData> deletes, IReadOnlyList<EntityData> inserts)
    {
        var rows = new List<object>();

        string Context(int p21)
            => rule.ContextRefs.TryGetValue(p21, out var r) ? r.Path : $"#{p21}";

        void Row(int contextP21, string relType, int listIndex, int localP21, string side, string direction)
            => rows.Add(new Dictionary<string, object>
            {
                ["context"] = Context(contextP21),
                ["rel_type"] = relType,
                ["list_index"] = listIndex,
                ["local_p21"] = $"#{localP21}",
                ["side"] = side,
                ["direction"] = direction,
            });

        void OutgoingOf(IReadOnlyList<EntityData> side, string tag)
        {
            var own = side.Select(d => d.P21).ToHashSet();
            foreach (var d in side)
                foreach (var e in d.Edges)
                    if (!own.Contains(e.TargetP21))
                        Row(e.TargetP21, e.RelType, e.ListIndex, e.SourceP21, tag, "out");
        }

        OutgoingOf(deletes, "L");
        OutgoingOf(inserts, "R");

        // Incoming, L: captured with the graphlet (GraphletCapture.IncomingGlue).
        if (deletes.Count > 0 && rule.BeforeGraphlet is not null)
            foreach (var e in rule.BeforeGraphlet.IncomingGlue)
                Row(e.SourceP21, e.RelType, e.ListIndex, e.TargetP21, "L", "in");

        // Incoming, R: shared-refresh edges into the new graphlet (a rel inside the
        // graphlet itself is not glue — it is stored with the copies).
        if (inserts.Count > 0)
        {
            var own = inserts.Select(d => d.P21).ToHashSet();
            foreach (var shared in rule.SharedRefresh)
                foreach (var e in shared.Edges)
                    if (own.Contains(e.TargetP21) && !own.Contains(e.SourceP21))
                        Row(e.SourceP21, e.RelType, e.ListIndex, e.TargetP21, "R", "in");
        }

        return rows;
    }
}
