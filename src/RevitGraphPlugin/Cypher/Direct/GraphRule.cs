using GeometryGym.Ifc;

// ── Pipeline: DIRECT-WRITE (Revit → ggifc tree → Cypher → Neo4j; no temp IFC) ──
// Incremental sync core: one Revit change = one GraphRule (a graph transformation
// rule in the sense of Esser 2022 §3.4 — graphlet + context + glue edges).
// This record is the option-2 persistence payload: today it is generated and applied
// to the current-state graph; a later stage will additionally store it.
namespace RevitGraphPlugin.Cypher;

/// <summary>The change kind a <see cref="GraphRule"/> describes.</summary>
public enum RuleOp
{
    /// <summary>Element added: insert its graphlet + glue edges.</summary>
    Insert,
    /// <summary>Element removed: delete its graphlet, repair shared context.</summary>
    Remove,
    /// <summary>Element modified: replace its graphlet (Remove + Insert, one transaction).</summary>
    Replace,
}

/// <summary>
/// One graph transformation rule, derived from a single Revit element change.
/// </summary>
/// <param name="Op">Insert / Remove / Replace.</param>
/// <param name="RevitElementId">The Revit element the rule is about (ownership key).</param>
/// <param name="Timestamp">Graph version scope the rule applies to.</param>
/// <param name="Graphlet">
/// Nodes to insert (walked <see cref="EntityData"/>): the element-owned entities plus
/// any NEW shared entities its conversion created (e.g. the storey's first containment
/// rel). Empty for <see cref="RuleOp.Remove"/>.
/// </param>
/// <param name="SharedRefresh">
/// Existing shared entities whose outgoing edge set must be replaced wholesale from a
/// fresh walk (e.g. the containment rel after a member joined or left): old edges are
/// deleted, walked edges re-created — list_index renumbers naturally to match a fresh
/// snapshot. Node properties are MERGEd too.
/// </param>
/// <param name="SharedDelete">
/// p21_ids of shared nodes that must go entirely (e.g. a containment rel whose last
/// member was removed — a fresh export of that state would not contain it).
/// </param>
public sealed record GraphRule(
    RuleOp Op,
    long RevitElementId,
    string Timestamp,
    IReadOnlyList<EntityData> Graphlet,
    IReadOnlyList<EntityData> SharedRefresh,
    IReadOnlyList<string> SharedDelete)
{
    /// <summary>
    /// The DPO <b>L</b> side: what this rule destroyed, captured from the current-state
    /// graph inside the rule's own transaction just before the delete
    /// (<see cref="CypherEmitter.ApplyRuleAsync"/> fills it in and returns the completed
    /// rule). <c>null</c> until then, and always <c>null</c> for
    /// <see cref="RuleOp.Insert"/> — an insert destroys nothing.
    /// <para>
    /// Rule building cannot produce this: <see cref="RuleOp.Remove"/> starts from an
    /// element that no longer exists in Revit, and by rule-build time the ggifc mirror
    /// has already been detached and un-owned. Without it the rule chain would only be
    /// replayable forwards — no undo, and no L pattern for a receiver to match against
    /// (Esser 2022 §3.4). See doc_process/2026-08-02-plan-rule-persistence.md §2.
    /// </para>
    /// </summary>
    public GraphletCapture? BeforeGraphlet { get; init; }

    /// <summary>
    /// Portable names for every p21 the rule references but does not own, keyed by that
    /// p21 (see <see cref="PartitionReferences"/>). Filled in by
    /// <see cref="CypherEmitter.ApplyRuleAsync"/> from the host graph, before the rule
    /// mutates anything.
    /// <para>
    /// The apply path deliberately keeps using p21 — it runs against the very graph the
    /// rule was built from, where p21 is exact and free. Portability is a property of the
    /// <em>stored</em> rule: a p21 means nothing in another version or another host graph,
    /// so persistence writes these refs instead. A reference that could not be resolved is
    /// simply absent.
    /// </para>
    /// </summary>
    public IReadOnlyDictionary<int, ContextRef> ContextRefs { get; init; }
        = new Dictionary<int, ContextRef>();

    /// <summary>
    /// What <see cref="RuleStore"/> recorded for this rule, set by
    /// <see cref="CypherEmitter.ApplyRuleAsync"/> on the returned rule. <c>null</c>
    /// before persistence ran — or when it deliberately stored nothing (a Replace that
    /// <see cref="GraphletDiff"/> proved semantically empty).
    /// </summary>
    public RuleStore.StoredRule? Stored { get; init; }

    /// <summary>
    /// Split every p21 the rule mentions into <c>Own</c> (nodes the rule itself carries —
    /// its R graphlet and, once captured, its L graphlet) and <c>External</c> (everything
    /// else: context it glues to, shared nodes it refreshes or deletes). Only the external
    /// set needs portable naming; inside the rule p21 is a local name.
    /// </summary>
    public (IReadOnlyCollection<int> External, IReadOnlySet<int> Own) PartitionReferences()
    {
        var own = new HashSet<int>();
        foreach (var data in Graphlet) own.Add(data.P21);
        if (BeforeGraphlet is not null)
            foreach (var data in BeforeGraphlet.Nodes) own.Add(data.P21);

        var mentioned = new HashSet<int>();
        void Mention(EdgeData edge) { mentioned.Add(edge.SourceP21); mentioned.Add(edge.TargetP21); }

        foreach (var data in Graphlet)
            foreach (var edge in data.Edges) Mention(edge);

        if (BeforeGraphlet is not null)
        {
            foreach (var data in BeforeGraphlet.Nodes)
                foreach (var edge in data.Edges) Mention(edge);
            foreach (var edge in BeforeGraphlet.IncomingGlue) Mention(edge);

            // Shared nodes the rule drops stay EXTERNAL (they need portable names of
            // their own — replay must find and drop them in its graph), but their edge
            // ends are mentioned so the stored glue can name e.g. the storey behind a
            // dropped containment rel.
            foreach (var data in BeforeGraphlet.SharedDeletedOrEmpty)
            {
                mentioned.Add(data.P21);
                foreach (var edge in data.Edges) Mention(edge);
            }
        }

        // Shared context the rule rewrites: the rel node itself plus whatever it now points
        // at (members of OTHER elements — external from this rule's point of view).
        foreach (var data in SharedRefresh)
        {
            mentioned.Add(data.P21);
            foreach (var edge in data.Edges) Mention(edge);
        }

        foreach (var p21Id in SharedDelete)
            if (P21Id.TryParse(p21Id, out var p21)) mentioned.Add(p21);

        mentioned.ExceptWith(own);
        return (mentioned, own);
    }

    /// <summary>
    /// IFC entity types that are shared context, never owned by a single element even
    /// when an element's conversion happens to create them: ggifc creates the storey's
    /// containment rel while converting the FIRST element on that storey, but every
    /// later element joins the same rel — tagging it to the first element would let
    /// that element's removal tear out the containment of all others (the paper's
    /// shared-resources rule). The registry skips ownership-tagging these types.
    /// </summary>
    public static readonly HashSet<string> SharedResourceTypes = new(StringComparer.Ordinal)
    {
        nameof(IfcRelContainedInSpatialStructure),
    };
}

