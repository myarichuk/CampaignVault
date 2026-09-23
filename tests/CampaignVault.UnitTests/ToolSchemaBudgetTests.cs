using System.Text.Json;
using CampaignVault.Schema;
using Xunit;
using System.Linq;

namespace CampaignVault.UnitTests;

public class ToolSchemaBudgetTests
{
    private static readonly JsonSerializerOptions JsonOptions =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    [Fact]
    public void TakeTurnInputSchema_BuildsSuccessfully()
    {
        var schema = TakeTurnSchemaBuilder.Build(JsonOptions);
        var json = schema.GetRawText();

        Assert.NotEmpty(json);
        Assert.Contains("\"properties\"", json);
        Assert.Contains("\"changes\"", json);
    }

    [Fact]
    public void WorldBuildInputSchema_BuildsSuccessfully()
    {
        var schema = WorldBuildSchemaBuilder.Build(JsonOptions);
        var json = schema.GetRawText();

        Assert.NotEmpty(json);
        Assert.Contains("\"properties\"", json);
        Assert.Contains("$defs", json);
        Assert.DoesNotContain("\"items\":{\"type\":\"object\"}", json.Replace(" ", ""));
    }

    [Fact]
    public void TakeTurnSchema_HasValidReferences()
    {
        var schema = TakeTurnSchemaBuilder.Build(JsonOptions);
        var json = schema.GetRawText();

        // Verify worldChange anyOf exists and has refs
        Assert.Contains("worldChange", json);
        Assert.Contains("anyOf", json);
        Assert.Contains("$ref", json);

        // Verify $defs section exists
        Assert.Contains("$defs", json);

        // Every "#/$defs/<name>" ref must actually resolve to a key in $defs.
        using var doc = JsonDocument.Parse(json);
        var defNames = doc.RootElement.TryGetProperty("$defs", out var defs)
            ? defs.EnumerateObject().Select(p => p.Name).ToHashSet()
            : [];

        var danglingRefs = System.Text.RegularExpressions.Regex
            .Matches(json, "\"\\$ref\"\\s*:\\s*\"#/\\$defs/([^\"]+)\"")
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .Where(name => !defNames.Contains(name))
            .ToList();

        Assert.True(danglingRefs.Count == 0,
            $"Found $ref(s) pointing at nonexistent $defs entries: {string.Join(", ", danglingRefs)}");
    }

    [Fact]
    public void TakeTurnSchema_ExposesForceFullReseed()
    {
        var schema = TakeTurnSchemaBuilder.Build(JsonOptions);
        var json = schema.GetRawText();

        Assert.Contains("\"forceFullReseed\"", json);
    }

    [Fact]
    public void HotTierVariants_Exist()
    {
        var hotTierCount = CommitSchemaModel.Variants.Count(v => v.IsHotTier);

        // Should have exactly 10 hot-tier types per Phase 1.1
        Assert.Equal(10, hotTierCount);
    }

    [Fact]
    public void AllVariants_InSchema()
    {
        var schema = TakeTurnSchemaBuilder.Build(JsonOptions);
        var json = schema.GetRawText();
        var variantCount = CommitSchemaModel.Variants.Count;

        // Rough check: each variant should appear at least in $defs
        // Count "$type" discriminator consts - should be at least as many as variants
        var typeConstCount = System.Text.RegularExpressions.Regex.Matches(json, "\"const\":").Count;
        Assert.True(typeConstCount >= variantCount,
            $"Expected at least {variantCount} $type discriminators, found {typeConstCount}");
    }

    private static string[] RequiredOf(JsonDocument doc, string def) =>
        doc.RootElement.GetProperty("$defs").GetProperty(def).GetProperty("required")
            .EnumerateArray().Select(x => x.GetString()!).ToArray();

    [Fact]
    public void FullSchema_RequiredArrays_OnlyListRealRequirements()
    {
        using var doc = JsonDocument.Parse(TakeTurnSchemaBuilder.Build(JsonOptions, ToolSchemaMode.Full).GetRawText());

        Assert.Equal(["$type", "locationId"], RequiredOf(doc, "location_update"));
        Assert.DoesNotContain("eventId", RequiredOf(doc, "event"));
        Assert.DoesNotContain("locationId", RequiredOf(doc, "event"));
        Assert.DoesNotContain("initiatorId", RequiredOf(doc, "event"));
        Assert.DoesNotContain("isReaction", RequiredOf(doc, "ruleset_action"));
        Assert.DoesNotContain("parameters", RequiredOf(doc, "ruleset_action"));
        Assert.Contains("characterId", RequiredOf(doc, "hp"));
    }

    [Fact]
    public void StubSchema_IsConstantSmallAndHasNoDefs()
    {
        var json = TakeTurnSchemaBuilder.Build(JsonOptions, ToolSchemaMode.Stub).GetRawText();
        using var doc = JsonDocument.Parse(json);

        Assert.False(doc.RootElement.TryGetProperty("$defs", out _));
        Assert.DoesNotContain("\"hp\"", json);
        Assert.Contains("get_commit_schema", json);
        Assert.Contains("\"forceFullReseed\"", json);
        Assert.Contains("\"clientPartyFingerprint\"", json);
        Assert.True(json.Length < 5000, $"Stub schema grew to {json.Length} chars");
    }

    [Theory]
    [InlineData(null, ToolSchemaMode.Stub)]
    [InlineData("", ToolSchemaMode.Stub)]
    [InlineData("garbage", ToolSchemaMode.Stub)]
    [InlineData("full", ToolSchemaMode.Full)]
    [InlineData("Stub", ToolSchemaMode.Stub)]
    public void ParseMode_DefaultsToStub(string? value, ToolSchemaMode expected)
    {
        Assert.Equal(expected, McpSchemaInstaller.ParseMode(value));
    }
}
