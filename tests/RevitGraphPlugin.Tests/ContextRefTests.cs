using RevitGraphPlugin.Cypher;
using Xunit;

namespace RevitGraphPlugin.Tests;

/// <summary>
/// Unit tests (no Neo4j) for the portable context reference: its ConMan2-compatible
/// serialization and the parse that reads it back. The string form is the reference's
/// identity — it is what a stored rule will carry instead of a p21.
/// </summary>
public class ContextRefTests
{
    [Fact]
    public void Anchor_only_ref_serializes_as_a_single_segment()
    {
        var connection = ContextRef.Anchor(ContextAnchorKind.Connection, "2O2Fr$t4X7Zf8NOew3FLOH");
        Assert.Equal("[connection_node=2O2Fr$t4X7Zf8NOew3FLOH]", connection.Path);

        var primary = ContextRef.Anchor(ContextAnchorKind.Primary, "1a2b3c");
        Assert.Equal("[primary_node=1a2b3c]", primary.Path);
    }

    // ConMan2's DataHandler.path_to_string sorts the keys inside each segment, so
    // EntityType / list_index / rel_type is the required order ('E' < 'l' < 'r' in ASCII).
    [Fact]
    public void Path_segments_use_ConMan2_key_order()
    {
        var contextRef = new ContextRef(
            ContextAnchorKind.Primary, "1a2b3c",
            new[] { new ContextStep("OwnerHistory", 0, "IfcOwnerHistory") });

        Assert.Equal(
            "[primary_node=1a2b3c]|[EntityType=IfcOwnerHistory,list_index=0,rel_type=OwnerHistory]",
            contextRef.Path);
    }

    [Fact]
    public void Round_trips_through_parse()
    {
        var original = new ContextRef(
            ContextAnchorKind.Connection, "2O2Fr$t4X7Zf8NOew3FLOH",
            new[]
            {
                new ContextStep("RepresentationContexts", 0, "IfcGeometricRepresentationContext"),
                new ContextStep("HasSubContexts", 2, "IfcGeometricRepresentationSubContext"),
            });

        Assert.True(ContextRef.TryParse(original.Path, out var parsed));
        Assert.Equal(original, parsed);
        Assert.Equal(original.Path, parsed!.Path);
        Assert.Equal(2, parsed.Steps.Count);
        Assert.Equal(2, parsed.Steps[1].ListIndex);
    }

    // Equality is over the serialized path: the record default compares Steps by
    // reference, which would make two separately built but identical refs differ.
    [Fact]
    public void Equality_is_by_path_not_by_list_reference()
    {
        var a = new ContextRef(ContextAnchorKind.Primary, "g",
            new[] { new ContextStep("OwnerHistory", 0, "IfcOwnerHistory") });
        var b = new ContextRef(ContextAnchorKind.Primary, "g",
            new List<ContextStep> { new("OwnerHistory", 0, "IfcOwnerHistory") });

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("primary_node=g")]                      // no brackets
    [InlineData("[secondary_node=g]")]                  // not an anchor kind
    [InlineData("[primary_node=g]|[rel_type=X]")]       // step missing EntityType/list_index
    [InlineData("[primary_node=g]|[EntityType=E,list_index=x,rel_type=R]")]  // index not a number
    public void Rejects_malformed_paths(string? path)
    {
        Assert.False(ContextRef.TryParse(path, out var parsed));
        Assert.Null(parsed);
    }
}
