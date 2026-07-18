using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

// ── Pipeline: DIRECT-WRITE only (IFC4 attribute whitelist for Cypher/Direct/EntityWalker). ──
// The temp-IFC bridge does not use this.
namespace RevitGraphPlugin.Ifc;

/// <summary>
/// Forward-attribute whitelist for IFC4 entity declarations, loaded once from
/// the embedded <c>RevitGraphPlugin.Schema.ifc4_attributes.json</c> resource.
/// </summary>
public static class Ifc4Schema
{
    private const string ResourceName = "RevitGraphPlugin.Schema.ifc4_attributes.json";

    /// <summary>
    /// Loaded schema: per entity type, the forward attributes in EXPRESS declaration
    /// order (<see cref="Ordered"/>, for positional STEP-line mapping) plus the same
    /// names as an O(1) lookup set (<see cref="Sets"/>, for whitelist checks).
    /// </summary>
    private sealed record SchemaData(
        IReadOnlyDictionary<string, IReadOnlyList<string>> Ordered,
        IReadOnlyDictionary<string, HashSet<string>> Sets);

    private static readonly Lazy<SchemaData> _schema =
        new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Returns true if <paramref name="attributeName"/> is a forward attribute
    /// declared on <paramref name="entityType"/> in the IFC4 schema. Unknown
    /// entity types fall back to <c>true</c> so that ggifc-only classes
    /// (which have no IFC4 declaration) are not silently filtered out.
    /// </summary>
    public static bool IsSchemaAttribute(string entityType, string attributeName)
    {
        if (_schema.Value.Sets.TryGetValue(entityType, out var attrs))
            return attrs.Contains(attributeName);

        Debug.WriteLine(
            $"[Ifc4Schema] unknown entity type '{entityType}' — allowing '{attributeName}' as fallback");
        return true;
    }

    /// <summary>
    /// Returns the forward attributes of <paramref name="entityType"/> in EXPRESS
    /// declaration order — i.e. the positional order of the parameters in that
    /// entity's STEP (Part 21) line, so the STEP-line parser can zip parameter N to
    /// its attribute name. Unknown entity types return an empty list (the caller
    /// decides how to handle a type absent from the IFC4 schema).
    /// </summary>
    public static IReadOnlyList<string> GetOrderedAttributes(string entityType)
    {
        return _schema.Value.Ordered.TryGetValue(entityType, out var attrs)
            ? attrs
            : Array.Empty<string>();
    }

    private static SchemaData Load()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{ResourceName}' not found on {assembly.FullName}.");

        var raw = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(stream)
            ?? throw new InvalidOperationException(
                $"Failed to deserialize embedded resource '{ResourceName}'.");

        var ordered = new Dictionary<string, IReadOnlyList<string>>(raw.Count, StringComparer.Ordinal);
        var sets = new Dictionary<string, HashSet<string>>(raw.Count, StringComparer.Ordinal);
        foreach (var (entity, attrs) in raw)
        {
            ordered[entity] = attrs;
            sets[entity] = new HashSet<string>(attrs, StringComparer.Ordinal);
        }
        return new SchemaData(ordered, sets);
    }
}
