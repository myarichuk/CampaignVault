using System;
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
        var visible = CommitSchemaModel.Variants.Where(v => !v.IsEngineOnly && v.ModeId is null).ToList();

        // Every model-authorable variant gets a $type const in $defs; engine-only / mode verbs don't.
        var typeConstCount = System.Text.RegularExpressions.Regex.Matches(json, "\"const\":").Count;
        Assert.Equal(visible.Count, typeConstCount);
        foreach (var hidden in CommitSchemaModel.Variants.Where(v => v.IsEngineOnly || v.ModeId is not null))
        {
            Assert.DoesNotContain($"\"const\":\"{hidden.Discriminator}\"", json);
        }
    }

    private static string[] RequiredOf(JsonDocument doc, string def) =>
    [
        .. doc.RootElement.GetProperty("$defs").GetProperty(def).GetProperty("required")
            .EnumerateArray().Select(x => x.GetString()!)
    ];

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
        // Names are advertised (so a client with no skills can pick a valid $type); fields are not.
        // Fixed core verbs, read from the list segment itself: plain substring checks would pass on names that
        // appear elsewhere in the schema, and enumerating the live registry races with tests that register a
        // temporary plugin verb.
        const string marker = "Valid $type values: ";
        var listStart = json.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(listStart >= 0, "the stub changes description should list the valid $type names");
        var listed = json[(listStart + marker.Length)..].Split('.')[0].Split(", ").ToHashSet();
        Assert.Contains("hp", listed);
        Assert.Contains("event", listed);
        Assert.Contains("location_update", listed);
        Assert.Contains("quest_progress", listed);
        Assert.DoesNotContain("\"const\"", json);
        Assert.Contains("lookup kind=commit_schema", json);
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
