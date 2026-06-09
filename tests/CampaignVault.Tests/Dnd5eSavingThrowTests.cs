using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using Xunit;
using CampaignVault.Data;
using CampaignVault.Models;
using CampaignVault.Rulesets;
using Microsoft.Extensions.Logging.Abstractions;
using System.Linq;
using CampaignVault.Data.ChangeHandlers;

namespace CampaignVault.Tests;

public class Dnd5eSavingThrowTests
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
                NullLogger<WorldChangeDispatcher>.Instance),
            campaignName: null
        );
    }

    [Fact]
    public async Task ResolveSavingThrow_UsesSavingThrowModifiers()
    {
        var mockRollService = Substitute.For<IRollService>();
        mockRollService.RollAsync(Arg.Any<RollRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new RollOutcome { Result = 15, Summary = "Rolled 15" }));

        var resolver = new Dnd5eRulesetResolver(mockRollService);

        var actorId = "char_1";
        // Dexterity 10 (mod +0) + SavingThrowModifier for Dexterity (+5) = Total Bonus +5
        var actor = new Character 
        { 
            Id = actorId, 
            SystemStats = new Dnd5eExtension 
            { 
                Dexterity = 10,
                SavingThrowModifiers = new Dictionary<string, int> { { "Dexterity", 5 } }
            } 
        };
        var context = CreateContext(actor);

        var action = new RulesetAction
        {
            ActorId = actorId,
            ActionType = RulesetActionType.SavingThrow,
            ActionName = "Dexterity Save",
            Parameters = new Dictionary<string, string> { { "dc", "15" }, { "save", "Dexterity" } }
        };

        await resolver.ResolveAsync(context, action);

        // Verify that the roll request had a bonus of 5
        await mockRollService.Received(1).RollAsync(Arg.Is<RollRequest>(req => req.Bonus == 5), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveSavingThrow_UsesAllSavesStatusEffect()
    {
        var mockRollService = Substitute.For<IRollService>();
        var bonusPassed = 0;
        
        mockRollService.RollAsync(Arg.Any<RollRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => 
            {
                var req = callInfo.Arg<RollRequest>();
                bonusPassed = req.Bonus;
                return Task.FromResult(new RollOutcome { Result = 15, Summary = "Rolled 15" });
            });

        var resolver = new Dnd5eRulesetResolver(mockRollService);

        var actorId = "char_1";
        // Dexterity 10 (mod +0) + StatusEffect with 'AllSaves' (+2.5 -> floor to 2) = Total Bonus +2
        var actor = new Character 
        { 
            Id = actorId, 
            SystemStats = new Dnd5eExtension 
            { 
                Dexterity = 10,
                StatusEffects = new List<StatusEffect>
                {
                    new StatusEffect
                    {
                        Name = "Bless",
                        StatModifiers = new Dictionary<string, float> { { "AllSaves", 2.0f } }
                    }
                }
            } 
        };
        var context = CreateContext(actor);

        var action = new RulesetAction
        {
            ActorId = actorId,
            ActionType = RulesetActionType.SavingThrow,
            ActionName = "Dexterity Save",
            Parameters = new Dictionary<string, string> { { "dc", "15" }, { "save", "Dexterity" } }
        };

        await resolver.ResolveAsync(context, action);

        // Verify that the roll request had a bonus of 2
        Assert.Equal(2, bonusPassed);
    }
}
