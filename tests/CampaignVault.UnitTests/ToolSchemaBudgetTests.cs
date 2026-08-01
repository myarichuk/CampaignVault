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
}
