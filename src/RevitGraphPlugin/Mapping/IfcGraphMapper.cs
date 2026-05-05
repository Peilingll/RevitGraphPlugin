using GeometryGym.Ifc;
using RevitGraphPlugin.Graph;

namespace RevitGraphPlugin.Mapping;

/// <summary>
/// Walks an in-memory IFC entity tree (GeometryGym.Ifc) and produces a
/// <see cref="GraphBatch"/> matching ConMan2's schema (related-work.md §2).
///
/// Stage 2 scope (per design.md §4 Stage 2): IfcWall, IfcWindow,
/// IfcOpeningElement, plus the two relations that connect them
/// (IfcRelVoidsElement, IfcRelFillsElement) and their immediate
/// secondary references (IfcLocalPlacement, IfcProductDefinitionShape).
/// Hand-coded per entity — deliberately not generalised, per spec.
///
/// Edges follow STEP-21 forward attributes only. Inverse attributes
/// (e.g. <c>IfcElement.HasOpenings</c>) are NOT materialised; they are
/// derivable by a single Cypher hop through the connection node.
/// </summary>
public static class IfcGraphMapper
{
    private const int Timestamp = 0; // design.md §6.3: literal 0 reserves the field.

    public static GraphBatch Map(BaseClassIfc root)
    {
        var ctx = new MapContext();
        Visit(root, ctx);
        return new GraphBatch(ctx.Nodes, ctx.Edges);
    }

    /// <summary>
    /// Walks every entity in <paramref name="db"/>. Used by the Stage 2 test
    /// fixture and as a starting point for full-document sync; Stage 4's
    /// incremental handler will instead invoke <see cref="Map(BaseClassIfc)"/>
    /// per changed element, plus any newly-added <c>IfcRelationship</c>s.
    /// </summary>
    public static GraphBatch MapAll(DatabaseIfc db)
    {
        var ctx = new MapContext();
        foreach (var entity in db.OfType<BaseClassIfc>())
            Visit(entity, ctx);
        return new GraphBatch(ctx.Nodes, ctx.Edges);
    }

    private sealed class MapContext
    {
        public readonly List<GraphNode> Nodes = new();
        public readonly List<GraphEdge> Edges = new();
        public readonly HashSet<int> Visited = new();
    }

    /// <summary>
    /// Visits an entity if not already seen, returns its <c>StepId</c>.
    /// Returns <c>-1</c> for null inputs (caller should skip the edge).
    /// </summary>
    private static int Visit(BaseClassIfc? entity, MapContext ctx)
    {
        if (entity is null) return -1;
        var id = entity.StepId;
        if (!ctx.Visited.Add(id)) return id;

        switch (entity)
        {
            case IfcWall w:             EmitWall(w, ctx); break;
            case IfcWindow win:         EmitWindow(win, ctx); break;
            case IfcOpeningElement op:  EmitOpening(op, ctx); break;
            case IfcRelVoidsElement rv: EmitRelVoids(rv, ctx); break;
            case IfcRelFillsElement rf: EmitRelFills(rf, ctx); break;
            default:                    EmitSecondary(entity.GetType().Name, entity, ctx); break;
        }

        return id;
    }

    private static void EmitWall(IfcWall w, MapContext ctx)
    {
        var props = new Dictionary<string, object?>
        {
            ["Name"] = PropertyNormaliser.Normalise(w.Name),
            ["Description"] = PropertyNormaliser.Normalise(w.Description),
            ["PredefinedType"] = PropertyNormaliser.Normalise(w.PredefinedType.ToString()),
        };
        ctx.Nodes.Add(new GraphNode(GraphNodeKind.Primary, "IfcWall", w.StepId, Timestamp, w.GlobalId, props));
        EmitProductReferences(w, ctx);
    }

