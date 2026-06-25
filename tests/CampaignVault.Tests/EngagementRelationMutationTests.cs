using System.Text.Json;
using CampaignVault.Models;
using Xunit;

namespace CampaignVault.Tests;

public class EngagementRelationMutationTests
{
    [Fact]
    public void EngagementRelationChange_SerializesAndDeserializes()
    {
        var change = new EngagementRelationChange
        {
            CharacterId = "characters/bard",
            TargetId = "characters/archivist",
            Category = EngagementCategory.Social,
            Verb = "leaning in toward",
            Bidirectional = true
        };

        var json = JsonSerializer.Serialize<WorldChange>(change);
        var deserialized = JsonSerializer.Deserialize<WorldChange>(json);

        Assert.NotNull(deserialized);
        var relChange = Assert.IsType<EngagementRelationChange>(deserialized);
        Assert.Equal("characters/bard", relChange.CharacterId);
        Assert.Equal("characters/archivist", relChange.TargetId);
        Assert.Equal(EngagementCategory.Social, relChange.Category);
        Assert.Equal("leaning in toward", relChange.Verb);
        Assert.True(relChange.Bidirectional);
        Assert.Contains("engagement_relation", json);
    }

    [Fact]
    public void LegacySpatialRelationChange_DeserializesAsEngagementRelationChange()
    {
        const string json = """
            {
              "$type": "spatial_relation",
              "actorId": "characters/bard",
              "targetId": "characters/archivist",
              "verb": "LeaningIn",
              "bidirectional": true
            }
            """;

        var deserialized = JsonSerializer.Deserialize<WorldChange>(json);

        Assert.NotNull(deserialized);
        var relChange = Assert.IsAssignableFrom<EngagementRelationChange>(deserialized);
        Assert.Equal("LeaningIn", relChange.Verb);
    }
}