/// <summary>
/// Builds <see cref="GraphRule"/> payload pieces from a walked ggifc database.
/// Kept separate from rule application (CypherEmitter) so the same extraction feeds
/// both "apply now" (current-state graph) and, later, "store the rule" (option 2).
/// </summary>
public static class GraphletExtractor
{
    /// <summary>
    /// Extract the insertion payload for one element from a full walk: every entity
    /// whose StepId lies in (<paramref name="watermarkBefore"/>, <paramref name="watermarkAfter"/>]
    /// — the range its conversion created (owned entities carry <c>revit_element_id</c>
    /// via the ownership map; new shared entities, e.g. a first containment rel, ride
    /// along untagged).
    /// </summary>
    public static List<EntityData> NewEntities(
        IEnumerable<EntityData> walked, int watermarkBefore, int watermarkAfter)
    {
        return walked
            .Where(d => d.P21 > watermarkBefore && d.P21 <= watermarkAfter)
            .ToList();
    }

    /// <summary>
    /// Walk only the entities in the watermark range (<paramref name="watermarkBefore"/>,
    /// <paramref name="watermarkAfter"/>] — the graphlet one element's conversion just
    /// created — with ownership stamping. O(graphlet) instead of walking the whole
    /// database; the live path calls this once per changed element.
    /// </summary>
    public static List<EntityData> WalkNew(
        DatabaseIfc db,
        IReadOnlyDictionary<int, long> ownerByStepId,
        int watermarkBefore,
        int watermarkAfter,
        string timestamp)
    {
        var result = new List<EntityData>();
        for (var id = watermarkBefore + 1; id <= watermarkAfter; id++)
        {
            if (db[id] is { StepId: > 0 } entity)
                result.Add(CypherEmitter.WalkOwned(entity, timestamp, ownerByStepId));
        }
        return result;
    }

    /// <summary>
    /// Collect the shared containment rels of the given storeys for a <see cref="GraphRule"/>:
    /// rels with members are freshly walked into <c>Refresh</c> (membership edges carry
    /// list_index renumbered from ggifc's CURRENT list, so applying them reproduces exactly
    /// what a fresh snapshot would hold); rels whose member list is empty go to
    /// <c>Delete</c> as p21_ids — ggifc refuses to serialize a memberless rel (invalid
    /// IFC: RelatedElements is SET [1:?]), and a fresh export of that state would not
    /// contain it, so the graph node must be dropped.
    /// </summary>
    public static (List<EntityData> Refresh, List<string> Delete) StoreyContainmentChanges(
        IEnumerable<IfcBuildingStorey> storeys, string timestamp)
    {
        var refresh = new List<EntityData>();
        var delete = new List<string>();
        foreach (var storey in storeys.Distinct())
        {
            foreach (var rel in storey.ContainsElements)
            {
                if (rel.StepId <= 0) continue;
                if (rel.RelatedElements.Count == 0)
                    delete.Add($"#{rel.StepId}");
                else
                    refresh.Add(EntityWalker.Walk(rel, timestamp));
            }
        }
        return (refresh, delete);
    }
}
