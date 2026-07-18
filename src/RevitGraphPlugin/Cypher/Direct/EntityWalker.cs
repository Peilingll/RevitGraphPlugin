using System.Collections;
using System.Diagnostics;
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
/// One InlineNode pattern: an inline value (an <see cref="IfcValue"/> with no StepId
/// of its own) that ConMan2 stores as a separate InlineNode connected to its parent.
/// Mirrors ConMan2's inline_patterns (IfcGraphInterface.process_ifc_attributes).
/// <paramref name="OwnerElementId"/> is the parent entity's Revit-element ownership
/// (null for shared/boilerplate parents) — inline nodes live and die with their
/// parent's graphlet, so removal by <c>revit_element_id</c> must reach them too or
/// they leak as orphans. Set by <see cref="CypherEmitter.WalkAll"/>, not by Walk.
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
        var inlines = new List<InlineData>();

        // Node properties: lossless STEP-line source (方案 B). Every primitive attribute
        // value ($/''/*/.ENUM./int/real/list) is taken verbatim from ggifc's Part-21
        // output, which faithfully preserves unset/derived/empty-string distinctions that
        // the property getters collapse. See doc/log/2026-07-12_direct-roundtrip-diagnosis.md.
        EmitPropertiesFromStepLine(entity, entityType, props);

        // Edges + inline nodes: reflection (unchanged; verified isomorphic to the bridge
        // graph — 128 relationships matched). Only entity references, inline IfcValues,
        // and aggregates thereof are consumed here; primitive slots belong to the STEP
        // properties above.
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

    /// <summary>
    /// Fills node properties from the entity's Part-21 (STEP) line, mapping each parameter
    /// positionally onto its EXPRESS-declared attribute name. Only primitive slots become
    /// properties; references / typed inline values are left to the reflection pass.
    /// Throws (fail-loud) if ggifc's parameter count does not match the schema, so a
    /// malformed serialization surfaces the offending entity instead of corrupting silently.
    /// </summary>
    private static void EmitPropertiesFromStepLine(
        BaseClassIfc entity, string entityType, Dictionary<string, object> props)
    {
        var names = Ifc4Schema.GetOrderedAttributes(entityType);
        if (names.Count == 0)
        {
            // Type absent from the IFC4 schema: no ordered attribute list to map onto.
            // Edges/inlines still come from reflection below.
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

    /// <summary>
    /// Extracts outgoing edges and inline-node children for one attribute value via
    /// reflection. Primitive scalars and primitive aggregates are ignored here — those
    /// are node properties, emitted from the STEP line by <see cref="EmitPropertiesFromStepLine"/>.
    /// </summary>
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
            // StepId == 0 inline entities surface as IfcValue below (the common case:
            // a property's NominalValue).
            return;
        }

        // Inline value (IfcValue, e.g. IfcPropertySingleValue.NominalValue). Not a
        // BaseClassIfc and has no StepId of its own → ConMan2 records it as an
        // InlineNode connected by this attribute. Mirror that.
        if (value is IfcValue ifcValue)
        {
            inlines.Add(new InlineData(
                sourceP21, name, 0, ifcValue.GetType().Name, WrappedValue(ifcValue)));
            return;
        }

        // String is IEnumerable<char>; it is a primitive property, not a collection.
        if (value is string) return;

        // Dictionary (e.g. IfcPropertySet.HasProperties, keyed by property name).
        // ggifc stores these as Dictionary<string, IfcProperty>; emit an edge to each
        // value entity. MUST precede the IEnumerable branch (a Dictionary enumerates
        // as KeyValuePair, which would otherwise be missed — the HasProperties edges).
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

        // Collection of entities (edges) or inline values (inline nodes). Primitive
        // aggregates (coordinate lists, direction ratios, …) are skipped — the STEP line
        // carries them as a node property.
        if (value is IEnumerable enumerable)
        {
            int idx = 0;
            foreach (var item in enumerable)
            {
                if (item is BaseClassIfc itemEntity)
                {
                    if (itemEntity.StepId > 0)
                        edges.Add(new EdgeData(sourceP21, name, idx, itemEntity.StepId));
                    // else: inline BaseClassIfc — rare; not seen in current models
                }
                else if (item is IfcValue itemValue)
                {
                    inlines.Add(new InlineData(
                        sourceP21, name, idx, itemValue.GetType().Name, WrappedValue(itemValue)));
                }
                // primitive item → ignored; STEP emits the primitive-list property.
                idx++;
            }
            return;
        }

        // Enum / DateTime / numeric / measure structs → primitive property, from STEP.
    }

    /// <summary>
    /// Extract the primitive wrapped value of an <see cref="IfcValue"/> for an
    /// InlineNode's <c>wrappedValue</c>, matching ConMan2's encoding: bool/int/double
    /// stay typed; logical/other enums become their uppercase name; null → "$".
    /// </summary>
    private static object WrappedValue(IfcValue v)
    {
        var raw = v.Value;
        if (raw is Enum e) return e.ToString().ToUpperInvariant();
        if (raw is null) return v.ValueString is { Length: > 0 } vs ? vs : "$";
        return raw;
    }
}
