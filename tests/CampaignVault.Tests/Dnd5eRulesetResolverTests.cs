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
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 15, HasCritical = false, HasComplication = false, Summary = "Rolled 15" });
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 8, Summary = "Rolled 8" });

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
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 12, HasCritical = false, HasComplication = false, Summary = "Rolled 12" });
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 8, Summary = "Rolled 8" });

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
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 20, HasCritical = true, HasComplication = false, Summary = "Rolled Nat 20" });
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 8, Summary = "Rolled 8" });
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

    [Fact]
    public async Task ResolveAttack_InvalidBonus_ReturnsError()
    {
        var rollService = new FakeRollService();
        var resolver = new Dnd5eRulesetResolver(rollService);
        var actor = new Character { Id = "char1", SystemStats = new Dnd5eExtension() };
        var target = new Character { Id = "char2", SystemStats = new Dnd5eExtension() };

        var context = CreateContext(actor, target);
        var action = new RulesetAction
        {
            ActorId = "char1",
            TargetIds = new List<string> { "char2" },
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
        var resolver = new Dnd5eRulesetResolver(rollService);
        
        // Actor is correct, but target is using a different system's extension (e.g. Pf2eExtension or base SystemExtension)
        var actor = new Character { Id = "char1", SystemStats = new Dnd5eExtension() };
        var target = new Character { Id = "char2", SystemStats = new Pf2eExtension() };

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

    [Fact]
    public async Task ResolveContestedCheck_Success()
    {
        var rollService = new FakeRollService();
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 18 }); // Actor
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 12 }); // Target

        var resolver = new Dnd5eRulesetResolver(rollService);
        var actor = new Character { Id = "char1", SystemStats = new Dnd5eExtension() };
        var target = new Character { Id = "char2", SystemStats = new Dnd5eExtension() };

        var context = CreateContext(actor, target);
        var action = new RulesetAction
        {
            ActorId = "char1",
            TargetIds = new List<string> { "char2" },
            ActionType = RulesetActionType.ContestedCheck,
            ActionName = "Grapple"
        };

        var output = await resolver.ResolveAsync(context, action);

        Assert.Contains("Actor Wins", output.Result.Narrative);
    }
}
