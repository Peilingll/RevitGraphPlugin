using System.Collections;
using System.Reflection;
using GeometryGym.Ifc;
using RevitGraphPlugin.Ifc;

// ── Pipeline: DIRECT-WRITE (Revit → ggifc tree → Cypher → Neo4j; no temp IFC) ──
// Opt-in. Default sink is the temp-IFC bridge (Cypher/IfcSnippetSink.cs).
namespace RevitGraphPlugin.Cypher;

/// <summary>
/// One edge derived from an IFC entity attribute.
/// </summary>
public sealed record EdgeData(int SourceP21, string RelType, int ListIndex, int TargetP21);

/// <summary>
/// Per-entity record produced by <see cref="EntityWalker.Walk"/>.
/// Maps an IFC entity to a Neo4j node (properties) + outgoing edges
/// following ConMan2's schema rules.
/// </summary>
public sealed record EntityData(
    int P21,
    string EntityType,
    string? GlobalId,
    NodeKind Kind,
    Dictionary<string, object> Properties,
    List<EdgeData> Edges
);

public static class EntityWalker
{
    /// <summary>
    /// ggifc framework properties that are not part of any IFC schema. These would
    /// never appear in <see cref="Ifc4Schema"/>, but checking here short-circuits the
    /// whitelist lookup and keeps the filter explicit.
    /// </summary>
    private static readonly HashSet<string> GgIfcInternals = new(StringComparer.Ordinal)
    {
        "Database", "Index", "StepId", "StepClassName", "Json", "Guid",
    };

    /// <summary>
    /// Walk a single IFC entity and produce its Neo4j node properties + outgoing edges.
    /// </summary>
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

            EmitAttribute(p21, name, value, props, edges);
        }

        return new EntityData(p21, entityType, globalId, kind, props, edges);
    }

    private static void EmitAttribute(
        int sourceP21,
        string name,
        object? value,
        Dictionary<string, object> props,
        List<EdgeData> edges)
    {
        // null  →  "$"  (ConMan2 convention)
        if (value is null)
        {
            props[name] = "$";
            return;
        }

        // Entity reference (StepId > 0)
        if (value is BaseClassIfc refEntity)
        {
            if (refEntity.StepId > 0)
                edges.Add(new EdgeData(sourceP21, name, 0, refEntity.StepId));
            // StepId == 0 is an InlineNode — TODO: handle in a later phase
            return;
        }

        // Enum  →  uppercase string
        if (value is Enum enumValue)
        {
            props[name] = enumValue.ToString().ToUpperInvariant();
            return;
        }

        // DateTime  →  Unix epoch seconds (matches ConMan2's IfcTimeStamp encoding,
        // which keeps the raw integer from the .ifc STEP file).
        if (value is DateTime dt)
        {
            var utc = dt.Kind == DateTimeKind.Utc ? dt : dt.ToUniversalTime();
            props[name] = new DateTimeOffset(utc, TimeSpan.Zero).ToUnixTimeSeconds();
            return;
        }

        // Primitive
        if (value is string s)
        {
            props[name] = string.IsNullOrEmpty(s) ? "$" : s;
            return;
        }
        if (value is int or long or double or float or bool or decimal)
        {
            props[name] = value;
            return;
        }

        // Collection
        if (value is IEnumerable enumerable)
        {
            var primitiveItems = new List<object>();
            int idx = 0;
            bool sawEntityItem = false;

            foreach (var item in enumerable)
            {
                if (item is BaseClassIfc itemEntity)
                {
                    if (itemEntity.StepId > 0)
                    {
                        edges.Add(new EdgeData(sourceP21, name, idx, itemEntity.StepId));
                        sawEntityItem = true;
                    }
                    // else: inline — TODO
                }
                else if (item != null)
                {
                    primitiveItems.Add(item);
                }
                idx++;
            }

            if (!sawEntityItem)
            {
                // ConMan2 stringifies primitive lists as "(a,b,c)" (or "$" if empty).
                props[name] = primitiveItems.Count == 0
                    ? "$"
                    : $"({string.Join(",", primitiveItems)})";
            }
            return;
        }

        // Fallback — stringify (covers ggifc measure/value structs)
        var str = value.ToString();
        props[name] = string.IsNullOrEmpty(str) ? "$" : str;
    }
}
