using GeometryGym.Ifc;

// ── Pipeline: DIRECT-WRITE, live incremental sync ──
// One Revit change = one GraphRule (Esser 2022 §3.4: graphlet + context + glue).
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

/// <summary>One graph transformation rule, derived from a single Revit element change.</summary>
/// <param name="Op">Insert / Remove / Replace.</param>
/// <param name="RevitElementId">The Revit element the rule is about (ownership key).</param>
/// <param name="Timestamp">Graph version scope the rule applies to.</param>
/// <param name="Graphlet">Nodes to insert: the element-owned entities plus any new shared entities its conversion created. Empty for Remove.</param>
/// <param name="SharedRefresh">Existing shared entities (containment / aggregation rels) whose edge set is replaced from a fresh walk.</param>
/// <param name="SharedDelete">p21_ids of shared nodes to drop entirely (e.g. a containment rel left memberless).</param>
public sealed record GraphRule(
    RuleOp Op,
    long RevitElementId,
    string Timestamp,
    IReadOnlyList<EntityData> Graphlet,
    IReadOnlyList<EntityData> SharedRefresh,
    IReadOnlyList<string> SharedDelete)
{
    /// <summary>
    /// The DPO L side: what this rule destroyed, captured from the graph by
    /// <see cref="CypherEmitter.ApplyRuleAsync"/> just before the delete. Null until
    /// then, and always null for Insert.
    /// </summary>
    public GraphletCapture? BeforeGraphlet { get; init; }

    /// <summary>
    /// Portable names (<see cref="ContextRef"/>) for every p21 the rule references but
    /// does not own, resolved by <see cref="CypherEmitter.ApplyRuleAsync"/> before the
    /// rule mutates anything. The apply uses p21 directly; the stored rule uses these.
    /// </summary>
    public IReadOnlyDictionary<int, ContextRef> ContextRefs { get; init; }
        = new Dictionary<int, ContextRef>();

    /// <summary>What <see cref="RuleStore"/> recorded; null before persistence or when a Replace diffed to NoChange.</summary>
    public RuleStore.StoredRule? Stored { get; init; }

    /// <summary>How <see cref="GraphletDiff"/> aligned this Replace's L and R sides; null for Insert / Remove and for an unaligned Replace.</summary>
    public GraphletDiffOutcome? Diff { get; init; }

    /// <summary>Split every p21 the rule mentions into Own (its L / R graphlets) and External (context, shared nodes) — only External needs portable names.</summary>
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

            // Dropped shared nodes stay external (replay must find them by name).
            foreach (var data in BeforeGraphlet.SharedDeletedOrEmpty)
            {
                mentioned.Add(data.P21);
                foreach (var edge in data.Edges) Mention(edge);
            }
        }

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
    /// Entity types that are shared context, never owned by one element: ggifc creates
    /// the storey's containment rel with the first element on it, but every later element
    /// joins the same rel. The registry skips ownership-tagging these.
    /// </summary>
    public static readonly HashSet<string> SharedResourceTypes = new(StringComparer.Ordinal)
    {
        nameof(IfcRelContainedInSpatialStructure),
        nameof(IfcRelAggregates),   // building → storeys, created by ggifc with the first storey
    };
}

/// <summary>Builds <see cref="GraphRule"/> payload pieces from the ggifc database (Revit-free).</summary>
public static class GraphletExtractor
{
    /// <summary>Walk only the entities in (<paramref name="watermarkBefore"/>, <paramref name="watermarkAfter"/>] — the graphlet one conversion just created. O(graphlet).</summary>
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
    /// The storeys' containment rels: rels with members are walked fresh into Refresh;
    /// memberless rels (invalid IFC, ggifc will not serialize them) go to Delete.
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

    /// <summary><see cref="StoreyContainmentChanges"/> plus the building's aggregation rel (joined / left by level inserts and removes), same semantics.</summary>
    public static (List<EntityData> Refresh, List<string> Delete) SpatialChanges(
        IEnumerable<IfcBuildingStorey> storeys, IfcBuilding? building, string timestamp)
    {
        var (refresh, delete) = StoreyContainmentChanges(storeys, timestamp);
        if (building is not null)
            foreach (var rel in building.IsDecomposedBy)
            {
                if (rel.StepId <= 0) continue;
                if (rel.RelatedObjects.Count == 0)
                    delete.Add($"#{rel.StepId}");
                else
                    refresh.Add(EntityWalker.Walk(rel, timestamp));
            }
        return (refresh, delete);
    }
}
