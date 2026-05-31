using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;
using CampaignVault.Rulesets;
using Microsoft.Extensions.Logging.Abstractions;
using Raven.Client.Documents.Session;
using System.Linq;

namespace CampaignVault.Tests;

public class FakeRollService : IRollService
{
    public Queue<RollOutcome> NextRolls { get; } = new();
    public Queue<IReadOnlyList<RollOutcome>> NextBatches { get; } = new();
    public Queue<FalloutCombatDiceResult> NextFalloutRolls { get; } = new();

    public Task<RollOutcome> RollAsync(RollRequest request, CancellationToken ct = default)
    {
        return Task.FromResult(NextRolls.Dequeue());
    }

    public Task<IReadOnlyList<RollOutcome>> RollBatchAsync(IEnumerable<RollRequest> requests, CancellationToken ct = default)
    {
        return Task.FromResult(NextBatches.Dequeue());
    }

    public Task<FalloutCombatDiceResult> RollFalloutCombatDiceAsync(int count, CancellationToken ct = default)
    {
        return Task.FromResult(NextFalloutRolls.Dequeue());
    }
}

public class Dnd5eRulesetResolverTests
{
    private ChangeContext CreateContext(params Character[] characters)
    {
        var charDict = characters.ToDictionary(c => c.Id);
        return new ChangeContext(
            sessionForTests: null,
            characters: charDict,
            items: new Dictionary<string, Item>(),
            logger: NullLogger.Instance,
            summary: new List<string>(),
            dispatcher: new WorldChangeDispatcher(
                new IWorldChangeHandler[0], 
                NullLogger<WorldChangeDispatcher>.Instance)
        );
    }

    [Fact]
    public async Task ResolveAttack_Hit_GeneratesHpChange()
    {
        var rollService = new FakeRollService();
        rollService.NextBatches.Enqueue(new List<RollOutcome>
        {
            new RollOutcome { Result = 15, HasCritical = false, HasComplication = false, Summary = "Rolled 15" },
            new RollOutcome { Result = 8, Summary = "Rolled 8" }
        });

        var resolver = new Dnd5eRulesetResolver(rollService);

        var actor = new Character { Id = "char1", SystemStats = new Dnd5eExtension() };
        var target = new Character { Id = "char2", SystemStats = new Dnd5eExtension { ArmorClass = 14 } };

        var context = CreateContext(actor, target);
        var action = new RulesetAction
        {
            ActorId = "char1",
            TargetIds = new List<string> { "char2" },
            ActionType = RulesetActionType.Attack,
            ActionName = "Longsword",
            Parameters = new Dictionary<string, string> { ["damageDice"] = "1d8" }
        };

        var output = await resolver.ResolveAsync(context, action);

        Assert.Single(output.Mutations);
        var hpChange = Assert.IsType<HpChange>(output.Mutations[0]);
        Assert.Equal("char2", hpChange.CharacterId);
        Assert.Equal(-8, hpChange.Delta);
        Assert.Contains("Hit for 8 damage", output.Result.Narrative);
    }

    [Fact]
    public async Task ResolveAttack_Miss_GeneratesNoMutations()
    {
        var rollService = new FakeRollService();
        rollService.NextBatches.Enqueue(new List<RollOutcome>
        {
            new RollOutcome { Result = 12, HasCritical = false, HasComplication = false, Summary = "Rolled 12" },
            new RollOutcome { Result = 8, Summary = "Rolled 8" }
        });

        var resolver = new Dnd5eRulesetResolver(rollService);

        var actor = new Character { Id = "char1", SystemStats = new Dnd5eExtension() };
        var target = new Character { Id = "char2", SystemStats = new Dnd5eExtension { ArmorClass = 14 } };

        var context = CreateContext(actor, target);
        var action = new RulesetAction
        {
            ActorId = "char1",
            TargetIds = new List<string> { "char2" },
            ActionType = RulesetActionType.Attack,
            ActionName = "Longsword"
        };

        var output = await resolver.ResolveAsync(context, action);

        Assert.Empty(output.Mutations);
        Assert.Contains("Missed", output.Result.Narrative);
    }

    [Fact]
    public async Task ResolveAttack_CriticalHit_RollsExtraDamage()
    {
        var rollService = new FakeRollService();
        rollService.NextBatches.Enqueue(new List<RollOutcome>
        {
            new RollOutcome { Result = 20, HasCritical = true, HasComplication = false, Summary = "Rolled Nat 20" },
            new RollOutcome { Result = 8, Summary = "Rolled 8" }
        });
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 5, Summary = "Rolled 5" });

        var resolver = new Dnd5eRulesetResolver(rollService);

        var actor = new Character { Id = "char1", SystemStats = new Dnd5eExtension() };
        var target = new Character { Id = "char2", SystemStats = new Dnd5eExtension { ArmorClass = 25 } };

        var context = CreateContext(actor, target);
        var action = new RulesetAction
        {
            ActorId = "char1",
            TargetIds = new List<string> { "char2" },
            ActionType = RulesetActionType.Attack,
            ActionName = "Longsword",
            Parameters = new Dictionary<string, string> { ["damageDice"] = "1d8" }
        };

        var output = await resolver.ResolveAsync(context, action);

        Assert.Single(output.Mutations);
        var hpChange = Assert.IsType<HpChange>(output.Mutations[0]);
        Assert.Equal(-13, hpChange.Delta);
        Assert.Contains("CRITICAL HIT!", output.Result.Narrative);
    }

    [Fact]
    public async Task ResolveSkillCheck_SucceedsAgainstDC()
    {
        var rollService = new FakeRollService();
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 16, Summary = "Rolled 16" });

        var resolver = new Dnd5eRulesetResolver(rollService);
        var actor = new Character 
        { 
            Id = "char1", 
            SystemStats = new Dnd5eExtension { SkillModifiers = new Dictionary<string, int> { { "Stealth", 5 } } } 
        };

        var context = CreateContext(actor);
        var action = new RulesetAction
        {
            ActorId = "char1",
            ActionType = RulesetActionType.SkillCheck,
            ActionName = "Sneak",
            Parameters = new Dictionary<string, string> { ["skill"] = "Stealth", ["dc"] = "15" }
        };

        var output = await resolver.ResolveAsync(context, action);

        Assert.Empty(output.Mutations);
        Assert.Contains("Success", output.Result.Narrative);
    }
}
