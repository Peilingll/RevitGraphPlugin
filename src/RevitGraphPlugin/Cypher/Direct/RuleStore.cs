using Neo4j.Driver;

// ── Pipeline: DIRECT-WRITE, live incremental sync ──
// Rule persistence step 3b (doc_process/2026-08-02-plan-rule-persistence.md §6):
// write the completed rule into the :Rule chain — the professor's "stores them to
// Neo4j", the option-2 deliverable proper. Runs INSIDE the same transaction as
// ApplyRuleAsync's mutation of the current-state graph: apply and persist commit or
// fail together, or the chain and the graph would disagree about history.
//
// Storage layout (schema frozen in the plan; refinements documented there):
//   (:Rule:Node {timestamp:"<target>-rule-<seq>", seq, op, revit_element_id,
//                target_ts, deletes, inserts, changes, applied_at})
//      -[:DELETES]-> L-side copies   in namespace "<target>-rule-<seq>-L"
//      -[:INSERTS]-> R-side copies   in namespace "<target>-rule-<seq>-R"
//      -[:SETS]->    (:Change {path, key, list_index, before, after, inline})
//      -[:GLUE]->    (:Glue {context, rel_type, list_index, local_p21, side, direction})
// The current-state graph is never touched: every stored node lives under a rule
// timestamp, never under the target timestamp, so a re-baseline's per-timestamp wipe
// cannot reach the chain, and MATCH-by-target-timestamp queries see no pollution.
namespace RevitGraphPlugin.Cypher;

public static class RuleStore
{
    /// <summary>What persistence recorded for one applied rule (null = nothing stored).</summary>
    public sealed record StoredRule(long Seq, string Op, string RuleTimestamp);

    /// <summary>
    /// Persist one completed rule (L captured, context refs resolved) into the chain.
    /// A Replace is first classified by <see cref="GraphletDiff"/>:
    /// property-only → stored as a small <c>Modify</c> (op + Change rows, no copies);
    /// semantically unchanged → <b>not stored at all</b> (Revit fires modify events for
    /// changes our converters do not read — a rule that provably did nothing is noise);
    /// anything else → the full Replace with both graphlet copies. Returns what was
    /// stored, or null for the not-stored case.
    /// </summary>
    public static async Task<StoredRule?> PersistAsync(IAsyncQueryRunner tx, GraphRule rule)
    {
        IReadOnlyList<EntityData> deletes = Array.Empty<EntityData>();
        IReadOnlyList<EntityData> inserts = Array.Empty<EntityData>();
        IReadOnlyList<PropertyChange> changes = Array.Empty<PropertyChange>();
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
                // A rule that also drops shared nodes changed structure by definition —
                // never eligible for the Modify shortcut (and in practice SharedDelete
                // only fires on removals; this is belt-and-braces).
                var diff = rule.BeforeGraphlet is { Nodes.Count: > 0 } captured
                           && rule.SharedDelete.Count == 0
                    ? GraphletDiff.Compare(captured, rule.Graphlet)
                    : GraphletDiffOutcome.Structural;
                switch (diff.Kind)
                {
                    case GraphletDiffKind.NoChange:
                        return null;
                    case GraphletDiffKind.PropertyOnly:
                        op = "Modify";
                        changes = diff.Changes;
                        break;
                    default:
                        op = "Replace";
                        deletes = LSide(rule);
                        inserts = rule.Graphlet;
                        break;
                }
                break;
        }

        // Portable names of the shared nodes this rule drops — replay needs to drop
        // them in its own graph, and they cannot be found by element id (unowned).
        var sharedDelete = rule.SharedDelete
            .Select(p21Id => P21Id.TryParse(p21Id, out var p21)
                             && rule.ContextRefs.TryGetValue(p21, out var r) ? r.Path : p21Id)
            .ToList();

        var seq = await NextSeqAsync(tx, rule.Timestamp);
        var ruleTs = $"{rule.Timestamp}-rule-{seq}";

        // Graphlet copies, one sub-namespace per DPO side. Reuses the verified snapshot
        // writers with only the timestamp swapped; edges to context nodes fall out
        // naturally (BulkMergeEdges matches both ends inside the namespace) and are
        // stored portably as :Glue rows instead.
        await WriteCopies(tx, deletes, ruleTs + "-L");
        await WriteCopies(tx, inserts, ruleTs + "-R");

        await tx.RunAsync(@"
CREATE (r:Rule:Node {timestamp: $ts, seq: $seq, op: $op, revit_element_id: $eid,
                     target_ts: $target, deletes: $deletes, inserts: $inserts,
                     changes: $changes, shared_delete: $sharedDelete, applied_at: $at})",
            new
            {
                ts = ruleTs, seq, op, eid = rule.RevitElementId, target = rule.Timestamp,
                deletes = deletes.Count, inserts = inserts.Count, changes = changes.Count,
                sharedDelete, at = DateTime.UtcNow.ToString("o"),
            });

