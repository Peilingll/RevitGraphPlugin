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
