using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;
using CampaignVault.Rulesets;
using CampaignVault.Rulesets.Bootstrap;
using CampaignVault.Schema;
using CampaignVault.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>Remediation plan phase 1 (M-01 to M-08): the small independent fixes.</summary>
public class MechanicsPhase1Tests
{
    private sealed class Grants(AdvantageEffect effect) : IRollModifierProvider
    {
        public IEnumerable<RollModifier> Modifiers(RollQuery query) => [new RollModifier("test", 0, effect, "test")];
    }

    private static ChangeContext Context(params Character[] characters) => new(
        sessionForTests: null,
        characters: characters.ToDictionary(c => c.Id),
        items: new Dictionary<string, Item>(),
        locations: new Dictionary<string, Location>(),
        factions: new Dictionary<string, Faction>(),
        quests: new Dictionary<string, Quest>(),
        logger: NullLogger.Instance,
        summary: [],
        dispatcher: new WorldChangeDispatcher(new IWorldChangeHandler[0], new CampaignDocumentKeys(), NullLogger<WorldChangeDispatcher>.Instance),
        campaignName: null);

    private static RollModifierPipeline Pipeline(AdvantageEffect effect) =>
        new([new Grants(effect)], NullLogger<RollModifierPipeline>.Instance);

    // ---- M-01: the manual doesn't claim behavior the commits lack ----

    [Fact]
    public void Manual_SideEffectExample_IsDeclaredOnTheCommit()
    {
        var manual = typeof(DmHelpManual).GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .First(s => s.Contains("automatic side effects"));

        var example = Regex.Match(manual, @"automatic side effects.*?e\.g\. `(\w+)`").Groups[1].Value;
        var variant = CommitSchemaModel.Variants.Single(v => v.Discriminator == example);

        Assert.NotEmpty(variant.SideEffects);
    }

