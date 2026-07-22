using GeometryGym.Ifc;

// ── Pipeline: DIRECT-WRITE, live incremental sync ──
// Turns one element-level change into a GraphRule, given pieces of the persistent
// session state (the ggifc db mirroring the Revit document). Deliberately Revit-API-
// free — callers (LiveSyncSession) do the Revit-side work (element lookup, converter
// dispatch, watermark capture) and hand plain ggifc values in, so this layer is fully
// testable headless.
namespace RevitGraphPlugin.Cypher;

public static class LiveRuleBuilder
{
    /// <summary>
    /// Rule for an element whose conversion just created the entities in the watermark
    /// range (before, after]: the graphlet (owned entities + any new shared context,
    /// e.g. a storey's first containment rel) plus the refreshed containment membership
    /// of <paramref name="storeys"/>. <paramref name="op"/> is <see cref="RuleOp.Insert"/>
    /// for an added element or <see cref="RuleOp.Replace"/> for a modified one (same
    /// payload — Replace also deletes the old graphlet first, keyed by
    /// <c>revit_element_id</c>; GlobalId stays stable across versions because
    /// converters derive it from Revit's UniqueId).
    /// </summary>
    public static GraphRule BuildUpsert(
        RuleOp op,
        DatabaseIfc db,
        IReadOnlyDictionary<int, long> ownerByStepId,
        IEnumerable<IfcBuildingStorey> storeys,
        long elementId,
        int watermarkBefore,
        int watermarkAfter,
        string timestamp)
    {
        if (op is not (RuleOp.Insert or RuleOp.Replace))
            throw new ArgumentException($"BuildUpsert handles Insert/Replace, not {op}.", nameof(op));

        var graphlet = GraphletExtractor.WalkNew(
            db, ownerByStepId, watermarkBefore, watermarkAfter, timestamp);
        var (refresh, delete) = GraphletExtractor.StoreyContainmentChanges(storeys, timestamp);
        return new GraphRule(op, elementId, timestamp, graphlet, refresh, delete);
    }

    /// <summary>
    /// Rule for a removed element. ggifc-side detach (<see cref="DetachFromContainment"/>)
    /// must have happened first so the containment walk reflects the shrunken membership
    /// (renumbered list_index; a memberless rel surfaces in SharedDelete).
    /// </summary>
    public static GraphRule BuildRemove(
        IEnumerable<IfcBuildingStorey> storeys, long elementId, string timestamp)
    {
        var (refresh, delete) = GraphletExtractor.StoreyContainmentChanges(storeys, timestamp);
        return new GraphRule(
            RuleOp.Remove, elementId, timestamp,
            Graphlet: Array.Empty<EntityData>(), refresh, delete);
    }

    /// <summary>
    /// ggifc-side detach of an element from its storey's shared containment rel — the
    /// in-memory mirror of the Revit deletion/modification, required before walking the
    /// containment for a Remove/Replace rule. No-op if the element is not contained.
    /// </summary>
    public static void DetachFromContainment(IfcElement element)
    {
        element.ContainedInStructure?.RelatedElements.Remove(element);
    }

    /// <summary>
    /// Drop every ownership entry of <paramref name="elementId"/> — the superseded
    /// (dead) ggifc entities of a removed/re-converted element must not be re-tagged
    /// or re-walked later. The dead entities themselves stay in the in-memory db
    /// (ggifc has no safe entity removal; accepted v1 cost, the graph never sees them).
    /// </summary>
    public static void ForgetOwnership(IDictionary<int, long> ownerByStepId, long elementId)
    {
        var stale = ownerByStepId
            .Where(kv => kv.Value == elementId)
            .Select(kv => kv.Key)
            .ToList();
        foreach (var stepId in stale)
            ownerByStepId.Remove(stepId);
    }
}