        await Link(tx, ruleTs, ruleTs + "-L", "DELETES");
        await Link(tx, ruleTs, ruleTs + "-R", "INSERTS");

        if (changes.Count > 0)
        {
            await tx.RunAsync(@"
MATCH (r:Rule {timestamp: $ts})
UNWIND $changes AS c
CREATE (ch:Change:Node {timestamp: $ts, path: c.path, key: c.key, list_index: c.list_index,
                        before: c.before, after: c.after, inline: c.inline})
CREATE (r)-[:SETS]->(ch)",
                new
                {
                    ts = ruleTs,
                    changes = changes.Select(c => (object)new Dictionary<string, object?>
                    {
                        ["path"] = c.Node.Path,
                        ["key"] = c.Key,
                        ["list_index"] = c.ListIndex,
                        ["before"] = c.Before,
                        ["after"] = c.After,
                        ["inline"] = c.Inline,
                    }).ToList(),
                });
        }

        var glue = CollectGlue(rule, deletes, inserts);
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

    /// <summary>
    /// Record a re-baseline as a <c>(:Baseline)</c> chain member (plan step 4): the
    /// current-state graph was wiped and rewritten here, so rules BEFORE this anchor
    /// cannot seamlessly replay past it — replay starts from the newest anchor. History
    /// accumulates: the wipe is per-timestamp and never reaches the chain, and the chain
    /// deliberately survives it. Runs in its own transaction — the baseline snapshot
    /// itself (<see cref="CypherEmitter.WriteAsync"/>) is already committed when this runs.
    /// </summary>
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

    /// <summary>
    /// Allocate the next chain sequence number from the per-target <c>(:RuleChain)</c>
    /// node's counter — one property read instead of a <c>max(seq)</c> scan over the
    /// whole chain. Single Revit API thread ⇒ no allocation races.
    /// </summary>
    private static async Task<long> NextSeqAsync(IAsyncQueryRunner tx, string target)
    {
        return (await (await tx.RunAsync(@"
MERGE (c:RuleChain:Node {target_ts: $target})
ON CREATE SET c.timestamp = $target + '-rules', c.next_seq = 1
SET c.next_seq = c.next_seq + 1
RETURN c.next_seq - 1 AS seq",
            new { target })).ToListAsync()).Single()["seq"].As<long>();
    }

    /// <summary>
    /// Append a member (:Rule or :Baseline) to its target's chain: move <c>[:HEAD]</c>
    /// to it and link the previous head via <c>[:NEXT]</c>. The chain is one linked
    /// list of rules and baseline anchors in application order — replay walks
    /// <c>[:NEXT]</c> from the newest <c>(:Baseline)</c>, and "latest rule" is one hop
    /// from the chain node instead of an ORDER BY over every rule.
    /// </summary>
    private static async Task AppendToChainAsync(
        IAsyncQueryRunner tx, string target, string label, string memberTs)
    {
        await tx.RunAsync($@"
MATCH (c:RuleChain {{target_ts: $target}})
MATCH (m:{label} {{timestamp: $memberTs}})
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
        // Inline copies are not linked directly — they hang off their parent node copy.
        await tx.RunAsync($@"
MATCH (r:Rule {{timestamp: $ruleTs}})
MATCH (n:GenericNode {{timestamp: $copyTs}})
MERGE (r)-[:{relType}]->(n)",
            new { ruleTs, copyTs });
    }

    /// <summary>
    /// The rule's boundary edges as portable rows: every edge with exactly one end
    /// inside a stored copy. <c>context</c> is the external end's <see cref="ContextRef"/>
    /// (falling back to the raw p21 literal if it was unresolvable — step 2's acceptance
    /// tests assert that never happens); <c>local_p21</c> is the inside end within the
    /// <c>side</c> copy namespace. A Modify stores none — structure did not change.
    /// </summary>
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

        // Incoming, L: captured with the graphlet (step 1's IncomingGlue).
        if (deletes.Count > 0 && rule.BeforeGraphlet is not null)
            foreach (var e in rule.BeforeGraphlet.IncomingGlue)
                Row(e.SourceP21, e.RelType, e.ListIndex, e.TargetP21, "L", "in");

        // Incoming, R: the shared-context refresh edges that point INTO the new graphlet
        // (e.g. the containment rel's RelatedElements membership of this element). A
        // source that is itself part of the graphlet is NOT glue — the first element on
        // a storey carries the freshly created containment rel inside its own graphlet,
        // so that edge is internal and already stored with the copies.
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
