using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

// ── Pipeline: DIRECT-WRITE (IFC4 attribute whitelist for EntityWalker) ──
namespace RevitGraphPlugin.Ifc;

/// <summary>
/// Forward-attribute whitelist for IFC4 entity declarations, loaded once from
/// the embedded <c>RevitGraphPlugin.Schema.ifc4_attributes.json</c> resource.
/// </summary>
public static class Ifc4Schema
{
    private const string ResourceName = "RevitGraphPlugin.Schema.ifc4_attributes.json";

    /// <summary>Per entity type: forward attributes in EXPRESS order (<see cref="Ordered"/>) and as a lookup set (<see cref="Sets"/>).</summary>
    private sealed record SchemaData(
        IReadOnlyDictionary<string, IReadOnlyList<string>> Ordered,
        IReadOnlyDictionary<string, HashSet<string>> Sets);

    private static readonly Lazy<SchemaData> _schema =
        new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>True if <paramref name="attributeName"/> is a forward attribute of <paramref name="entityType"/>. Unknown types return true (ggifc-only classes are not filtered).</summary>
    public static bool IsSchemaAttribute(string entityType, string attributeName)
    {
        if (_schema.Value.Sets.TryGetValue(entityType, out var attrs))
            return attrs.Contains(attributeName);

        Debug.WriteLine(
            $"[Ifc4Schema] unknown entity type '{entityType}' — allowing '{attributeName}' as fallback");
        return true;
    }

    /// <summary>Forward attributes of <paramref name="entityType"/> in EXPRESS order (= STEP-line parameter order). Empty for unknown types.</summary>
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
