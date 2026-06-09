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

public class Fallout2d20RulesetResolverTests
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
    public async Task ResolveAsync_SavingThrow_UsesSuccessCount()
    {
        var mockRollService = Substitute.For<IRollService>();
        mockRollService.RollAsync(Arg.Any<RollRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new RollOutcome { Successes = 2, Summary = "2 Successes" }));

        var resolver = new Fallout2d20RulesetResolver(mockRollService);

        var actorId = "char_1";
        var actor = new Character { Id = actorId, SystemStats = new Fallout2d20Extension { Endurance = 8 } };
        var context = CreateContext(actor);

        var action = new RulesetAction
        {
            ActorId = actorId,
            ActionType = RulesetActionType.SavingThrow,
            ActionName = "Poison Save",
            Parameters = new Dictionary<string, string> { { "difficulty", "1" }, { "attribute", "Endurance" } }
        };

        var output = await resolver.ResolveAsync(context, action);

        Assert.True(output.Result.Success);
        Assert.Contains("Success", output.Result.Narrative);
        await mockRollService.Received(1).RollAsync(Arg.Is<RollRequest>(req => req.Mechanic == DiceMechanic.SuccessCount), Arg.Any<CancellationToken>());
    }
}
