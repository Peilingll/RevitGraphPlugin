using System.Collections;
using System.Reflection;
using GeometryGym.Ifc;

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
    /// Properties exposed by ggifc's base classes that are NOT IFC schema attributes.
    /// </summary>
    private static readonly HashSet<string> GgIfcInternals = new(StringComparer.Ordinal)
    {
        "Database", "Index", "StepId", "StepClassName", "Json", "Guid",
    };

    /// <summary>
    /// IFC4 INVERSE relationships. These are derived views back from IfcRel* entities;
    /// the forward edge is emitted by the relationship entity itself, so skip here to
    /// avoid double-counting.
    /// </summary>
    private static readonly HashSet<string> InverseRelationships = new(StringComparer.Ordinal)
    {
        // IfcObjectDefinition
        "HasAssignments", "Nests", "IsNestedBy", "HasContext",
        "IsDecomposedBy", "Decomposes", "HasAssociations",
        // IfcObject
        "IsDeclaredBy", "Declares", "IsDefinedBy", "IsTypedBy",
        // IfcProduct
        "ReferencedBy", "PositionedRelativeTo",
        // IfcSpatialElement
        "ContainsElements", "ServicedBySystems", "ReferencesElements",
        // IfcElement
        "FillsVoids", "ConnectedTo", "IsInterferedByElements", "InterferesElements",
        "HasProjections", "HasOpenings", "IsConnectionRealization",
        "ProvidesBoundaries", "ConnectedFrom", "ContainedInStructure", "HasCoverings",
        // IfcRepresentationItem
        "StyledByItem", "LayerAssignment",
        // IfcRepresentationContext
        "RepresentationsInContext", "HasSubContexts", "HasCoordinateOperation",
        // IfcPropertyDefinition / IfcPropertySet
        "PartOfPset", "PropertyForDependance", "PropertyDependsOn", "PartOfComplex",
        "Definitions",
        // IfcTypeObject
        "Types",
        // IfcActor
        "EngagedIn",
        // IfcMaterial-related
        "AssociatedTo", "HasRepresentation", "IsRelatedWith", "RelatesTo",
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
            if (InverseRelationships.Contains(name)) continue;
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
