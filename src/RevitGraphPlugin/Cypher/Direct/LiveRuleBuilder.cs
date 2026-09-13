using GeometryGym.Ifc;

// ── Pipeline: DIRECT-WRITE, live incremental sync ──
// Turns one element change into a GraphRule from the ggifc mirror. Revit-API-free:
// LiveSyncSession does the Revit side (lookup, conversion, watermarks).
namespace RevitGraphPlugin.Cypher;

public static class LiveRuleBuilder
{
    /// <summary>
    /// Insert / Replace rule for an element whose conversion just created the entities in
    /// (before, after]: that graphlet plus the refreshed spatial rels.
    /// </summary>
    public static GraphRule BuildUpsert(
        RuleOp op,
        DatabaseIfc db,
        IReadOnlyDictionary<int, long> ownerByStepId,
        IEnumerable<IfcBuildingStorey> storeys,
        long elementId,
        int watermarkBefore,
        int watermarkAfter,
        string timestamp,
        IfcBuilding? building = null)
    {
        if (op is not (RuleOp.Insert or RuleOp.Replace))
            throw new ArgumentException($"BuildUpsert handles Insert/Replace, not {op}.", nameof(op));

        var graphlet = GraphletExtractor.WalkNew(
            db, ownerByStepId, watermarkBefore, watermarkAfter, timestamp);
        var (refresh, delete) = GraphletExtractor.SpatialChanges(storeys, building, timestamp);
        return new GraphRule(op, elementId, timestamp, graphlet, refresh, delete);
    }

    /// <summary>Rule for a level modified in place: the storey's owned entities re-walked with current values and unchanged p21s, so the diff stores a Modify.</summary>
    public static GraphRule BuildLevelModify(
        DatabaseIfc db,
        IReadOnlyDictionary<int, long> ownerByStepId,
        IEnumerable<IfcBuildingStorey> storeys,
        IfcBuilding building,
        long levelId,
        string timestamp)
    {
        var graphlet = ownerByStepId
            .Where(kv => kv.Value == levelId)
            .OrderBy(kv => kv.Key)
            .Select(kv => db[kv.Key])
            .Where(e => e is { StepId: > 0 })
            .Select(e => CypherEmitter.WalkOwned(e!, timestamp, ownerByStepId))
            .ToList();
        var (refresh, delete) = GraphletExtractor.SpatialChanges(storeys, building, timestamp);
        return new GraphRule(RuleOp.Replace, levelId, timestamp, graphlet, refresh, delete);
    }

    /// <summary>Remove rule. Call <see cref="DetachFromContainment"/> first so the containment walk reflects the shrunken membership.</summary>
    public static GraphRule BuildRemove(
        IEnumerable<IfcBuildingStorey> storeys, long elementId, string timestamp,
        IfcBuilding? building = null, IEnumerable<string>? alsoDelete = null)
    {
        var (refresh, delete) = GraphletExtractor.SpatialChanges(storeys, building, timestamp);
        if (alsoDelete is not null) delete.AddRange(alsoDelete);
        return new GraphRule(
            RuleOp.Remove, elementId, timestamp,
            Graphlet: Array.Empty<EntityData>(), refresh, delete);
    }

    /// <summary>Detach a storey from its building in the mirror; returns the p21s of its containment rels (to be dropped).</summary>
    public static List<string> DetachStorey(IfcBuildingStorey storey)
    {
        storey.Decomposes?.RelatedObjects.Remove(storey);
        return storey.ContainsElements
            .Where(rel => rel.StepId > 0)
            .Select(rel => $"#{rel.StepId}")
            .ToList();
    }

    /// <summary>Detach an element from its storey's containment rel in the mirror. No-op if not contained.</summary>
    public static void DetachFromContainment(IfcElement element)
    {
        element.ContainedInStructure?.RelatedElements.Remove(element);
    }

    /// <summary>Drop every ownership entry of <paramref name="elementId"/>. The dead ggifc entities stay in the db (ggifc has no safe removal) but are never walked again.</summary>
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
