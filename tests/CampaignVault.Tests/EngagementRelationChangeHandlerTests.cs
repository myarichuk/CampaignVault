using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CampaignVault.Tests;

public class EngagementRelationChangeHandlerTests
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
                [new EngagementRelationChangeHandler()],
                new CampaignVault.Data.CampaignDocumentKeys(), NullLogger<WorldChangeDispatcher>.Instance),
            campaignName: null);
    }

    [Fact]
    public async Task ApplyAsync_EstablishesBidirectionalRelation()
    {
        var actor = new Character { Id = "char_1", SystemStats = new SystemExtension() };
        var target = new Character { Id = "char_2", SystemStats = new SystemExtension() };
        var context = CreateContext(actor, target);

        var change = new EngagementRelationChange
        {
            ActorId = "char_1",
            TargetId = "char_2",
            Category = EngagementCategory.Physical,
            Verb = "Grappling",
            Bidirectional = true
        };

        var handler = new EngagementRelationChangeHandler();
        var result = await handler.ApplyAsync(change, context);

        Assert.True(result.Success);

        var actorRel = actor.SystemStats.EngagementRelations.FirstOrDefault(r => r.TargetId == "char_2");
        Assert.NotNull(actorRel);
        Assert.Equal("Grappling", actorRel.Verb);

        var targetRel = target.SystemStats.EngagementRelations.FirstOrDefault(r => r.TargetId == "char_1");
        Assert.NotNull(targetRel);
        Assert.Equal("GrappledBy", targetRel.Verb);
    }

    [Fact]
    public async Task ApplyAsync_AcceptsCustomVerbWithoutEnum()
    {
        var actor = new Character { Id = "char_1", SystemStats = new SystemExtension() };
        var target = new Character { Id = "char_2", SystemStats = new SystemExtension() };
        var context = CreateContext(actor, target);

        var change = new EngagementRelationChange
        {
            ActorId = "char_1",
            TargetId = "char_2",
            Category = EngagementCategory.Social,
            Verb = "ranting at",
            Bidirectional = true
        };

        var result = await new EngagementRelationChangeHandler().ApplyAsync(change, context);

        Assert.True(result.Success);
        Assert.Equal("ranting at", actor.SystemStats.EngagementRelations[0].Verb);
        Assert.Equal("ranting at", target.SystemStats.EngagementRelations[0].Verb);
    }

    [Fact]
    public async Task ApplyAsync_RemovesRelation_WhenVerbAndRelationTypeAreNull()
    {
        var actor = new Character { Id = "char_1", SystemStats = new SystemExtension() };
        var target = new Character { Id = "char_2", SystemStats = new SystemExtension() };
        actor.SystemStats.EngagementRelations.Add(new EngagementRelation { TargetId = "char_2", Verb = "Grappling", Category = EngagementCategory.Physical });
        target.SystemStats.EngagementRelations.Add(new EngagementRelation { TargetId = "char_1", Verb = "GrappledBy", Category = EngagementCategory.Physical });

        var context = CreateContext(actor, target);

        var change = new EngagementRelationChange
        {
            ActorId = "char_1",
            TargetId = "char_2",
            Verb = null,
            RelationType = null,
            Bidirectional = true
        };

        var result = await new EngagementRelationChangeHandler().ApplyAsync(change, context);

        Assert.True(result.Success);
        Assert.Empty(actor.SystemStats.EngagementRelations);
        Assert.Empty(target.SystemStats.EngagementRelations);
    }

    [Fact]
    public async Task ApplyAsync_EstablishesUnidirectionalRelation()
    {
        var actor = new Character { Id = "char_1", SystemStats = new SystemExtension() };
        var target = new Character { Id = "char_2", SystemStats = new SystemExtension() };
        var context = CreateContext(actor, target);

        var change = new EngagementRelationChange
        {
            ActorId = "char_1",
            TargetId = "char_2",
            Category = EngagementCategory.Attention,
            Verb = "watching",
            Bidirectional = false
        };

        var result = await new EngagementRelationChangeHandler().ApplyAsync(change, context);

        Assert.True(result.Success);
        Assert.Single(actor.SystemStats.EngagementRelations);
        Assert.Equal("watching", actor.SystemStats.EngagementRelations[0].Verb);
        Assert.Empty(target.SystemStats.EngagementRelations);
    }
}
