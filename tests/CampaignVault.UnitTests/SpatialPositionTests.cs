using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CampaignVault.Tests;

public class SpatialPositionTests
{
    [Fact]
    public void Character_CanHoldAndSerializeSpatialPositions()
    {
        var character = new Character
        {
            Id = "characters/drunk",
            Name = "Rowdy Drunk",
            SystemStats = new SystemExtension
            {
                SpatialPositions =
                [
                    new SpatialPosition
                    {
                        TargetId = "characters/pc",
                        DistanceBand = SpatialDistanceBand.Near,
                        Bearing = "North",
                        Zone = "bar"
                    }
                ]
            }
        };

        var json = JsonSerializer.Serialize(character);
        var deserialized = JsonSerializer.Deserialize<Character>(json);

        Assert.NotNull(deserialized);
        Assert.Single(deserialized!.SystemStats.SpatialPositions);
        Assert.Equal(SpatialDistanceBand.Near, deserialized.SystemStats.SpatialPositions[0].DistanceBand);
        Assert.Equal("North", deserialized.SystemStats.SpatialPositions[0].Bearing);
        Assert.Contains("spatialPositions", json);
    }

    [Fact]
    public async Task SpatialPositionChangeHandler_SetsAndRemovesPosition()
    {
        var character = new Character { Id = "char_1", SystemStats = new SystemExtension() };
        var context = ChangeContextTestHelper.Create(
            characters: new Dictionary<string, Character> { { character.Id, character } },
            items: new Dictionary<string, Item>(),
            locations: new Dictionary<string, Location>(),
            factions: new Dictionary<string, Faction>(),
            quests: new Dictionary<string, Quest>(),
            logger: NullLogger.Instance,
            summary: [],
            dispatcher: new WorldChangeDispatcher(
                [new SpatialPositionChangeHandler()],
                new CampaignVault.Data.CampaignDocumentKeys(), NullLogger<WorldChangeDispatcher>.Instance));

        var handler = new SpatialPositionChangeHandler();
        var set = new SpatialPositionChange
        {
            CharacterId = "char_1",
            TargetId = "characters/pc",
            DistanceBand = SpatialDistanceBand.Near,
            Bearing = "North",
            Zone = "bar"
        };

        Assert.True((await handler.ApplyAsync(set, context)).Success);
        Assert.Single(character.SystemStats.SpatialPositions);
        Assert.Equal("bar", character.SystemStats.SpatialPositions[0].Zone);

        var remove = new SpatialPositionChange
        {
            CharacterId = "char_1",
            TargetId = "characters/pc",
            DistanceBand = null
        };

        Assert.True((await handler.ApplyAsync(remove, context)).Success);
        Assert.Empty(character.SystemStats.SpatialPositions);
    }

    [Fact]
    public void SpatialPositionChange_SerializesAndDeserializes()
    {
        var change = new SpatialPositionChange
        {
            CharacterId = "characters/drunk",
            TargetId = "characters/pc",
            DistanceBand = SpatialDistanceBand.Near,
            Bearing = "North"
        };

        var json = JsonSerializer.Serialize<WorldChange>(change);
        var deserialized = JsonSerializer.Deserialize<WorldChange>(json);

        var posChange = Assert.IsType<SpatialPositionChange>(deserialized);
        Assert.Equal("characters/drunk", posChange.CharacterId);
        Assert.Contains("spatial_position", json);
    }
}
