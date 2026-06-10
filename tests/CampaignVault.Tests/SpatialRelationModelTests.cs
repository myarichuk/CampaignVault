using System;
using System.Collections.Generic;
using System.Text.Json;
using CampaignVault.Models;
using Xunit;

namespace CampaignVault.Tests;

public class SpatialRelationModelTests
{
    [Fact]
    public void Character_CanHoldAndSerializeSpatialRelations()
    {
        var character = new Character
        {
            Id = "characters/bram",
            Name = "Bram",
            SystemStats = new SystemExtension()
        };

        character.SystemStats.SpatialRelations = new List<SpatialRelation>
        {
            new() { TargetId = "characters/elara", RelationType = "Grappling" }
        };

        var json = JsonSerializer.Serialize(character);
        var deserialized = JsonSerializer.Deserialize<Character>(json);

        Assert.NotNull(deserialized);
        Assert.Single(deserialized.SystemStats.SpatialRelations);
        Assert.Equal("characters/elara", deserialized.SystemStats.SpatialRelations[0].TargetId);
        Assert.Equal("Grappling", deserialized.SystemStats.SpatialRelations[0].RelationType);
    }
}
