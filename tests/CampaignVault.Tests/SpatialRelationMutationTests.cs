using System.Text.Json;
using CampaignVault.Models;
using Xunit;

namespace CampaignVault.Tests;

public class SpatialRelationMutationTests
{
    [Fact]
    public void SpatialRelationChange_SerializesAndDeserializes()
    {
        var change = new SpatialRelationChange
        {
            ActorId = "characters/bard",
            TargetId = "characters/archivist",
            RelationType = "LeaningIn",
            Bidirectional = true
        };

        var json = JsonSerializer.Serialize<WorldChange>(change);
        var deserialized = JsonSerializer.Deserialize<WorldChange>(json);

        Assert.NotNull(deserialized);
        var relChange = Assert.IsType<SpatialRelationChange>(deserialized);
        Assert.Equal("characters/bard", relChange.ActorId);
        Assert.Equal("characters/archivist", relChange.TargetId);
        Assert.Equal("LeaningIn", relChange.RelationType);
        Assert.True(relChange.Bidirectional);
    }
}
