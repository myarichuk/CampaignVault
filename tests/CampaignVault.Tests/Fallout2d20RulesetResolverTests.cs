using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;
using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;
using CampaignVault.Rulesets;
using Microsoft.Extensions.Logging.Abstractions;

namespace CampaignVault.Tests;

public class Fallout2d20RulesetResolverTests
{
    private ChangeContext CreateContext(params Character[] characters)
    {
        var charDict = new Dictionary<string, Character>();
        foreach (var c in characters) charDict[c.Id] = c;
        
        return new ChangeContext(
            sessionForTests: null,
            characters: charDict,
            items: new Dictionary<string, Item>(),
            locations: new Dictionary<string, Location>(),
            factions: new Dictionary<string, Faction>(),
            quests: new Dictionary<string, Quest>(),
            logger: NullLogger.Instance,
            summary: new List<string>(),
            dispatcher: new WorldChangeDispatcher(
                new IWorldChangeHandler[0], 
                NullLogger<WorldChangeDispatcher>.Instance),
            campaignName: null
        );
    }

    [Fact]
    public async Task ResolveSkillCheck_CountsSuccessesAndGeneratesAP()
    {
        var rollService = new FakeRollService();
        rollService.NextRolls.Enqueue(new RollOutcome { Successes = 3, Summary = "Rolled 3 successes" });

        var resolver = new Fallout2d20RulesetResolver(rollService);
        
        var actor = new Character 
        { 
            Id = "char1", 
            SystemStats = new Fallout2d20Extension 
            { 
                Agility = 8, 
                Skills = new Dictionary<string, int> { { "Sneak", 4 } } 
            } 
        };

        var context = CreateContext(actor);
        var action = new RulesetAction
        {
            ActorId = "char1",
            ActionType = RulesetActionType.SkillCheck,
            ActionName = "Hide",
            Parameters = new Dictionary<string, string> { 
                ["attribute"] = "Agility", 
                ["skill"] = "Sneak",
                ["difficulty"] = "1"
            }
        };

        var output = await resolver.ResolveAsync(context, action);

        Assert.Contains("Success", output.Result.Narrative);
        Assert.Contains("Generated 2 AP", output.Result.Narrative); // 3 successes - 1 difficulty = 2 AP
        Assert.Contains("TN 12", output.Result.Narrative); // Agility 8 + Sneak 4
    }

    [Fact]
    public async Task ResolveAttack_AppliesDamageResistance()
    {
        var rollService = new FakeRollService();
        rollService.NextRolls.Enqueue(new RollOutcome { Successes = 2, Summary = "Rolled 2 successes" }); // Attack hits
        rollService.NextFalloutRolls.Enqueue(new FalloutCombatDiceResult(Damage: 5, Effects: 1, HasCritical: false)); // 5 Damage

        var resolver = new Fallout2d20RulesetResolver(rollService);
        
        var actor = new Character { Id = "char1", SystemStats = new Fallout2d20Extension() };
        var target = new Character 
        { 
            Id = "char2", 
            SystemStats = new Fallout2d20Extension 
            { 
                Defense = 1,
                DamageResistance = new Dictionary<string, int> { { "Energy", 2 } } 
            } 
        };

        var context = CreateContext(actor, target);
        var action = new RulesetAction
        {
            ActorId = "char1",
            TargetIds = new List<string> { "char2" },
            ActionType = RulesetActionType.Attack,
            ActionName = "Laser Rifle",
            Parameters = new Dictionary<string, string> { ["damageType"] = "Energy" }
        };

        var output = await resolver.ResolveAsync(context, action);

        Assert.Single(output.Mutations);
        var hpChange = Assert.IsType<HpChange>(output.Mutations[0]);
        Assert.Equal(-3, hpChange.Delta); // 5 damage - 2 Energy DR = 3 final damage
        Assert.Contains("Hit for 3 damage", output.Result.Narrative);
    }

    [Fact]
    public async Task ResolveAttack_InvalidDifficulty_ReturnsError()
    {
        var rollService = new FakeRollService();
        var resolver = new Fallout2d20RulesetResolver(rollService);
        var actor = new Character { Id = "char1", SystemStats = new Fallout2d20Extension() };
        var target = new Character { Id = "char2", SystemStats = new Fallout2d20Extension() };

        var context = CreateContext(actor, target);
        var action = new RulesetAction
        {
            ActorId = "char1",
            TargetIds = new List<string> { "char2" },
            ActionType = RulesetActionType.Attack,
            Parameters = new Dictionary<string, string> { ["difficulty"] = "not_a_number" }
        };

        var output = await resolver.ResolveAsync(context, action);

        Assert.Contains("invalid difficulty value", output.Result.Narrative);
    }

    [Fact]
    public async Task ResolveAttack_MismatchedTargetExtension_ReturnsError()
    {
        var rollService = new FakeRollService();
        var resolver = new Fallout2d20RulesetResolver(rollService);
        
        var actor = new Character { Id = "char1", SystemStats = new Fallout2d20Extension() };
        var target = new Character { Id = "char2", SystemStats = new Pf2eExtension() }; // Mismatched

        var context = CreateContext(actor, target);
        var action = new RulesetAction
        {
            ActorId = "char1",
            TargetIds = new List<string> { "char2" },
            ActionType = RulesetActionType.Attack
        };

        var output = await resolver.ResolveAsync(context, action);

        Assert.Contains("incompatible ruleset stats", output.Result.Narrative);
    }
}