    [Fact]
    public void Manual_DoesNotSay_RestHeals()
    {
        var manuals = typeof(DmHelpManual).GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string)).Select(f => (string)f.GetRawConstantValue()!);
        Assert.DoesNotContain(manuals, m => m.Contains("`rest` already heals"));
        Assert.Empty(CommitSchemaModel.Variants.Single(v => v.Discriminator == "rest").SideEffects);
    }

    // ---- M-02: a crit doubles the dice, never a flat modifier ----

    [Fact]
    public async Task Crit_WithInlineModifier_RollsTheExtraDiceWithoutIt()
    {
        var rolls = new FakeRollService();
        rolls.NextRolls.Enqueue(new RollOutcome { Result = 20, HasCritical = true, Summary = "Nat 20" });
        rolls.NextRolls.Enqueue(new RollOutcome { Result = 8, Summary = "8" });
        rolls.NextRolls.Enqueue(new RollOutcome { Result = 5, Summary = "5" });
        var actor = new Character { Id = "char1", SystemStats = new Dnd5eExtension() };
        var target = new Character { Id = "char2", SystemStats = new Dnd5eExtension { ArmorClass = 25 } };

        var output = await new Dnd5eRulesetResolver(rolls).ResolveAsync(Context(actor, target), new RulesetAction
        {
            CharacterId = "char1",
            TargetIds = ["char2"],
            ActionType = RulesetActionType.Attack,
            ActionName = "Longsword",
            Parameters = new Dictionary<string, string> { ["damageDice"] = "1d8+3" },
        }, TestContext.Current.CancellationToken);

        Assert.Equal("1d8+3", rolls.RecordedRequests.Single(r => r.Tag == "damage").Expression);
        Assert.Equal("1d8", rolls.RecordedRequests.Single(r => r.Tag == "critDamage").Expression);
        Assert.Equal(-13, Assert.IsType<HpChange>(Assert.Single(output.Mutations)).Delta);
    }

    [Theory]
    [InlineData("1d8+3", "1d8")]
    [InlineData("2d6 - 1", "2d6")]
    [InlineData("1d8", "1d8")]
    public void DiceOnly_StripsTheTrailingFlatPart(string dice, string expected) =>
        Assert.Equal(expected, DerivedAttack.DiceOnly(dice));

    // ---- M-03: every PF2e d20 roll reads the fold's mechanic ----

    private static async Task<RollRequest> Pf2eRoll(RulesetAction action, Character actor, params Character[] others)
    {
        var rolls = new FakeRollService();
        for (var i = 0; i < 4; i++)
            rolls.NextRolls.Enqueue(new RollOutcome { Result = 12, Summary = "12" });
        var resolver = new Pf2eRulesetResolver(rolls, rollModifiers: Pipeline(AdvantageEffect.Disadvantage));
        await resolver.ResolveAsync(Context([actor, .. others]), action, TestContext.Current.CancellationToken);
        return rolls.RecordedRequests[0];
    }

    private static Character Pf2eHero(string id) => new() { Id = id, Name = id, SystemStats = new Pf2eExtension() };

    [Theory]
    [InlineData(RulesetActionType.SkillCheck, "Athletics", "skill")]
    [InlineData(RulesetActionType.ContestedCheck, "Grapple", "grapple")]
    [InlineData(RulesetActionType.ContestedCheck, "Escape", "escape")]
    public async Task Pf2e_PluginDisadvantage_ReachesTheRoll(RulesetActionType type, string name, string tag)
    {
        var parameters = new Dictionary<string, string> { ["dc"] = "15" };
        var action = new RulesetAction
        {
            CharacterId = "a",
            TargetIds = ["b"],
            ActionType = type,
            ActionName = name,
            Parameters = parameters,
        };
        var first = await Pf2eRoll(action, Pf2eHero("a"), Pf2eHero("b"));

        Assert.Equal(DiceMechanic.Disadvantage, first.Mechanic);
        _ = tag;
    }

    [Fact]
    public async Task Pf2e_PluginDisadvantage_ReachesTheSpellSave()
    {
        var first = await Pf2eRoll(new RulesetAction
        {
            CharacterId = "a",
            TargetIds = ["b"],
            ActionType = RulesetActionType.Spell,
            ActionName = "Fireball",
            Parameters = new Dictionary<string, string> { ["dc"] = "15", ["save"] = "Reflex" },
        }, Pf2eHero("a"), Pf2eHero("b"));

        Assert.Equal(DiceMechanic.Disadvantage, first.Mechanic);
    }

    // ---- M-04: free actions are free ----

    [Fact]
    public void Pf2e_FreeAction_CostsNothing()
    {
        var resolver = new Pf2eRulesetResolver(Substitute.For<IRollService>());
        var state = new CombatantState { CharacterId = "a", ActionBudget = new Dictionary<string, int> { ["actions"] = 3 } };
        var action = new RulesetAction
        {
            CharacterId = "a",
            ActionType = RulesetActionType.SkillCheck,
            ActionName = "Release",
            Parameters = new Dictionary<string, string> { ["actionCost"] = "0" },
        };

        Assert.True(resolver.TryConsumeActionSlot(state, action, out _));
        Assert.Equal(3, state.ActionBudget["actions"]);
    }

    [Fact]
    public void Pf2e_NegativeActionCost_IsRefused_AndThreeEmptiesTheBudget()
    {
        var resolver = new Pf2eRulesetResolver(Substitute.For<IRollService>());
        var state = new CombatantState { CharacterId = "a", ActionBudget = new Dictionary<string, int> { ["actions"] = 3 } };
        RulesetAction Cost(string c) => new()
        {
            CharacterId = "a",
            ActionType = RulesetActionType.SkillCheck,
            ActionName = "Sprint",
            Parameters = new Dictionary<string, string> { ["actionCost"] = c },
        };

        Assert.False(resolver.TryConsumeActionSlot(state, Cost("-1"), out var error));
        Assert.Contains("negative", error);
        Assert.True(resolver.TryConsumeActionSlot(state, Cost("3"), out _));
        Assert.Equal(0, state.ActionBudget["actions"]);
    }

    // ---- M-06: initiative takes advantage; a contested check takes the caller's bonus ----

    [Fact]
    public async Task Initiative_ProviderAdvantage_TakesEffect_In5eAndPf2e()
    {
        var rolls5e = new FakeRollService();
        rolls5e.NextRolls.Enqueue(new RollOutcome { Result = 10, Summary = "10" });
        await new Dnd5eRulesetResolver(rolls5e, rollModifiers: Pipeline(AdvantageEffect.Advantage))
            .RollInitiativeAsync(new Character { Id = "a", SystemStats = new Dnd5eExtension() }, TestContext.Current.CancellationToken);
        Assert.Equal(DiceMechanic.Advantage, rolls5e.RecordedRequests[0].Mechanic);

        var rolls2e = new FakeRollService();
        rolls2e.NextRolls.Enqueue(new RollOutcome { Result = 10, Summary = "10" });
        await new Pf2eRulesetResolver(rolls2e, rollModifiers: Pipeline(AdvantageEffect.Advantage))
            .RollInitiativeAsync(new Character { Id = "a", SystemStats = new Pf2eExtension() }, TestContext.Current.CancellationToken);
        Assert.Equal(DiceMechanic.Advantage, rolls2e.RecordedRequests[0].Mechanic);
    }

    [Fact]
    public async Task ContestedCheck_HonorsTheCallersBonus_InPlaceOfTheSheet()
    {
        var rolls = new FakeRollService();
        rolls.NextRolls.Enqueue(new RollOutcome { Result = 10, Summary = "10" });
        rolls.NextRolls.Enqueue(new RollOutcome { Result = 10, Summary = "10" });
        var actor = new Character { Id = "a", SystemStats = new Dnd5eExtension { Strength = 20 } };
        var target = new Character { Id = "b", SystemStats = new Dnd5eExtension() };

        await new Dnd5eRulesetResolver(rolls).ResolveAsync(Context(actor, target), new RulesetAction
        {
            CharacterId = "a",
            TargetIds = ["b"],
            ActionType = RulesetActionType.ContestedCheck,
            ActionName = "Shove",
            Parameters = new Dictionary<string, string> { ["skill"] = "Strength", ["bonus"] = "1" },
        }, TestContext.Current.CancellationToken);

        Assert.Equal(1, rolls.RecordedRequests[0].Bonus); // not +5 from the sheet
    }

    // ---- M-08: one definition of "is concentrating" ----

    [Theory]
    [InlineData("Concentration: Bless", null, null, true)]
    [InlineData("Concentration (Bless)", null, null, true)]
    [InlineData("Concentration", null, null, true)]
    [InlineData("Bless", "concentration", null, true)]
    [InlineData("Bless", null, "Concentration", true)]
    [InlineData("Lost Concentration", null, null, false)]
    [InlineData("Concentration Broken", null, null, false)]
    [InlineData("Frightened", null, "Condition", false)]
    public void IsConcentration(string name, string? conditionName, string? category, bool expected) =>
        Assert.Equal(expected, ConcentrationRules.IsConcentration(
            new StatusEffect { Name = name, ConditionName = conditionName, Category = category! }));
}

