using System.Collections.Generic;
using System.Text.Json;
using CampaignVault.Models;
using Xunit;

namespace CampaignVault.Tests;

public class EngagementRelationModelTests
{
    [Fact]
    public void Character_CanHoldAndSerializeEngagementRelations()
    {
        var character = new Character
        {
            Id = "characters/bram",
            Name = "Bram",
            SystemStats = new SystemExtension()
        };

        character.SystemStats.EngagementRelations =
        [
            new EngagementRelation
            {
                TargetId = "characters/elara",
                Category = EngagementCategory.Physical,
                Verb = "grappling"
            }
        ];

        var json = JsonSerializer.Serialize(character);
        var deserialized = JsonSerializer.Deserialize<Character>(json);

        Assert.NotNull(deserialized);
        Assert.Single(deserialized!.SystemStats.EngagementRelations);
        Assert.Equal("characters/elara", deserialized.SystemStats.EngagementRelations[0].TargetId);
        Assert.Equal("grappling", deserialized.SystemStats.EngagementRelations[0].Verb);
        Assert.Equal(EngagementCategory.Physical, deserialized.SystemStats.EngagementRelations[0].Category);
        Assert.Contains("verb", json);
        Assert.Contains("category", json);
        Assert.DoesNotContain("relationType", json);
    }

    [Fact]
    public void Character_DeserializesLegacySpatialRelationsKey()
    {
        const string json = """
            {
              "Id": "characters/bram",
              "Name": "Bram",
              "SystemStats": {
                "spatialRelations": [
                  { "targetId": "characters/elara", "relationType": "Grappling" }
                ]
              }
            }
            """;

        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var deserialized = JsonSerializer.Deserialize<Character>(json, options);

        Assert.NotNull(deserialized);
        Assert.Single(deserialized!.SystemStats.EngagementRelations);
        Assert.Equal("Grappling", deserialized.SystemStats.EngagementRelations[0].Verb);
        Assert.Equal(EngagementCategory.Physical, deserialized.SystemStats.EngagementRelations[0].Category);
    }
}
