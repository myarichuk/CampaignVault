using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;
using CampaignVault.Rulesets;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace CampaignVault.Tests;

public class FalloutRulesGapsTests
{
    private static ChangeContext CreateContext(params Character[] characters)
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
                [],
                new CampaignVault.Data.CampaignDocumentKeys(),
                NullLogger<WorldChangeDispatcher>.Instance),
            campaignName: null);
    }

    [Fact]
    public async Task ResolveAttack_AppliesRangeAndCover_ToDifficulty()
    {
        var mockRoll = Substitute.For<IRollService>();
        mockRoll.RollAsync(Arg.Any<RollRequest>(), Arg.Any<System.Threading.CancellationToken>())
            .Returns(Task.FromResult(new RollOutcome { Successes = 1, Summary = "1 success" }));

        var resolver = new Fallout2d20RulesetResolver(mockRoll);
        var actor = new Character { Id = "actor", SystemStats = new Fallout2d20Extension() };
        var target = new Character
        {
            Id = "target",
            Name = "Raider",
            SystemStats = new Fallout2d20Extension { Defense = 1 },
        };

        var action = new RulesetAction
        {
            ActorId = "actor",
            TargetIds = ["target"],
            ActionType = RulesetActionType.Attack,
            ActionName = "Hunting Rifle",
            Parameters = new Dictionary<string, string>
            {
                ["rangeModifier"] = "1",
                ["cover"] = "1",
            },
        };

        var output = await resolver.ResolveAsync(CreateContext(actor, target), action);

        Assert.Contains("need 3 successes", output.Result.Narrative);
    }

    [Fact]
    public async Task ResolveSpell_Utility_ScienceCheck_OutsideCombat()
    {
        var mockRoll = Substitute.For<IRollService>();
        mockRoll.RollAsync(Arg.Any<RollRequest>(), Arg.Any<System.Threading.CancellationToken>())
            .Returns(Task.FromResult(new RollOutcome { Successes = 2, Summary = "2 successes" }));

        var resolver = new Fallout2d20RulesetResolver(mockRoll);
        var actor = new Character
        {
            Id = "actor",
            SystemStats = new Fallout2d20Extension
            {
                Intelligence = 8,
                Skills = new Dictionary<string, int> { ["Science"] = 2 },
            },
        };

        var action = new RulesetAction
        {
            ActorId = "actor",
            ActionType = RulesetActionType.Spell,
            ActionName = "Fabricate Chem",
            Parameters = new Dictionary<string, string>
            {
                ["resolution"] = "utility",
                ["dc"] = "2",
                ["skill"] = "Science",
                ["attribute"] = "Intelligence",
            },
        };

        var output = await resolver.ResolveAsync(CreateContext(actor), action);

        Assert.True(output.Result.Success);
        Assert.Contains("Success", output.Result.Narrative);
    }

    [Fact]
    public async Task ResolveRecovery_Stimpak_HealsTarget()
    {
        var mockRoll = Substitute.For<IRollService>();
        var resolver = new Fallout2d20RulesetResolver(mockRoll);
        var actor = new Character { Id = "actor", SystemStats = new Fallout2d20Extension() };
        var ally = new Character { Id = "ally", SystemStats = new Fallout2d20Extension() };

        var action = new RulesetAction
        {
            ActorId = "actor",
            TargetIds = ["ally"],
            ActionType = RulesetActionType.UseItem,
            ActionName = "Stimpak",
            Parameters = new Dictionary<string, string> { ["healAmount"] = "8" },
        };

        var output = await resolver.ResolveAsync(CreateContext(actor, ally), action);

        var hp = Assert.Single(output.Mutations.OfType<HpChange>());
        Assert.Equal("ally", hp.CharacterId);
        Assert.Equal(8, hp.Delta);
    }

    [Fact]
    public async Task ResolveAttack_IncludesTargetPart_InNarrative()
    {
        var mockRoll = Substitute.For<IRollService>();
        mockRoll.RollAsync(Arg.Any<RollRequest>(), Arg.Any<System.Threading.CancellationToken>())
            .Returns(Task.FromResult(new RollOutcome { Successes = 2, Summary = "2 successes" }));
        mockRoll.RollFalloutCombatDiceAsync(Arg.Any<int>(), Arg.Any<System.Threading.CancellationToken>())
            .Returns(Task.FromResult(new FalloutCombatDiceResult(2, 0, false)));

        var resolver = new Fallout2d20RulesetResolver(mockRoll);
        var actor = new Character { Id = "actor", SystemStats = new Fallout2d20Extension() };
        var target = new Character
        {
            Id = "target",
            Name = "Raider",
            SystemStats = new Fallout2d20Extension { Defense = 1 },
        };

        var action = new RulesetAction
        {
            ActorId = "actor",
            TargetIds = ["target"],
            ActionType = RulesetActionType.Attack,
            ActionName = "Called Shot",
            Parameters = new Dictionary<string, string>
            {
                ["targetPart"] = "Head",
            },
        };

        var output = await resolver.ResolveAsync(CreateContext(actor, target), action);

        Assert.Contains("Location: Head", output.Result.Narrative);
    }
}