/// <summary>M-07: the level cap holds in the handler, not only in the menu.</summary>
[Collection("RavenDB")]
public class LevelCapTests(RavenDBFixture fixture) : IClassFixture<RavenDBFixture>
{
    [Theory]
    [InlineData(20, 1)]
    [InlineData(19, 2)]
    public async Task LevelUp_PastTheCap_IsRefused(int level, int gained)
    {
        using var session = fixture.Store.OpenAsyncSession();
        var keys = new CampaignDocumentKeys();
        var campaign = "level-cap-" + Guid.NewGuid().ToString("N")[..8];
        await session.StoreAsync(new CampaignConfig { Id = keys.Config(campaign), ActiveSystem = RulesetSystem.Dnd5e }, TestContext.Current.CancellationToken);
        var hero = new Character
        {
            Id = "chars/hero-" + campaign,
            Name = "Hero",
            IsPc = true,
            CampaignName = campaign,
            MaxHp = 100,
            CurrentHp = 100,
            ClassLevel = $"Fighter {level}",
            SystemStats = new Dnd5eExtension { Constitution = 14, HitDie = "d10", Level = level },
        };
        await session.StoreAsync(hero, TestContext.Current.CancellationToken);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        var ctx = new ChangeContext(session, new Dictionary<string, Character>(), new Dictionary<string, Item>(),
            new Dictionary<string, Location>(), new Dictionary<string, Faction>(), new Dictionary<string, Quest>(),
            NullLogger.Instance, [],
            new WorldChangeDispatcher([], keys, NullLogger<WorldChangeDispatcher>.Instance), null, null, campaign);
        ctx.RegisterNewCharacter(hero);

        var result = await RulesetDataTestHelper.CreateLevelUpHandler(keys)
            .ApplyAsync(new LevelUpChange { CharacterId = hero.Id, LevelsGained = gained }, ctx, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("level cap of 20", result.Message);
        Assert.Equal(level, ((Dnd5eExtension)hero.SystemStats).Level);
    }
}
