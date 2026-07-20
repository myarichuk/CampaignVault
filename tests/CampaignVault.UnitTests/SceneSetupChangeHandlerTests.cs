using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CampaignVault.Tests;

public class SceneSetupChangeHandlerTests
{
    private ChangeContext CreateContext(List<string> summary, params Character[] characters)
    {
        var charDict = characters.ToDictionary(c => c.Id);
        return new ChangeContext(
            sessionForTests: null,
            characters: charDict,
            items: new Dictionary<string, Item>(),
            locations: new Dictionary<string, Location>(),
            factions: new Dictionary<string, Faction>(),
            quests: new Dictionary<string, Quest>(),
            logger: NullLogger.Instance,
            summary: summary,
            dispatcher: new WorldChangeDispatcher(
                [new EngagementRelationChangeHandler(), new SpatialPositionChangeHandler(), new SceneSetupChangeHandler()],
                new CampaignDocumentKeys(), NullLogger<WorldChangeDispatcher>.Instance),
            campaignName: null);
    }

    [Fact]
    public async Task ApplyAsync_EngagementOnly_SetsEngagementNotSpatial()
    {
        var actor = new Character { Id = "char_1", SystemStats = new SystemExtension() };
        var target = new Character { Id = "char_2", SystemStats = new SystemExtension() };
        var context = CreateContext([], actor, target);

        var change = new SceneSetupChange
        {
            CharacterId = "char_1",
            TargetId = "char_2",
            Engagement = new SceneSetupEngagement { Category = EngagementCategory.Physical, Verb = "Grappling", Bidirectional = true }
        };

        var handler = new SceneSetupChangeHandler();
        var result = await handler.ApplyAsync(change, context);

        Assert.True(result.Success);
        Assert.Single(actor.SystemStats.EngagementRelations);
        Assert.Equal("Grappling", actor.SystemStats.EngagementRelations[0].Verb);
        Assert.Single(target.SystemStats.EngagementRelations);
        Assert.Empty(actor.SystemStats.SpatialPositions);
    }

    [Fact]
    public async Task ApplyAsync_SpatialOnly_SetsSpatialNotEngagement()
    {
        var actor = new Character { Id = "char_1", SystemStats = new SystemExtension() };
        var target = new Character { Id = "char_2", SystemStats = new SystemExtension() };
        var context = CreateContext([], actor, target);

        var change = new SceneSetupChange
        {
            CharacterId = "char_1",
            TargetId = "char_2",
            Spatial = new SceneSetupSpatial { DistanceBand = "Touch", Zone = "bar" }
        };

        var handler = new SceneSetupChangeHandler();
        var result = await handler.ApplyAsync(change, context);

        Assert.True(result.Success);
        Assert.Empty(actor.SystemStats.EngagementRelations);
        Assert.Single(actor.SystemStats.SpatialPositions);
        Assert.Equal("Touch", actor.SystemStats.SpatialPositions[0].DistanceBand);
    }

    [Fact]
    public async Task ApplyAsync_Both_SetsEngagementAndSpatial()
    {
        var actor = new Character { Id = "char_1", SystemStats = new SystemExtension() };
        var target = new Character { Id = "char_2", SystemStats = new SystemExtension() };
        var summary = new List<string>();
        var context = CreateContext(summary, actor, target);

        var change = new SceneSetupChange
        {
            CharacterId = "char_1",
            TargetId = "char_2",
            Engagement = new SceneSetupEngagement { Category = EngagementCategory.Physical, Verb = "Grappling", Bidirectional = true },
            Spatial = new SceneSetupSpatial { DistanceBand = "Touch" }
        };

        var handler = new SceneSetupChangeHandler();
        var result = await handler.ApplyAsync(change, context);

        Assert.True(result.Success);
        Assert.Single(actor.SystemStats.EngagementRelations);
        Assert.Single(actor.SystemStats.SpatialPositions);
        Assert.Single(target.SystemStats.EngagementRelations); // bidirectional mirroring still applies

        var establishedMessages = summary.Count(m => m.Contains("EngagementRelation established", System.StringComparison.Ordinal));
        Assert.Equal(1, establishedMessages);
    }

    [Fact]
    public async Task ApplyAsync_Neither_Fails()
    {
        var actor = new Character { Id = "char_1", SystemStats = new SystemExtension() };
        var target = new Character { Id = "char_2", SystemStats = new SystemExtension() };
        var context = CreateContext([], actor, target);

        var change = new SceneSetupChange { CharacterId = "char_1", TargetId = "char_2" };

        var handler = new SceneSetupChangeHandler();
        var result = await handler.ApplyAsync(change, context);

        Assert.False(result.Success);
    }
}
