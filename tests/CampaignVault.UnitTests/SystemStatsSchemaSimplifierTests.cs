using System.Linq;
using System.Text.Json;
using CampaignVault.Middleware;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>
/// SystemStatsSchemaSimplifier collapses the polymorphic SystemExtension anyOf expansion (dnd5e/pf2e
/// branches carrying a "$system" discriminator) into a loose object stub, wherever it appears in a
/// tool's schema. This only touches the advertised schema — actual request binding still goes through
/// the real [JsonDerivedType] dispatch, unaffected by what's advertised here.
/// </summary>
public class SystemStatsSchemaSimplifierTests
{
    private const string SystemExtensionAnyOf =
        """{"description":"Ruleset-specific stats.","type":["object","null"],"anyOf":[{"properties":{"$system":{"const":"dnd5e"},"armorClass":{"type":"integer"}}},{"properties":{"$system":{"const":"pf2e"},"armorClass":{"type":"integer"}}}]}""";

    [Fact]
    public void CollapsesSystemExtensionExpansion_ToLooseObjectStub()
    {
        var schema = JsonDocument.Parse(
            "{\"type\":\"object\",\"properties\":{\"characterId\":{\"type\":\"string\"},\"systemStats\":" + SystemExtensionAnyOf + "}}"
        ).RootElement;

        var result = SystemStatsSchemaSimplifier.Simplify(schema);

        var systemStats = result.GetProperty("properties").GetProperty("systemStats");
        Assert.False(systemStats.TryGetProperty("anyOf", out _));
        var typeArray = systemStats.GetProperty("type").EnumerateArray().Select(t => t.GetString() ?? "").ToArray();
        Assert.Equal(["object", "null"], typeArray);
        Assert.True(result.GetRawText().Length < schema.GetRawText().Length);
    }

    [Fact]
    public void PreservesNonNullableType_WhenOriginalWasNotNullable()
    {
        var nonNullable = SystemExtensionAnyOf.Replace("""["object","null"]""", "\"object\"");
        var schema = JsonDocument.Parse(
            "{\"type\":\"object\",\"properties\":{\"systemStats\":" + nonNullable + "}}"
        ).RootElement;

        var result = SystemStatsSchemaSimplifier.Simplify(schema);

        var systemStats = result.GetProperty("properties").GetProperty("systemStats");
        Assert.Equal(JsonValueKind.String, systemStats.GetProperty("type").ValueKind);
        Assert.Equal("object", systemStats.GetProperty("type").GetString());
    }

    [Fact]
    public void FindsAndCollapsesNestedOccurrences_InsideArrayItems()
    {
        var schema = JsonDocument.Parse(
            "{\"type\":\"object\",\"properties\":{\"characters\":{\"type\":\"array\",\"items\":{\"type\":\"object\",\"properties\":{\"systemStats\":" + SystemExtensionAnyOf + "}}}}}"
        ).RootElement;

        var result = SystemStatsSchemaSimplifier.Simplify(schema);

        var nested = result.GetProperty("properties").GetProperty("characters")
            .GetProperty("items").GetProperty("properties").GetProperty("systemStats");
        Assert.False(nested.TryGetProperty("anyOf", out _));
    }

    [Fact]
    public void LeavesUnrelatedSchemasUnchanged()
    {
        var schema = JsonDocument.Parse(
            """{"type":"object","properties":{"id":{"type":"string"},"tagsToAdd":{"type":"array","items":{"type":"string"}}}}"""
        ).RootElement;

        var result = SystemStatsSchemaSimplifier.Simplify(schema);

        Assert.Equal(schema.GetRawText(), result.GetRawText());
    }

    [Fact]
    public void DoesNotCollapseRoot_EvenIfRootItselfLooksLikeExpansion()
    {
        // The root of a tool schema must always stay a plain object node, never itself get replaced.
        var schema = JsonDocument.Parse(SystemExtensionAnyOf).RootElement;

        var result = SystemStatsSchemaSimplifier.Simplify(schema);

        Assert.True(result.TryGetProperty("anyOf", out _));
    }
}
