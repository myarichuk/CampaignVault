using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CampaignVault.Tests;

public class SpatialRelationChangeHandlerTests
{
    private ChangeContext CreateContext(params Character[] characters)
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
            summary: [],
            dispatcher: new WorldChangeDispatcher(
                new IWorldChangeHandler[] { new SpatialRelationChangeHandler() },
                NullLogger<WorldChangeDispatcher>.Instance),
            campaignName: null
        );
    }

    [Fact]
    public async Task ApplyAsync_EstablishesBidirectionalRelation()
    {
        var actor = new Character { Id = "char_1", SystemStats = new SystemExtension() };
        var target = new Character { Id = "char_2", SystemStats = new SystemExtension() };
        var context = CreateContext(actor, target);

        var change = new SpatialRelationChange
        {
            ActorId = "char_1",
            TargetId = "char_2",
            RelationType = "Grappling",
            Bidirectional = true
        };

        var handler = new SpatialRelationChangeHandler();
        var result = await handler.ApplyAsync(change, context);

        Assert.True(result.Success);
        
        var actorRel = actor.SystemStats.SpatialRelations.FirstOrDefault(r => r.TargetId == "char_2");
        Assert.NotNull(actorRel);
        Assert.Equal("Grappling", actorRel.RelationType);

        var targetRel = target.SystemStats.SpatialRelations.FirstOrDefault(r => r.TargetId == "char_1");
        Assert.NotNull(targetRel);
        Assert.Equal("GrappledBy", targetRel.RelationType);
    }
}
