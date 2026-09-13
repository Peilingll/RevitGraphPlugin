using System.Collections;
using System.Diagnostics;
using System.Reflection;
using GeometryGym.Ifc;
using RevitGraphPlugin.Ifc;

// ── Pipeline: DIRECT-WRITE (Revit → ggifc tree → Cypher → Neo4j; no temp IFC) ──
namespace RevitGraphPlugin.Cypher;

/// <summary>
/// One edge derived from an IFC entity attribute.
/// </summary>
public sealed record EdgeData(int SourceP21, string RelType, int ListIndex, int TargetP21);

/// <summary>
/// An inline value (<see cref="IfcValue"/>, no StepId) stored as an InlineNode linked to
/// its parent (ConMan2's inline_patterns). <paramref name="OwnerElementId"/> is the
/// parent's ownership, set by <see cref="CypherEmitter.WalkOwned"/>.
/// </summary>
public sealed record InlineData(
    int SourceP21, string RelType, int ListIndex, string EntityType, object WrappedValue,
    long? OwnerElementId = null);

/// <summary>
/// Per-entity record produced by <see cref="EntityWalker.Walk"/>.
/// Maps an IFC entity to a Neo4j node (properties) + outgoing edges + inline children
/// following ConMan2's schema rules.
/// </summary>
public sealed record EntityData(
    int P21,
    string EntityType,
    string? GlobalId,
    NodeKind Kind,
    Dictionary<string, object> Properties,
    List<EdgeData> Edges,
    List<InlineData> Inlines
);

public static class EntityWalker
{
    /// <summary>ggifc framework properties that are not IFC attributes.</summary>
    private static readonly HashSet<string> GgIfcInternals = new(StringComparer.Ordinal)
    {
        "Database", "Index", "StepId", "StepClassName", "Json", "Guid",
    };

    /// <summary>Walk one IFC entity into its node properties, outgoing edges and inline children.</summary>
    public static EntityData Walk(BaseClassIfc entity, string timestamp)
    {
        var type = entity.GetType();
        var entityType = type.Name;
        var p21 = entity.StepId;
        var kind = NodeClassifier.Classify(entity);
        var globalId = entity is IfcRoot root ? root.GlobalId : null;

        var props = new Dictionary<string, object>
        {
            ["EntityType"] = entityType,
            ["p21_id"]     = $"#{p21}",
            ["timestamp"]  = timestamp,
        };
        if (!string.IsNullOrEmpty(globalId))
            props["GlobalId"] = globalId;

        var edges = new List<EdgeData>();
        var inlines = new List<InlineData>();

        // Node properties come from the STEP line (lossless: keeps $ / * / '' distinct).
        EmitPropertiesFromStepLine(entity, entityType, props);

        // Edges and inline nodes come from reflection; primitive slots are skipped here.
        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var name = prop.Name;

            if (GgIfcInternals.Contains(name)) continue;
            if (!Ifc4Schema.IsSchemaAttribute(entityType, name)) continue;
            if (name == "GlobalId" || name == "EntityType") continue;

            // Skip indexers / write-only / has parameters
            if (prop.GetIndexParameters().Length > 0) continue;
            if (!prop.CanRead) continue;

            object? value;
            try { value = prop.GetValue(entity); }
            catch { continue; }

            EmitEdgesAndInlines(p21, name, value, edges, inlines);
        }

        return new EntityData(p21, entityType, globalId, kind, props, edges, inlines);
    }

    /// <summary>Node properties from the STEP line, mapped positionally onto the EXPRESS attribute names. Throws if the parameter count does not match the schema.</summary>
    private static void EmitPropertiesFromStepLine(
        BaseClassIfc entity, string entityType, Dictionary<string, object> props)
    {
        var names = Ifc4Schema.GetOrderedAttributes(entityType);
        if (names.Count == 0)
        {
            // Type absent from the IFC4 schema: no properties; edges still come from reflection.
            Debug.WriteLine(
                $"[EntityWalker] '{entityType}' not in IFC4 schema — no STEP-based properties.");
            return;
        }

        var stepLine = entity.ToString();
        var tokens = StepLineParser.ParseArguments(stepLine);
        if (tokens.Count != names.Count)
            throw new InvalidOperationException(
                $"STEP argument count {tokens.Count} != schema attribute count {names.Count} " +
                $"for {entityType} (#{entity.StepId}). Line: {stepLine}");

        for (var i = 0; i < names.Count; i++)
        {
            var name = names[i];
            if (name == "GlobalId") continue; // already set from IfcRoot.GlobalId
            if (StepLineParser.TryToPropertyValue(tokens[i], out var value))
                props[name] = value;
        }
    }

    /// <summary>Outgoing edges and inline children for one attribute value; primitives are ignored (they are STEP-line properties).</summary>
    private static void EmitEdgesAndInlines(
        int sourceP21,
        string name,
        object? value,
        List<EdgeData> edges,
        List<InlineData> inlines)
    {
        if (value is null) return;

        // Entity reference (StepId > 0)
        if (value is BaseClassIfc refEntity)
        {
            if (refEntity.StepId > 0)
                edges.Add(new EdgeData(sourceP21, name, 0, refEntity.StepId));
            return;
        }

        // Inline value (e.g. IfcPropertySingleValue.NominalValue) → InlineNode.
        if (value is IfcValue ifcValue)
        {
            inlines.Add(new InlineData(
                sourceP21, name, 0, ifcValue.GetType().Name, WrappedValue(ifcValue)));
            return;
        }

        // String is IEnumerable<char>; it is a primitive property, not a collection.
        if (value is string) return;

        // Dictionary (e.g. IfcPropertySet.HasProperties): edge per value. Must precede
        // the IEnumerable branch.
        if (value is IDictionary dictionary)
        {
            var di = 0;
            foreach (var v in dictionary.Values)
            {
                if (v is BaseClassIfc de && de.StepId > 0)
                    edges.Add(new EdgeData(sourceP21, name, di, de.StepId));
                else if (v is IfcValue dv)
                    inlines.Add(new InlineData(sourceP21, name, di, dv.GetType().Name, WrappedValue(dv)));
                di++;
            }
            return;
        }

        // Collection of entities (edges) or inline values (inline nodes).
        if (value is IEnumerable enumerable)
        {
            int idx = 0;
            foreach (var item in enumerable)
            {
                if (item is BaseClassIfc itemEntity)
                {
                    if (itemEntity.StepId > 0)
                        edges.Add(new EdgeData(sourceP21, name, idx, itemEntity.StepId));
                }
                else if (item is IfcValue itemValue)
                {
                    inlines.Add(new InlineData(
                        sourceP21, name, idx, itemValue.GetType().Name, WrappedValue(itemValue)));
                }
                idx++;
            }
            return;
        }

    }

    /// <summary>An <see cref="IfcValue"/>'s wrapped value in ConMan2's encoding: bool/int/double typed, enums uppercase, null → "$".</summary>
    private static object WrappedValue(IfcValue v)
    {
        var raw = v.Value;
        if (raw is Enum e) return e.ToString().ToUpperInvariant();
        if (raw is null) return v.ValueString is { Length: > 0 } vs ? vs : "$";
        return raw;
    }
}
