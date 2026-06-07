using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;
using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;
using CampaignVault.Rulesets;
using Microsoft.Extensions.Logging.Abstractions;

namespace CampaignVault.Tests;

public class Pf2eRulesetResolverTests
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
            summary: [],
            dispatcher: new WorldChangeDispatcher(
                new IWorldChangeHandler[0], 
                NullLogger<WorldChangeDispatcher>.Instance),
            campaignName: null
        );
    }

    [Fact]
    public async Task ResolveAttack_MarginPlus10_IsCriticalSuccess_DoublesDamage()
    {
        var rollService = new FakeRollService();
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 25, Summary = "Rolled 25" }); // Attack vs AC 15 (Margin 10)
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 8, Summary = "Rolled 8" });  // Base Damage

        var resolver = new Pf2eRulesetResolver(rollService);

        var actor = new Character { Id = "char1", SystemStats = new Pf2eExtension() };
        var target = new Character { Id = "char2", SystemStats = new Pf2eExtension { ArmorClass = 15 } };

        var context = CreateContext(actor, target);
        var action = new RulesetAction
        {
            ActorId = "char1",
            TargetIds = ["char2"],
            ActionType = RulesetActionType.Attack,
            ActionName = "Longsword",
            Parameters = new Dictionary<string, string> { ["damageDice"] = "1d8" }
        };

        var output = await resolver.ResolveAsync(context, action);

        Assert.Single(output.Mutations);
        var hpChange = Assert.IsType<HpChange>(output.Mutations[0]);
        Assert.Equal(-16, hpChange.Delta); // 8 base * 2
        Assert.Contains("CriticalSuccess", output.Result.Narrative);
    }

    [Fact]
    public async Task ResolveAttack_Nat20_UpgradesSuccess_ToCriticalSuccess()
    {
        var rollService = new FakeRollService();
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 22, HasCritical = true, Summary = "Nat 20" }); // Attack vs AC 15 (Margin 7) -> Upgraded
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 5, Summary = "Rolled 5" }); // Base Damage

        var resolver = new Pf2eRulesetResolver(rollService);

        var actor = new Character { Id = "char1", SystemStats = new Pf2eExtension() };
        var target = new Character { Id = "char2", SystemStats = new Pf2eExtension { ArmorClass = 15 } };

        var context = CreateContext(actor, target);
        var action = new RulesetAction
        {
            ActorId = "char1",
            TargetIds = ["char2"],
            ActionType = RulesetActionType.Attack,
            ActionName = "Longsword"
        };

        var output = await resolver.ResolveAsync(context, action);

        Assert.Single(output.Mutations);
        var hpChange = Assert.IsType<HpChange>(output.Mutations[0]);
        Assert.Equal(-10, hpChange.Delta); // 5 base * 2
        Assert.Contains("CriticalSuccess", output.Result.Narrative);
    }

    [Fact]
    public async Task ResolveSkillCheck_MarginMinus10_IsCriticalFailure()
    {
        var rollService = new FakeRollService();
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 4, Summary = "Rolled 4" }); // vs DC 15 (Margin -11)

        var resolver = new Pf2eRulesetResolver(rollService);
        var actor = new Character { Id = "char1", SystemStats = new Pf2eExtension() };

        var context = CreateContext(actor);
        var action = new RulesetAction
        {
            ActorId = "char1",
            ActionType = RulesetActionType.SkillCheck,
            ActionName = "Recall Knowledge",
            Parameters = new Dictionary<string, string> { ["skill"] = "Arcana", ["dc"] = "15" }
        };

        var output = await resolver.ResolveAsync(context, action);

        Assert.Empty(output.Mutations);
        Assert.Contains("CriticalFailure", output.Result.Narrative);
    }

    [Fact]
    public async Task ResolveAttack_InvalidBonus_ReturnsError()
    {
        var rollService = new FakeRollService();
        var resolver = new Pf2eRulesetResolver(rollService);
        var actor = new Character { Id = "char1", SystemStats = new Pf2eExtension() };
        var target = new Character { Id = "char2", SystemStats = new Pf2eExtension() };

        var context = CreateContext(actor, target);
        var action = new RulesetAction
        {
            ActorId = "char1",
            TargetIds = ["char2"],
            ActionType = RulesetActionType.Attack,
            Parameters = new Dictionary<string, string> { ["bonus"] = "not_a_number" }
        };

        var output = await resolver.ResolveAsync(context, action);

        Assert.Contains("invalid bonus value", output.Result.Narrative);
    }

    [Fact]
    public async Task ResolveAttack_MismatchedTargetExtension_ReturnsError()
    {
        var rollService = new FakeRollService();
        var resolver = new Pf2eRulesetResolver(rollService);
        
        var actor = new Character { Id = "char1", SystemStats = new Pf2eExtension() };
        var target = new Character { Id = "char2", SystemStats = new Dnd5eExtension() }; // Mismatched

        var context = CreateContext(actor, target);
        var action = new RulesetAction
        {
            ActorId = "char1",
            TargetIds = ["char2"],
            ActionType = RulesetActionType.Attack
        };

        var output = await resolver.ResolveAsync(context, action);

        Assert.Contains("incompatible ruleset stats", output.Result.Narrative);
    }
}
