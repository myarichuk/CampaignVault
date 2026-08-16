using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;
using CampaignVault.Rulesets;
using CampaignVault.Rulesets.Bootstrap;
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

        var actor = new Character { Id = "test-char", SystemStats = new Pf2eExtension() };
        var context = CreateContext(actor);

        var action = new RulesetAction
        {
            CharacterId = "test-char",
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

        var targetId = "char_2";
        
        var actor = new Character { Id = "test-char", SystemStats = new Pf2eExtension() };
        var target = new Character { Id = targetId, SystemStats = new Pf2eExtension { ArmorClass = 10 } };
        
        var context = CreateContext(actor, target);

        var action = new RulesetAction
        {
            CharacterId = "test-char",
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

    [Fact]
    public async Task ResolveSpellSaveAsync_NoDcParameter_FallsBackToActorSpellDc()
    {
        var mockRollService = Substitute.For<IRollService>();
        mockRollService.RollAsync(Arg.Any<RollRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new RollOutcome { Result = 10, Summary = "Rolled 10" }));

        var resolver = new Pf2eRulesetResolver(mockRollService);

        var actor = new Character { Id = "caster", SystemStats = new Pf2eExtension { SpellDc = 18 } };
        var target = new Character { Id = "target", SystemStats = new Pf2eExtension() };
        var context = CreateContext(actor, target);

        var action = new RulesetAction
        {
            CharacterId = "caster",
            TargetIds = ["target"],
            ActionType = RulesetActionType.Spell,
            ActionName = "Fireball",
            Parameters = new Dictionary<string, string> { { "resolution", "save" }, { "save", "Reflex" } }
        };

        var output = await resolver.ResolveAsync(context, action);

        Assert.True(output.Result.Success, output.Result.Narrative);
        Assert.Contains("DC 18", output.Result.Narrative);
    }

    [Fact]
    public async Task ResolveSkillCheckAsync_NoDcParameterAndNoSpellDc_StillFails()
    {
        var resolver = new Pf2eRulesetResolver(Substitute.For<IRollService>());

        var actor = new Character { Id = "test-char", SystemStats = new Pf2eExtension() };
        var context = CreateContext(actor);

        var action = new RulesetAction
        {
            CharacterId = "test-char",
            ActionType = RulesetActionType.SkillCheck,
            ActionName = "Athletics",
            Parameters = new Dictionary<string, string>()
        };

        var output = await resolver.ResolveAsync(context, action);

        Assert.False(output.Result.Success);
        Assert.Contains("requires a 'dc' parameter", output.Result.Narrative);
    }

    [Fact]
    public void GetTurnActionBudget_ReturnsThreeActions()
    {
        var resolver = new Pf2eRulesetResolver(Substitute.For<IRollService>());
        var character = new Character { Id = "test-char", SystemStats = new Pf2eExtension() };

        var budget = resolver.GetTurnActionBudget(character);

        Assert.Equal(3, budget["actions"]);
        Assert.False(budget.ContainsKey("reaction"));
    }

    [Fact]
    public void TryConsumeActionSlot_DefaultCost_DecrementsByOne()
    {
        var resolver = new Pf2eRulesetResolver(Substitute.For<IRollService>());
        var state = new CombatantState { CharacterId = "test-char", ActionBudget = new Dictionary<string, int> { { "actions", 3 } } };
        var action = new RulesetAction { CharacterId = "test-char", ActionType = RulesetActionType.Attack };

        var ok = resolver.TryConsumeActionSlot(state, action, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(2, state.ActionBudget["actions"]);
    }

    [Fact]
    public void TryConsumeActionSlot_ActionCostTwo_ConsumesTwo()
    {
        var resolver = new Pf2eRulesetResolver(Substitute.For<IRollService>());
        var state = new CombatantState { CharacterId = "test-char", ActionBudget = new Dictionary<string, int> { { "actions", 3 } } };
        var action = new RulesetAction
        {
            CharacterId = "test-char",
            ActionType = RulesetActionType.Attack,
            Parameters = new Dictionary<string, string> { { "actionCost", "2" } }
        };

        var ok = resolver.TryConsumeActionSlot(state, action, out var error);

        Assert.True(ok);
        Assert.Equal(1, state.ActionBudget["actions"]);
    }

    [Fact]
    public void TryConsumeActionSlot_InsufficientActions_ReturnsFalseWithReason()
    {
        var resolver = new Pf2eRulesetResolver(Substitute.For<IRollService>());
        var state = new CombatantState { CharacterId = "test-char", ActionBudget = new Dictionary<string, int> { { "actions", 1 } } };
        var action = new RulesetAction
        {
            CharacterId = "test-char",
            ActionType = RulesetActionType.Attack,
            Parameters = new Dictionary<string, string> { { "actionCost", "2" } }
        };

        var ok = resolver.TryConsumeActionSlot(state, action, out var error);

        Assert.False(ok);
        Assert.NotNull(error);
        Assert.Equal(1, state.ActionBudget["actions"]);
    }

    [Fact]
    public void TryConsumeActionSlot_Reaction_AlwaysSucceeds_DoesNotConsumeBudget()
    {
        var resolver = new Pf2eRulesetResolver(Substitute.For<IRollService>());
        var state = new CombatantState { CharacterId = "test-char", ActionBudget = new Dictionary<string, int> { { "actions", 0 } } };
        var action = new RulesetAction { CharacterId = "test-char", ActionType = RulesetActionType.Attack, IsReaction = true };

        var ok = resolver.TryConsumeActionSlot(state, action, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(0, state.ActionBudget["actions"]);
    }

    [Fact]
    public async Task Pf2eDefense_Level1CharacterWithDex2_IncludesLevelAndProficiency()
    {
        var character = new Character
        {
            Id = "test-char",
            Name = "Elara",
            SystemStats = new Pf2eExtension
            {
                Level = 1,
                DexterityMod = 2,
                ArmorClass = 10
            }
        };

        var step = new Pf2eDeriveDefenseStep();
        var context = new BootstrapContext
        {
            Character = character,
            ActiveSystem = RulesetSystem.Pathfinder2e,
            Session = null
        };

        var result = await step.ApplyAsync(context);

        var stats = Assert.IsType<Pf2eExtension>(character.SystemStats);
        // Expected: 10 + DEX 2 + Level 1 + Proficiency (Trained) 2 = 15
        // Current buggy: 10 + DEX 2 = 12
        Assert.Equal(15, stats.ArmorClass);
    }
}
