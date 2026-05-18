using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace RevitGraphPlugin.Ifc;

/// <summary>
/// Forward-attribute whitelist for IFC4 entity declarations, loaded once from
/// the embedded <c>RevitGraphPlugin.Schema.ifc4_attributes.json</c> resource.
/// </summary>
public static class Ifc4Schema
{
    private const string ResourceName = "RevitGraphPlugin.Schema.ifc4_attributes.json";

    private static readonly Lazy<IReadOnlyDictionary<string, HashSet<string>>> _schema =
        new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Returns true if <paramref name="attributeName"/> is a forward attribute
    /// declared on <paramref name="entityType"/> in the IFC4 schema. Unknown
    /// entity types fall back to <c>true</c> so that ggifc-only classes
    /// (which have no IFC4 declaration) are not silently filtered out.
    /// </summary>
    public static bool IsSchemaAttribute(string entityType, string attributeName)
    {
        if (_schema.Value.TryGetValue(entityType, out var attrs))
            return attrs.Contains(attributeName);

        Debug.WriteLine(
            $"[Ifc4Schema] unknown entity type '{entityType}' — allowing '{attributeName}' as fallback");
        return true;
    }

    private static IReadOnlyDictionary<string, HashSet<string>> Load()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{ResourceName}' not found on {assembly.FullName}.");

        var raw = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(stream)
            ?? throw new InvalidOperationException(
                $"Failed to deserialize embedded resource '{ResourceName}'.");

        var result = new Dictionary<string, HashSet<string>>(raw.Count, StringComparer.Ordinal);
        foreach (var (entity, attrs) in raw)
            result[entity] = new HashSet<string>(attrs, StringComparer.Ordinal);
        return result;
    }
}
