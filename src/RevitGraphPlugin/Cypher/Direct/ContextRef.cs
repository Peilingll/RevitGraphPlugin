using System.Text;

// ── Pipeline: DIRECT-WRITE, live incremental sync ──
// Rule persistence step 2 (doc_process/2026-08-02-plan-rule-persistence.md §4):
// a PORTABLE name for a node a rule references but does not own.
//
// Inside a rule, p21 is a local name — the rule's own numbering of its own graphlet,
// like the node names of a DPO rule's L/R. The moment a reference LEAVES the graphlet it
// needs a name that survives: ggifc StepIds drift between versions (and across Revit
// restarts), and revit_element_id only means anything in one Revit document. So context
// is named the way ConMan2 names it (GraphPatch.create_unique_path_mappings): an IfcRoot
// GlobalId anchor, plus — when the target is not itself an IfcRoot — the walk from that
// anchor down to it, each step keyed by rel_type / list_index / EntityType.
namespace RevitGraphPlugin.Cypher;

/// <summary>Which ConMan2 node class the path is anchored on (both carry a GlobalId).</summary>
public enum ContextAnchorKind
{
    Primary,
    Connection,
}

/// <summary>
/// One hop of a unique path: follow the <paramref name="RelType"/> edge at
/// <paramref name="ListIndex"/> to the child of type <paramref name="EntityType"/>.
/// The triple is what makes the hop unique — ConMan2's step definition.
/// </summary>
public sealed record ContextStep(string RelType, int ListIndex, string EntityType)
{
    // Keys sorted alphabetically inside the segment, matching DataHandler.path_to_string
    // ('E' < 'l' < 'r' in ASCII).
    public override string ToString()
        => $"[EntityType={EntityType},list_index={ListIndex},rel_type={RelType}]";
}

/// <summary>
/// A portable reference to one context node: an anchor GlobalId plus the (possibly empty)
/// path from it. <see cref="Steps"/> is empty exactly when the target IS the anchor — the
/// common case here, since most context a rule touches is itself an IfcRoot (the storey's
/// containment rel, a host wall, the void/fill rels).
/// </summary>
public sealed record ContextRef(
    ContextAnchorKind AnchorKind,
    string AnchorGlobalId,
    IReadOnlyList<ContextStep> Steps)
{
    /// <summary>Direct anchor: the referenced node is an IfcRoot, so no walk is needed.</summary>
    public static ContextRef Anchor(ContextAnchorKind kind, string globalId)
        => new(kind, globalId, Array.Empty<ContextStep>());

    /// <summary>
    /// ConMan2-compatible serialization (<c>DataHandler.path_to_string</c>), e.g.
    /// <c>[primary_node=1a2b…]|[EntityType=IfcOwnerHistory,list_index=0,rel_type=OwnerHistory]</c>.
    /// This string is the reference's identity — see <see cref="Equals(ContextRef)"/>.
    /// </summary>
    public string Path
    {
        get
        {
            var sb = new StringBuilder();
            sb.Append(AnchorKind == ContextAnchorKind.Primary ? "[primary_node=" : "[connection_node=");
            sb.Append(AnchorGlobalId).Append(']');
            foreach (var step in Steps)
                sb.Append('|').Append(step);
            return sb.ToString();
        }
    }

    public override string ToString() => Path;

    // Value equality over the serialized path: the record default would compare Steps by
    // reference, so two identical refs built separately would not match.
    public bool Equals(ContextRef? other) => other is not null && Path == other.Path;
    public override int GetHashCode() => Path.GetHashCode(StringComparison.Ordinal);

    /// <summary>
    /// Parse a <see cref="Path"/> back into a reference — the read side of persistence, and
    /// the entry point for resolving a stored rule against a host graph.
    /// </summary>
    public static bool TryParse(string? path, out ContextRef? contextRef)
    {
        contextRef = null;
        if (string.IsNullOrEmpty(path)) return false;

        var segments = path.Split('|');
        if (!TryParseSegment(segments[0], out var anchorFields)) return false;

        ContextAnchorKind kind;
        if (anchorFields.TryGetValue("primary_node", out var gid)) kind = ContextAnchorKind.Primary;
        else if (anchorFields.TryGetValue("connection_node", out gid)) kind = ContextAnchorKind.Connection;
        else return false;

        var steps = new List<ContextStep>();
        for (var i = 1; i < segments.Length; i++)
        {
            if (!TryParseSegment(segments[i], out var f)) return false;
            if (!f.TryGetValue("rel_type", out var relType)) return false;
            if (!f.TryGetValue("EntityType", out var entityType)) return false;
            if (!f.TryGetValue("list_index", out var rawIndex) || !int.TryParse(rawIndex, out var listIndex))
                return false;
            steps.Add(new ContextStep(relType, listIndex, entityType));
        }

        contextRef = new ContextRef(kind, gid, steps);
        return true;
    }

    private static bool TryParseSegment(string segment, out Dictionary<string, string> fields)
    {
        fields = new Dictionary<string, string>(StringComparer.Ordinal);
        if (segment.Length < 2 || segment[0] != '[' || segment[^1] != ']') return false;

        foreach (var pair in segment[1..^1].Split(','))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0) return false;
            fields[pair[..eq]] = pair[(eq + 1)..];
        }
        return fields.Count > 0;
    }
}
