using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;
using CampaignVault.Rulesets;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace CampaignVault.Tests;

public class Pf2eRulesetResolverTests
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
                new IWorldChangeHandler[0], 
                new CampaignVault.Data.CampaignDocumentKeys(), NullLogger<WorldChangeDispatcher>.Instance),
            campaignName: null
        );
    }

    [Fact]
    public async Task ResolveAsync_SavingThrow_CalculatesDegreeOfSuccess()
    {
        var mockRollService = Substitute.For<IRollService>();
        mockRollService.RollAsync(Arg.Any<RollRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new RollOutcome { Result = 25, Summary = "Rolled 25" }));

        var resolver = new Pf2eRulesetResolver(mockRollService);

        var actorId = "char_1";
        var actor = new Character { Id = actorId, SystemStats = new Pf2eExtension() };
        var context = CreateContext(actor);

        var action = new RulesetAction
        {
            ActorId = actorId,
            ActionType = RulesetActionType.SavingThrow,
            ActionName = "Reflex Save",
            Parameters = new Dictionary<string, string> { { "dc", "15" }, { "save", "Dexterity" } }
        };

        var output = await resolver.ResolveAsync(context, action);

        Assert.True(output.Result.Success);
        Assert.Contains("CriticalSuccess", output.Result.Narrative);
    }

    [Fact]
    public async Task ResolveAttackAsync_AppliesMapPenalty()
    {
        var mockRollService = Substitute.For<IRollService>();
        mockRollService.RollAsync(Arg.Any<RollRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new RollOutcome { Result = 12, Summary = "Rolled 12" }));

        var resolver = new Pf2eRulesetResolver(mockRollService);

        var actorId = "char_1";
        var targetId = "char_2";
        
        var actor = new Character { Id = actorId, SystemStats = new Pf2eExtension() };
        var target = new Character { Id = targetId, SystemStats = new Pf2eExtension { ArmorClass = 10 } };
        
        var context = CreateContext(actor, target);

        var action = new RulesetAction
        {
            ActorId = actorId,
            TargetIds = [targetId],
            ActionType = RulesetActionType.Attack,
            ActionName = "Fist",
            Parameters = new Dictionary<string, string> 
            { 
                { "bonus", "4" },
                { "mapPenalty", "5" } 
            }
        };

        var output = await resolver.ResolveAsync(context, action);

        await mockRollService.Received(1).RollAsync(
            Arg.Is<RollRequest>(req => req.Tag == "attack" && req.Bonus == -1), 
            Arg.Any<CancellationToken>()
        );
    }
}
