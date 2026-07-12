using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;
using CampaignVault.Rulesets;
using CampaignVault.Services;
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
                new CampaignVault.Data.CampaignDocumentKeys(), NullLogger<WorldChangeDispatcher>.Instance),
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

        var actor = new Character { Id = "test-char", SystemStats = new Fallout2d20Extension { Endurance = 8 } };
        var context = CreateContext(actor);

        var action = new RulesetAction
        {
            CharacterId = "test-char",
            ActionType = RulesetActionType.SavingThrow,
            ActionName = "Poison Save",
            Parameters = new Dictionary<string, string> { { "difficulty", "1" }, { "attribute", "Endurance" } }
        };

        var output = await resolver.ResolveAsync(context, action);

        Assert.True(output.Result.Success);
        Assert.Contains("Success", output.Result.Narrative);
        await mockRollService.Received(1).RollAsync(Arg.Is<RollRequest>(req => req.Mechanic == DiceMechanic.SuccessCount), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveAttackAsync_NoHeldItem_FallsBackToWeaponDefinitionByName()
    {
        var mockRollService = Substitute.For<IRollService>();
        mockRollService.RollAsync(Arg.Any<RollRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new RollOutcome { Successes = 2, Summary = "2 Successes" }));
        mockRollService.RollFalloutCombatDiceAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new FalloutCombatDiceResult(1, 1, false)));

        var weaponDefs = new WeaponDefinitionProvider(
            Path.Combine(Path.GetTempPath(), "cv_weapondef_resolver_test_" + Guid.NewGuid()),
            typeof(WeaponDefinitionProvider).Assembly);
        var resolver = new Fallout2d20RulesetResolver(mockRollService, weaponDefs);

        var targetId = "char_2";
        var actor = new Character { Id = "test-char", SystemStats = new Fallout2d20Extension() };
        var target = new Character { Id = targetId, SystemStats = new Fallout2d20Extension { Defense = 1 } };
        var context = CreateContext(actor, target);

        // No held Item for "10mm Pistol" exists in context.Items — the resolver must fall back to
        // the reference weapons/10mm_pistol.yaml definition for damageDice/skill.
        var action = new RulesetAction
        {
            CharacterId = "test-char",
            TargetIds = [targetId],
            ActionType = RulesetActionType.Attack,
            ActionName = "10mm Pistol",
        };

        await resolver.ResolveAsync(context, action);

        Assert.Equal("2d6", action.Parameters["damageDice"]);
        Assert.Equal("SmallGuns", action.Parameters["skill"]);
        Assert.Equal("Physical", action.DamageType);
    }

    [Fact]
    public async Task ResolveAttackAsync_AppliesPiercingAndViciousEffects()
    {
        var mockRollService = Substitute.For<IRollService>();
        mockRollService.RollAsync(Arg.Any<RollRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new RollOutcome { Successes = 2, Summary = "2 Successes" }));
        
        mockRollService.RollFalloutCombatDiceAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new FalloutCombatDiceResult(1, 1, false)));

        var resolver = new Fallout2d20RulesetResolver(mockRollService);

        var targetId = "char_2";
        
        var actor = new Character { Id = "test-char", SystemStats = new Fallout2d20Extension() };
        var target = new Character { Id = targetId, SystemStats = new Fallout2d20Extension 
        { 
            Defense = 1, 
            DamageResistance = new Dictionary<string, int> { { "Physical", 2 } } 
        } };
        
        var context = CreateContext(actor, target);

        var action = new RulesetAction
        {
            CharacterId = "test-char",
            TargetIds = [targetId],
            ActionType = RulesetActionType.Attack,
            ActionName = "Combat Knife",
            DamageType = "Physical",
            Parameters = new Dictionary<string, string> 
            { 
                { "difficulty", "1" }, 
                { "damageDice", "3" },
                { "vicious", "true" },
                { "piercing", "1" }
            }
        };

        var output = await resolver.ResolveAsync(context, action);

        Assert.True(output.Result.Success);
        var hpChange = output.Mutations.OfType<HpChange>().FirstOrDefault();
        Assert.NotNull(hpChange);
        Assert.Equal(-1, hpChange.Delta);
    }

    [Fact]
    public async Task RollInitiativeAsync_DoesNotRollDice()
    {
        var mockRollService = Substitute.For<IRollService>();
        var resolver = new Fallout2d20RulesetResolver(mockRollService);

        var character = new Character 
        { 
            Id = "char_1", 
            SystemStats = new Fallout2d20Extension { Perception = 6, Agility = 7 } 
        };

        var initiativeResult = await resolver.RollInitiativeAsync(character);

        Assert.Equal(13f, initiativeResult);
        await mockRollService.DidNotReceiveWithAnyArgs().RollAsync(Arg.Any<RollRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveSkillCheckAsync_SurplusSuccessesButNoActionPointsPool_DoesNotFailOrEmitResourceChange()
    {
        var mockRollService = Substitute.For<IRollService>();
        mockRollService.RollAsync(Arg.Any<RollRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new RollOutcome { Successes = 3, Summary = "3 Successes" }));

        var resolver = new Fallout2d20RulesetResolver(mockRollService);

        // No ResourcePools set — simulates an NPC/creature not bootstrapped through character_create.
        var actor = new Character { Id = "test-char", SystemStats = new Fallout2d20Extension() };
        var context = CreateContext(actor);

        var action = new RulesetAction
        {
            CharacterId = "test-char",
            ActionType = RulesetActionType.SkillCheck,
            ActionName = "Lockpick",
            Parameters = new Dictionary<string, string> { { "difficulty", "1" } }
        };

        var output = await resolver.ResolveAsync(context, action);

        Assert.True(output.Result.Success, output.Result.Narrative);
        Assert.DoesNotContain(output.Mutations, m => m is ResourceChange);
        Assert.Contains("not tracked", output.Result.Narrative);
    }

    [Fact]
    public async Task ResolveSkillCheckAsync_SurplusSuccessesWithActionPointsPool_EmitsResourceChange()
    {
        var mockRollService = Substitute.For<IRollService>();
        mockRollService.RollAsync(Arg.Any<RollRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new RollOutcome { Successes = 3, Summary = "3 Successes" }));

        var resolver = new Fallout2d20RulesetResolver(mockRollService);

        var stats = new Fallout2d20Extension();
        stats.ResourcePools["action_points"] = new ResourcePool { Current = 5, Max = 10 };
        var actor = new Character { Id = "test-char", SystemStats = stats };
        var context = CreateContext(actor);

        var action = new RulesetAction
        {
            CharacterId = "test-char",
            ActionType = RulesetActionType.SkillCheck,
            ActionName = "Lockpick",
            Parameters = new Dictionary<string, string> { { "difficulty", "1" } }
        };

        var output = await resolver.ResolveAsync(context, action);

        Assert.True(output.Result.Success, output.Result.Narrative);
        var resourceChange = Assert.Single(output.Mutations.OfType<ResourceChange>());
        Assert.Equal("action_points", resourceChange.PoolName);
        Assert.Equal(2, resourceChange.Delta);
    }

    [Fact]
    public void GetTurnActionBudget_DefaultsToTenWhenNoPoolPresent()
    {
        var resolver = new Fallout2d20RulesetResolver(Substitute.For<IRollService>());
        var character = new Character { Id = "test-char", SystemStats = new Fallout2d20Extension() };

        var budget = resolver.GetTurnActionBudget(character);

        Assert.Equal(10, budget["ap"]);
    }

    [Fact]
    public void GetTurnActionBudget_ReadsMaxFromActionPointsResourcePool()
    {
        var resolver = new Fallout2d20RulesetResolver(Substitute.For<IRollService>());
        var stats = new Fallout2d20Extension();
        stats.ResourcePools["action_points"] = new ResourcePool { Current = 11, Max = 11 };
        var character = new Character { Id = "test-char", SystemStats = stats };

        var budget = resolver.GetTurnActionBudget(character);

        Assert.Equal(11, budget["ap"]);
    }

    [Fact]
    public void TryConsumeActionSlot_DefaultApCost_ConsumesOne()
    {
        var resolver = new Fallout2d20RulesetResolver(Substitute.For<IRollService>());
        var state = new CombatantState { CharacterId = "test-char", ActionBudget = new Dictionary<string, int> { { "ap", 2 } } };
        var action = new RulesetAction { CharacterId = "test-char", ActionType = RulesetActionType.Attack };

        var ok = resolver.TryConsumeActionSlot(state, action, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(1, state.ActionBudget["ap"]);
    }

    [Fact]
    public void TryConsumeActionSlot_ApCostTwo_ConsumesTwo()
    {
        var resolver = new Fallout2d20RulesetResolver(Substitute.For<IRollService>());
        var state = new CombatantState { CharacterId = "test-char", ActionBudget = new Dictionary<string, int> { { "ap", 5 } } };
        var action = new RulesetAction
        {
            CharacterId = "test-char",
            ActionType = RulesetActionType.Attack,
            Parameters = new Dictionary<string, string> { { "apCost", "2" } }
        };

        var ok = resolver.TryConsumeActionSlot(state, action, out var error);

        Assert.True(ok);
        Assert.Equal(3, state.ActionBudget["ap"]);
    }

    [Fact]
    public void TryConsumeActionSlot_InsufficientAp_ReturnsFalse()
    {
        var resolver = new Fallout2d20RulesetResolver(Substitute.For<IRollService>());
        var state = new CombatantState { CharacterId = "test-char", ActionBudget = new Dictionary<string, int> { { "ap", 1 } } };
        var action = new RulesetAction
        {
            CharacterId = "test-char",
            ActionType = RulesetActionType.Attack,
            Parameters = new Dictionary<string, string> { { "apCost", "2" } }
        };

        var ok = resolver.TryConsumeActionSlot(state, action, out var error);

        Assert.False(ok);
        Assert.NotNull(error);
        Assert.Equal(1, state.ActionBudget["ap"]);
    }

    [Fact]
    public void TryConsumeActionSlot_Reaction_Bypasses()
    {
        var resolver = new Fallout2d20RulesetResolver(Substitute.For<IRollService>());
        var state = new CombatantState { CharacterId = "test-char", ActionBudget = new Dictionary<string, int> { { "ap", 0 } } };
        var action = new RulesetAction { CharacterId = "test-char", ActionType = RulesetActionType.Attack, IsReaction = true };

        var ok = resolver.TryConsumeActionSlot(state, action, out var error);

        Assert.True(ok);
        Assert.Null(error);
    }
}