    private static void EmitWindow(IfcWindow w, MapContext ctx)
    {
        var props = new Dictionary<string, object?>
        {
            ["Name"] = PropertyNormaliser.Normalise(w.Name),
            ["Description"] = PropertyNormaliser.Normalise(w.Description),
            ["OverallHeight"] = PropertyNormaliser.Normalise(w.OverallHeight),
            ["OverallWidth"] = PropertyNormaliser.Normalise(w.OverallWidth),
            ["PredefinedType"] = PropertyNormaliser.Normalise(w.PredefinedType.ToString()),
        };
        ctx.Nodes.Add(new GraphNode(GraphNodeKind.Primary, "IfcWindow", w.StepId, Timestamp, w.GlobalId, props));
        EmitProductReferences(w, ctx);
    }

    private static void EmitOpening(IfcOpeningElement o, MapContext ctx)
    {
        var props = new Dictionary<string, object?>
        {
            ["Name"] = PropertyNormaliser.Normalise(o.Name),
            ["Description"] = PropertyNormaliser.Normalise(o.Description),
            ["PredefinedType"] = PropertyNormaliser.Normalise(o.PredefinedType.ToString()),
        };
        ctx.Nodes.Add(new GraphNode(GraphNodeKind.Primary, "IfcOpeningElement", o.StepId, Timestamp, o.GlobalId, props));
        EmitProductReferences(o, ctx);
    }

    private static void EmitRelVoids(IfcRelVoidsElement r, MapContext ctx)
    {
        var props = new Dictionary<string, object?>
        {
            ["Name"] = PropertyNormaliser.Normalise(r.Name),
            ["Description"] = PropertyNormaliser.Normalise(r.Description),
        };
        ctx.Nodes.Add(new GraphNode(GraphNodeKind.Connection, "IfcRelVoidsElement", r.StepId, Timestamp, r.GlobalId, props));

        var bldg = Visit(r.RelatingBuildingElement, ctx);
        if (bldg != -1) ctx.Edges.Add(new GraphEdge(r.StepId, bldg, "RelatingBuildingElement", -1));
        var op = Visit(r.RelatedOpeningElement, ctx);
        if (op != -1) ctx.Edges.Add(new GraphEdge(r.StepId, op, "RelatedOpeningElement", -1));
    }

    private static void EmitRelFills(IfcRelFillsElement r, MapContext ctx)
    {
        var props = new Dictionary<string, object?>
        {
            ["Name"] = PropertyNormaliser.Normalise(r.Name),
            ["Description"] = PropertyNormaliser.Normalise(r.Description),
        };
        ctx.Nodes.Add(new GraphNode(GraphNodeKind.Connection, "IfcRelFillsElement", r.StepId, Timestamp, r.GlobalId, props));

        var op = Visit(r.RelatingOpeningElement, ctx);
        if (op != -1) ctx.Edges.Add(new GraphEdge(r.StepId, op, "RelatingOpeningElement", -1));
        var bldg = Visit(r.RelatedBuildingElement, ctx);
        if (bldg != -1) ctx.Edges.Add(new GraphEdge(r.StepId, bldg, "RelatedBuildingElement", -1));
    }

    private static void EmitSecondary(string entityType, BaseClassIfc e, MapContext ctx)
    {
        ctx.Nodes.Add(new GraphNode(
            GraphNodeKind.Secondary,
            entityType,
            e.StepId,
            Timestamp,
            GlobalId: null,
            Properties: new Dictionary<string, object?>()));
    }

    /// <summary>Emit edges for <c>IfcProduct</c>'s two forward references.</summary>
    private static void EmitProductReferences(IfcProduct p, MapContext ctx)
    {
        var placement = Visit(p.ObjectPlacement, ctx);
        if (placement != -1) ctx.Edges.Add(new GraphEdge(p.StepId, placement, "ObjectPlacement", -1));
        var representation = Visit(p.Representation, ctx);
        if (representation != -1) ctx.Edges.Add(new GraphEdge(p.StepId, representation, "Representation", -1));
    }
}
