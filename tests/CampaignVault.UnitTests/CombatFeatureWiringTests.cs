using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
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

/// <summary>Sheet-derived attack math, SRD class features (Archery, Dueling, Sneak Attack, Extra Attack, Second Wind, Action Surge) and declarative feat effects.</summary>
[Collection("RavenDB")]
public class CombatFeatureWiringTests(RavenDBFixture fixture) : IClassFixture<RavenDBFixture>
{
    private static readonly Assembly Assembly = typeof(SpellDefinitionProvider).Assembly;
    private readonly CampaignDocumentKeys _keys = new();
    private readonly string _campaign = "feature-wiring-" + Guid.NewGuid().ToString("N")[..8];

    private static readonly ProgressionDefinitionProvider Progressions =
        new(Path.Combine(Path.GetTempPath(), "cv-wiring-progressions-" + Guid.NewGuid().ToString("N")), Assembly);

    private static FakeRollService Rolls(params int[] results)
    {
        var rolls = new FakeRollService();
        foreach (var r in results)
        {
            rolls.NextRolls.Enqueue(new RollOutcome { Result = r, Summary = r.ToString() });
        }

        return rolls;
    }

    private static Character Hank(Action<Dnd5eExtension>? tweak = null)
    {
        var stats = new Dnd5eExtension
        {
            Level = 7,
            Dexterity = 18,
            Strength = 14,
            Attributes = { ["proficiencyBonus"] = 3 },
        };
        stats.LevelUpChoices.Add(new LevelUpChoiceRecord { Level = 1, Key = "fightingStyle", Value = "archery" });
        tweak?.Invoke(stats);
        return new Character { Id = "hank", Name = "Hank", ClassLevel = "Fighter 4 / Scout Rogue 3", SystemStats = stats };
    }

    private static Character Pell(int ac = 12) => new() { Id = "pell", Name = "Pell", SystemStats = new Dnd5eExtension { ArmorClass = ac } };

    private static ChangeContext Context(params Character[] characters) => ContextWith([], characters);

    private static ChangeContext ContextWith(Item[] items, params Character[] characters) => new(
        sessionForTests: null,
        characters: characters.ToDictionary(c => c.Id),
        items: items.ToDictionary(i => i.Id),
        locations: new Dictionary<string, Location>(),
        factions: new Dictionary<string, Faction>(),
        quests: new Dictionary<string, Quest>(),
        logger: NullLogger.Instance,
        summary: [],
        dispatcher: new WorldChangeDispatcher([], new CampaignDocumentKeys(), NullLogger<WorldChangeDispatcher>.Instance),
        campaignName: null);

    private static RulesetAction Attack(string tags, params (string Key, string Value)[] extra)
    {
        var action = new RulesetAction
        {
            CharacterId = "hank",
            TargetIds = ["pell"],
            ActionType = RulesetActionType.Attack,
            ActionName = "Hunting crossbow",
            Parameters = new Dictionary<string, string> { ["damageDice"] = "1d10", ["weaponTags"] = tags },
        };
        foreach (var (k, v) in extra)
        {
            action.Parameters[k] = v;
        }

        return action;
    }

    private static async Task<(ResolverOutput Output, FakeRollService Rolls)> Run(
        Character actor, RulesetAction action, params int[] rolls)
    {
        var fake = Rolls(rolls);
        // What the action handler does: the class features and picked options (Archery, Dueling) are effects like a feat's.
        var features = CharacterClassFeatures.Effects(actor, RulesetSystem.Dnd5e, Progressions);
        action.FeatEffects[actor.Id] = [.. features, .. action.FeatEffects.GetValueOrDefault(actor.Id) ?? []];
        var output = await new Dnd5eRulesetResolver(fake).ResolveAsync(Context(actor, Pell()), action, TestContext.Current.CancellationToken);
        return (output, fake);
    }

    [Fact]
    public async Task RangedWeapon_DerivesDexProfAndArchery_ToHitAndDexOnDamage()
    {
        var (output, fake) = await Run(Hank(), Attack("ranged,two-handed"), 15, 6);

        Assert.Equal(9, fake.RecordedRequests[0].Bonus);   // DEX +4, prof +3, Archery +2
        Assert.Equal(4, fake.RecordedRequests[1].Bonus);   // DEX +4 on damage
        Assert.Contains("Derived", output.Result.Narrative);
        Assert.Contains("Archery +2", output.Result.Narrative);
    }

    [Fact]
    public async Task ExplicitBonus_ReplacesDerivedToHit_ButNotDamage()
    {
        var (_, fake) = await Run(Hank(s => s.LevelUpChoices.Clear()), Attack("ranged", ("bonus", "2")), 15, 6);

        Assert.Equal(2, fake.RecordedRequests[0].Bonus);
        Assert.Equal(4, fake.RecordedRequests[1].Bonus);
    }

    [Fact]
    public async Task FightingStyleEffects_AddToAStatedBonus()
    {
        // Archery is a roll effect from the progression, so it stacks like a feat's; a stated bonus is the rest.
        var (_, fake) = await Run(Hank(), Attack("ranged", ("bonus", "2")), 15, 6);

        Assert.Equal(4, fake.RecordedRequests[0].Bonus);
    }

    [Fact]
    public async Task ExplicitDamageBonus_ReplacesDerivedDamage()
    {
        var (_, fake) = await Run(Hank(), Attack("ranged", ("damageBonus", "1")), 15, 6);

        Assert.Equal(9, fake.RecordedRequests[0].Bonus);
        Assert.Equal(1, fake.RecordedRequests[1].Bonus);
    }

    [Fact]
    public async Task WeaponEnchantment_AddsToDerivedInsteadOfReplacingIt()
    {
        var (_, fake) = await Run(Hank(), Attack("ranged", ("itemToHitBonus", "1")), 15, 6);

        Assert.Equal(10, fake.RecordedRequests[0].Bonus);
    }

    private static ActiveFeatEffect Effect(string kind, int value, Action<FeatEffect>? tweak = null)
    {
        var effect = new FeatEffect { Kind = kind, Value = value };
        tweak?.Invoke(effect);
        return new ActiveFeatEffect("Long Shot", effect);
    }

    private static RulesetAction WithFeatEffects(RulesetAction action, params ActiveFeatEffect[] effects)
    {
        action.FeatEffects["hank"] = [.. effects];
        return action;
    }

    private static ActiveFeatEffect Named(string name, string kind, Action<FeatEffect> tweak) =>
        new(name, new FeatEffect { Kind = kind }.Also(tweak));

    [Fact]
    public async Task ExtraDamage_RollsOnAHit_WhenItsToggleIsSet()
    {
        var extra = Named("Divine Strike", FeatEffectKinds.ExtraDamage, e => { e.Dice = "1d8"; e.DamageType = "radiant"; e.Toggle = "divineStrike"; });

        var (off, _) = await Run(Hank(), WithFeatEffects(Attack("ranged"), extra), 15, 6, 5);
        Assert.Contains("Hit for 6 damage", off.Result.Narrative);

        var (on, fake) = await Run(Hank(), WithFeatEffects(Attack("ranged", ("divineStrike", "true")), extra), 15, 6, 5);
        Assert.Contains("Hit for 11 damage", on.Result.Narrative);
        Assert.Contains("Divine Strike +5 radiant", on.Result.Narrative);
        Assert.Equal("1d8", fake.RecordedRequests[2].Expression);
    }

    [Fact]
    public async Task ExtraDamage_NeedingAnAssertion_ReportsWhenNotClaimed()
    {
        var extra = Named("Colossus Slayer", FeatEffectKinds.ExtraDamage, e => { e.Dice = "1d8"; e.Assert = ["targetHurt"]; e.When = "the target is below its maximum"; });

        var (output, _) = await Run(Hank(), WithFeatEffects(Attack("ranged"), extra), 15, 6, 5);

        Assert.Contains("Hit for 6 damage", output.Result.Narrative);
        Assert.Contains("needs assert=targetHurt", output.Result.Narrative);
    }

    [Fact]
    public async Task CritRange_WidensTheCriticalHit()
    {
        var crit = Named("Improved Critical", FeatEffectKinds.CritRange, e => e.Value = 19);
        var fake = Rolls();
        fake.NextRolls.Enqueue(new RollOutcome { Result = 21, IndividualDice = [19], Summary = "19" });
        fake.NextRolls.Enqueue(new RollOutcome { Result = 6, Summary = "6" });
        fake.NextRolls.Enqueue(new RollOutcome { Result = 7, Summary = "7" });
        var action = WithFeatEffects(Attack("ranged"), crit);
        var output = await new Dnd5eRulesetResolver(fake).ResolveAsync(Context(Hank(), Pell()), action, TestContext.Current.CancellationToken);

        Assert.Contains("CRITICAL HIT", output.Result.Narrative);

        var miss = Rolls();
        miss.NextRolls.Enqueue(new RollOutcome { Result = 21, IndividualDice = [18], Summary = "18" });
        miss.NextRolls.Enqueue(new RollOutcome { Result = 6, Summary = "6" });
        var plain = await new Dnd5eRulesetResolver(miss).ResolveAsync(Context(Hank(), Pell()), WithFeatEffects(Attack("ranged"), crit), TestContext.Current.CancellationToken);
        Assert.DoesNotContain("CRITICAL HIT", plain.Result.Narrative);
    }

    [Fact]
    public async Task Resistance_HalvesDamageOfThatType()
    {
        var action = Attack("ranged");
        action.DamageType = "fire";
        action.FeatEffects["pell"] = [Named("Fire Ward", FeatEffectKinds.Resistance, e => e.DamageType = "fire")];

        var (output, _) = await Run(Hank(), action, 15, 6);

        Assert.Contains("Hit for 3 damage", output.Result.Narrative);   // 6 rolled, halved
    }

    [Fact]
    public async Task AdvantageEffect_RollsTheAttackWithAdvantage_AndDisadvantageCancelsIt()
    {
        var adv = Named("Reckless", FeatEffectKinds.Advantage, e => e.On = "attack");
        var dis = Named("Blinded Aim", FeatEffectKinds.Disadvantage, e => e.On = "attack");

        var (_, one) = await Run(Hank(), WithFeatEffects(Attack("ranged"), adv), 15, 6, 3);
        Assert.Equal(DiceMechanic.Advantage, one.RecordedRequests[0].Mechanic);

        var (note, both) = await Run(Hank(), WithFeatEffects(Attack("ranged"), adv, dis), 15, 6);
        Assert.Equal(DiceMechanic.Standard, both.RecordedRequests[0].Mechanic);
        Assert.Contains("Reckless: advantage", note.Result.Narrative);
    }

    [Fact]
    public void Validate_NewKinds_CheckTheirOwnFields()
    {
        Assert.Empty(FeatEffectRules.Validate([new FeatEffect { Kind = "extraDamage", Dice = "2d6", DamageType = "fire" }, new FeatEffect { Kind = "advantage", On = "save", Subject = "dexterity" }]));
        Assert.Contains(FeatEffectRules.Validate([new FeatEffect { Kind = "extraDamage", Dice = "lots" }]), m => m.Contains("dice like"));
        Assert.Contains(FeatEffectRules.Validate([new FeatEffect { Kind = "advantage" }]), m => m.Contains("needs on:"));
        Assert.Contains(FeatEffectRules.Validate([new FeatEffect { Kind = "critRange", Value = 12 }]), m => m.Contains("15 to 20"));
        Assert.Contains(FeatEffectRules.Validate([new FeatEffect { Kind = "resistance" }]), m => m.Contains("damageType"));
    }

    [Fact]
    public async Task FeatEffects_ToggleTradesToHitForDamage()
    {
        var action = WithFeatEffects(Attack("ranged", ("powerAttack", "true")),
            Effect(FeatEffectKinds.AttackBonus, -5, e => { e.Toggle = "powerAttack"; e.Weapon = ["ranged"]; }),
            Effect(FeatEffectKinds.DamageBonus, 10, e => { e.Toggle = "powerAttack"; e.Weapon = ["ranged"]; }));
        var (output, fake) = await Run(Hank(), action, 15, 6);

        Assert.Equal(4, fake.RecordedRequests[0].Bonus);
        Assert.Equal(14, fake.RecordedRequests[1].Bonus);
        Assert.Contains("Long Shot -5", output.Result.Narrative);
    }

    [Fact]
    public async Task FeatEffects_ToggleNotSet_DoesNotApply()
    {
        var action = WithFeatEffects(Attack("ranged"), Effect(FeatEffectKinds.AttackBonus, -5, e => e.Toggle = "powerAttack"));
        var (_, fake) = await Run(Hank(), action, 15, 6);

        Assert.Equal(9, fake.RecordedRequests[0].Bonus);
    }

    [Fact]
    public async Task FeatEffects_WeaponConditionMismatch_DoesNotApply()
    {
        var action = WithFeatEffects(Attack("melee,martial"), Effect(FeatEffectKinds.AttackBonus, 3, e => e.Weapon = ["ranged"]));
        var (_, fake) = await Run(Hank(), action, 15, 6);

        Assert.Equal(5, fake.RecordedRequests[0].Bonus);   // STR +2, prof +3; the ranged-only effect stays inert
    }

    [Fact]
    public async Task FeatEffects_AssertedCondition_AppliesOnlyWhenClaimed_AndReportsWhenNot()
    {
        var flank = Effect(FeatEffectKinds.AttackBonus, 2, e => { e.Assert = ["allyNear"]; e.When = "an ally is adjacent to the target"; });

        var (unclaimed, fakeA) = await Run(Hank(), WithFeatEffects(Attack("ranged"), flank), 15, 6);
        Assert.Equal(9, fakeA.RecordedRequests[0].Bonus);
        Assert.Contains("needs assert=allyNear", unclaimed.Result.Narrative);
        Assert.Contains("an ally is adjacent", unclaimed.Result.Narrative);

        var (_, fakeB) = await Run(Hank(), WithFeatEffects(Attack("ranged", ("assert", "allyNear")), flank), 15, 6);
        Assert.Equal(11, fakeB.RecordedRequests[0].Bonus);
    }

    [Fact]
    public async Task FeatEffects_StackOnTopOfAnExplicitBonus()
    {
        var action = WithFeatEffects(Attack("ranged", ("bonus", "2")), Effect(FeatEffectKinds.AttackBonus, 1));
        var (_, fake) = await Run(Hank(s => s.LevelUpChoices.Clear()), action, 15, 6);

        Assert.Equal(3, fake.RecordedRequests[0].Bonus);
    }

    [Fact]
    public async Task FeatEffects_SkillBonus_AppliesToNamedSkillOnly()
    {
        var hank = Hank();
        var stealth = new RulesetAction
        {
            CharacterId = "hank",
            ActionType = RulesetActionType.SkillCheck,
            ActionName = "Stealth",
            Parameters = new Dictionary<string, string> { ["dc"] = "10", ["skill"] = "Stealth" },
        };
        WithFeatEffects(stealth, Effect(FeatEffectKinds.SkillBonus, 2, e => e.Subject = "stealth"));
        var (_, withFeat) = await Run(hank, stealth, 12);

        var stealthElsewhere = new RulesetAction
        {
            CharacterId = "hank",
            ActionType = RulesetActionType.SkillCheck,
            ActionName = "Stealth",
            Parameters = new Dictionary<string, string> { ["dc"] = "10", ["skill"] = "Stealth" },
        };
        WithFeatEffects(stealthElsewhere, Effect(FeatEffectKinds.SkillBonus, 2, e => e.Subject = "athletics"));
        var (_, without) = await Run(hank, stealthElsewhere, 12);

        Assert.Equal(without.RecordedRequests[0].Bonus + 2, withFeat.RecordedRequests[0].Bonus);
    }

    [Fact]
    public void Pf2eBonusTypes_HighestBonusAndWorstPenaltyOfEachTypeApply()
    {
        var action = new RulesetAction { CharacterId = "hank", ActionType = RulesetActionType.Attack, ActionName = "x" };
        ActiveFeatEffect E(int v, string type) => Effect(FeatEffectKinds.AttackBonus, v, e => e.BonusType = type);

        var fold = FeatEffectRules.Fold(RollKinds.Attack, null, action,
            [E(1, "circumstance"), E(2, "circumstance"), E(1, "status"), E(-1, "circumstance"), E(-2, "circumstance"), E(1, "untyped"), E(1, "untyped")],
            RulesetSystem.Pathfinder2e);

        Assert.Equal(2 + 1 - 2 + 1 + 1, fold.Bonus);
    }

    [Fact]
    public void Dnd5e_IgnoresBonusTypes_AndSumsEverything()
    {
        var action = new RulesetAction { CharacterId = "hank", ActionType = RulesetActionType.Attack, ActionName = "x" };
        var fold = FeatEffectRules.Fold(RollKinds.Attack, null, action,
            [Effect(FeatEffectKinds.AttackBonus, 1, e => e.BonusType = "circumstance"), Effect(FeatEffectKinds.AttackBonus, 2, e => e.BonusType = "circumstance")],
            RulesetSystem.Dnd5e);

        Assert.Equal(3, fold.Bonus);
    }

    [Fact]
    public async Task PowerAttack_WithoutAFeatToggle_IsIgnoredWithNote()
    {
        var (output, fake) = await Run(Hank(), Attack("ranged", ("powerAttack", "true")), 15, 6);

        Assert.Equal(9, fake.RecordedRequests[0].Bonus);
        Assert.Contains("powerAttack ignored", output.Result.Narrative);
    }

    [Fact]
    public async Task Dueling_AddsTwoDamageToOneHandedMelee_UsingStrength()
    {
        var hank = Hank(s =>
        {
            s.LevelUpChoices.Clear();
            s.LevelUpChoices.Add(new LevelUpChoiceRecord { Level = 1, Key = "fightingStyle", Value = "dueling" });
            s.Strength = 16;
        });
        var (_, fake) = await Run(hank, Attack("melee,martial"), 15, 5);

        Assert.Equal(6, fake.RecordedRequests[0].Bonus);   // STR +3, prof +3
        Assert.Equal(5, fake.RecordedRequests[1].Bonus);   // STR +3, Dueling +2
    }

    [Fact]
    public async Task SneakAttack_WithAdvantage_AddsRogueDiceOnHit()
    {
        var hank = Hank(s => s.Level = 7);
        var action = Attack("finesse,melee", ("damageBonus", "0"));
        action.AdvantageState = AdvantageState.Advantage;
        var (output, fake) = await Run(hank, action, 15, 5, 7);

        Assert.Contains(fake.RecordedRequests, r => r.Tag == "sneakAttack" && r.Expression == "2d6");
        Assert.Contains("Sneak Attack +7", output.Result.Narrative);
        var hp = Assert.IsType<HpChange>(Assert.Single(output.Mutations));
        Assert.Equal(-12, hp.Delta);
    }

    [Fact]
    public async Task SneakAttack_WithoutAdvantageOrAlly_DoesNotFire()
    {
        var (output, fake) = await Run(Hank(), Attack("finesse,melee"), 15, 5);

        Assert.DoesNotContain(fake.RecordedRequests, r => r.Tag == "sneakAttack");
        Assert.DoesNotContain("Sneak Attack", output.Result.Narrative);
    }

    [Fact]
    public async Task SneakAttack_AllyAdjacent_FiresButNotWithDisadvantage()
    {
        var ally = Attack("finesse,melee", ("sneakAttack", "true"));
        var (withAlly, _) = await Run(Hank(), ally, 15, 5, 7);
        Assert.Contains("Sneak Attack +7", withAlly.Result.Narrative);

        var disadvantaged = Attack("finesse,melee", ("sneakAttack", "true"));
        disadvantaged.AdvantageState = AdvantageState.Disadvantage;
        var (blocked, _) = await Run(Hank(), disadvantaged, 19, 5);
        Assert.Contains("disadvantage", blocked.Result.Narrative);
    }

    [Fact]
    public async Task SneakAttack_RequiresFinesseOrRanged()
    {
        var (output, fake) = await Run(Hank(), Attack("melee,heavy", ("sneakAttack", "true")), 15, 5);

        Assert.DoesNotContain(fake.RecordedRequests, r => r.Tag == "sneakAttack");
        Assert.Contains("finesse or ranged", output.Result.Narrative);
    }

    [Fact]
    public void ExtraAttack_FighterFive_SecondAttackRidesOnTheSameAction()
    {
        var fighter = new Character { Id = "f", ClassLevel = "Fighter 5", SystemStats = new Dnd5eExtension { Level = 5 } };
        var resolver = new Dnd5eRulesetResolver(Rolls());
        var state = new CombatantState { CharacterId = "f", ActionBudget = new Dictionary<string, int>(resolver.GetTurnActionBudget(fighter)) };
        RulesetAction Swing() => new() { CharacterId = "f", ActionType = RulesetActionType.Attack, ActionName = "Sword" };

        Assert.True(resolver.TryConsumeActionSlot(state, Swing(), out _));
        Assert.True(resolver.TryConsumeActionSlot(state, Swing(), out _));
        Assert.False(resolver.TryConsumeActionSlot(state, Swing(), out var error));
        Assert.Contains("No action remaining", error);
    }

    [Fact]
    public void FighterFour_HasNoExtraAttack()
    {
        var fighter = new Character { Id = "f", ClassLevel = "Fighter 4", SystemStats = new Dnd5eExtension { Level = 4 } };
        var resolver = new Dnd5eRulesetResolver(Rolls());
        var state = new CombatantState { CharacterId = "f", ActionBudget = new Dictionary<string, int>(resolver.GetTurnActionBudget(fighter)) };
        RulesetAction Swing() => new() { CharacterId = "f", ActionType = RulesetActionType.Attack, ActionName = "Sword" };

        Assert.True(resolver.TryConsumeActionSlot(state, Swing(), out _));
        Assert.False(resolver.TryConsumeActionSlot(state, Swing(), out _));
    }

    [Fact]
    public void SpentAction_WithBonusLeft_ErrorNamesBonusActionParameter()
    {
        var resolver = new Dnd5eRulesetResolver(Rolls());
        var state = new CombatantState { CharacterId = "f", ActionBudget = new Dictionary<string, int> { ["action"] = 0, ["bonus"] = 1 } };

        Assert.False(resolver.TryConsumeActionSlot(state,
            new RulesetAction { CharacterId = "f", ActionType = RulesetActionType.SkillCheck, ActionName = "Hide" }, out var error));
        Assert.Contains("bonusAction", error);
    }

    [Fact]
    public async Task SecondWind_Fighter_SpendsPoolAndHeals()
    {
        var fighter = new Character
        {
            Id = "hank", Name = "Hank", ClassLevel = "Fighter 4",
            SystemStats = new Dnd5eExtension { Level = 4, ResourcePools = { ["second_wind"] = new ResourcePool { Current = 1, Max = 1 } } },
        };
        var fake = Rolls(9);
        var output = await new Dnd5eRulesetResolver(fake).ResolveAsync(Context(fighter), new RulesetAction
        {
            CharacterId = "hank", ActionType = RulesetActionType.Recovery, ActionName = "Second Wind",
        }, TestContext.Current.CancellationToken);

        Assert.True(output.Result.Success);
        Assert.Equal(4, fake.RecordedRequests[0].Bonus);
        Assert.Contains(output.Mutations, m => m is ResourceChange { PoolName: "second_wind", Delta: -1 });
        Assert.Contains(output.Mutations, m => m is HpChange { Delta: 9 });
    }

    [Fact]
    public async Task SecondWind_NoUsesLeft_Fails()
    {
        var fighter = new Character
        {
            Id = "hank", Name = "Hank", ClassLevel = "Fighter 4",
            SystemStats = new Dnd5eExtension { Level = 4, ResourcePools = { ["second_wind"] = new ResourcePool { Current = 0, Max = 1 } } },
        };
        var output = await new Dnd5eRulesetResolver(Rolls()).ResolveAsync(Context(fighter), new RulesetAction
        {
            CharacterId = "hank", ActionType = RulesetActionType.Recovery, ActionName = "Second Wind",
        }, TestContext.Current.CancellationToken);

        Assert.False(output.Result.Success);
        Assert.Equal("NoResource", output.Result.ErrorCode);
    }

    [Fact]
    public async Task Pf2e_DerivesAbilityLevelAndRank_AndAddsStrengthToMeleeDamage()
    {
        var actor = new Character
        {
            Id = "hank", Name = "Hank", ClassLevel = "Fighter 3",
            SystemStats = new Pf2eExtension { Level = 3, StrengthMod = 4 },
        };
        var target = new Character { Id = "pell", Name = "Pell", SystemStats = new Pf2eExtension { ArmorClass = 15 } };
        var fake = Rolls(16, 6);
        var output = await new Pf2eRulesetResolver(fake).ResolveAsync(Context(actor, target), new RulesetAction
        {
            CharacterId = "hank", TargetIds = ["pell"], ActionType = RulesetActionType.Attack, ActionName = "Longsword",
            Parameters = new Dictionary<string, string> { ["damageDice"] = "1d8", ["weaponTags"] = "melee,martial" },
        }, TestContext.Current.CancellationToken);

        Assert.Equal(9, fake.RecordedRequests[0].Bonus);   // STR +4, level 3 + Trained 2
        Assert.Equal(4, fake.RecordedRequests[1].Bonus);
        Assert.Contains("Trained", output.Result.Narrative);
    }

    [Fact]
    public async Task Pf2e_FeatEffects_ReachTheStrike_TypedAndVisibleInTheNarrative()
    {
        var actor = new Character
        {
            Id = "hank", Name = "Hank", ClassLevel = "Fighter 3",
            SystemStats = new Pf2eExtension { Level = 3, StrengthMod = 4 },
        };
        var target = new Character { Id = "pell", Name = "Pell", SystemStats = new Pf2eExtension { ArmorClass = 15 } };
        var action = new RulesetAction
        {
            CharacterId = "hank", TargetIds = ["pell"], ActionType = RulesetActionType.Attack, ActionName = "Longsword",
            Parameters = new Dictionary<string, string> { ["damageDice"] = "1d8", ["weaponTags"] = "melee,martial", ["assert"] = "allyNear" },
        };
        WithFeatEffects(action,
            Effect(FeatEffectKinds.AttackBonus, 1, e => { e.BonusType = "circumstance"; e.Assert = ["allyNear"]; e.When = "an ally flanks"; }),
            Effect(FeatEffectKinds.AttackBonus, 2, e => e.BonusType = "circumstance"),
            Effect(FeatEffectKinds.DamageBonus, 3, e => { e.Assert = ["highGround"]; e.When = "from higher ground"; }));
        var fake = Rolls(16, 6);
        var output = await new Pf2eRulesetResolver(fake).ResolveAsync(Context(actor, target), action, TestContext.Current.CancellationToken);

        Assert.Equal(11, fake.RecordedRequests[0].Bonus);   // 9 + best circumstance (+2, not +3)
        Assert.Equal(4, fake.RecordedRequests[1].Bonus);    // highGround not asserted
        Assert.Contains("Long Shot +2 (circumstance)", output.Result.Narrative);
        Assert.Contains("needs assert=highGround", output.Result.Narrative);
    }

    [Fact]
    public async Task Pf2e_ExplicitBonus_Wins()
    {
        var actor = new Character { Id = "hank", Name = "Hank", SystemStats = new Pf2eExtension { Level = 3, StrengthMod = 4 } };
        var target = new Character { Id = "pell", Name = "Pell", SystemStats = new Pf2eExtension { ArmorClass = 15 } };
        var fake = Rolls(16, 6);
        await new Pf2eRulesetResolver(fake).ResolveAsync(Context(actor, target), new RulesetAction
        {
            CharacterId = "hank", TargetIds = ["pell"], ActionType = RulesetActionType.Attack, ActionName = "Longsword",
            Parameters = new Dictionary<string, string> { ["damageDice"] = "1d8", ["bonus"] = "7" },
        }, TestContext.Current.CancellationToken);

        Assert.Equal(7, fake.RecordedRequests[0].Bonus);
    }

    [Fact]
    public void HeldWeapon_EnchantmentAndTags_ReachTheActionWithoutCountingAsExplicitBonus()
    {
        var weapon = new Item { Id = "items/xbow", Name = "Hunting crossbow", Tags = ["ranged", "two-handed"] };
        weapon.Properties["toHitBonus"] = "1";
        var action = new RulesetAction { CharacterId = "hank", ActionType = RulesetActionType.Attack, ActionName = "Shoot" };

        WeaponParameterResolver.ApplyWeaponItemProperties(action, weapon);

        Assert.False(action.Parameters.ContainsKey("bonus"));
        Assert.Equal("1", action.Parameters["itemToHitBonus"]);
        Assert.Equal("ranged,two-handed", action.Parameters["weaponTags"]);
    }

    private static RulesetAction SkillCheck(string skill, params (string Key, string Value)[] extra)
    {
        var action = new RulesetAction
        {
            CharacterId = "hank", ActionType = RulesetActionType.SkillCheck, ActionName = skill,
            Parameters = new Dictionary<string, string> { ["dc"] = "12" },
        };
        foreach (var (k, v) in extra)
        {
            action.Parameters[k] = v;
        }

        return action;
    }

    [Fact]
    public async Task Dnd5e_SkillWithoutSheetEntry_RollsGoverningAbility_NotZero()
    {
        var fake = Rolls(10);
        await new Dnd5eRulesetResolver(fake).ResolveAsync(Context(Hank()), SkillCheck("Stealth"), TestContext.Current.CancellationToken);

        Assert.Equal(4, fake.RecordedRequests[0].Bonus);   // DEX 18, untrained
    }

    [Fact]
    public async Task Dnd5e_SheetSkillModifier_StillWinsOverAbilityFallback()
    {
        var hank = Hank(s => s.SkillModifiers["Stealth"] = 7);
        var fake = Rolls(10);
        await new Dnd5eRulesetResolver(fake).ResolveAsync(Context(hank), SkillCheck("Stealth"), TestContext.Current.CancellationToken);

        Assert.Equal(7, fake.RecordedRequests[0].Bonus);
    }

    [Fact]
    public async Task Dnd5e_SkillCheck_CallerBonusReplacesSheetModifier_AndSaysSo()
    {
        var hank = Hank(s => s.SkillModifiers["Stealth"] = 7);
        var fake = Rolls(10);
        var output = await new Dnd5eRulesetResolver(fake).ResolveAsync(Context(hank), SkillCheck("Stealth", ("bonus", "2")), TestContext.Current.CancellationToken);

        Assert.Equal(2, fake.RecordedRequests[0].Bonus);
        Assert.Contains("bonus supplied by caller", output.Result.Narrative);
    }

    [Fact]
    public async Task Pf2e_UntrainedSkillAndPerception_RollKeyAbility()
    {
        var actor = new Character { Id = "hank", Name = "Hank", SystemStats = new Pf2eExtension { Level = 3, DexterityMod = 3, WisdomMod = 2 } };
        var fake = Rolls(10, 10);
        var resolver = new Pf2eRulesetResolver(fake);
        await resolver.ResolveAsync(Context(actor), SkillCheck("Stealth"), TestContext.Current.CancellationToken);
        await resolver.ResolveAsync(Context(actor), SkillCheck("Perception"), TestContext.Current.CancellationToken);

        Assert.Equal(3, fake.RecordedRequests[0].Bonus);
        Assert.Equal(2, fake.RecordedRequests[1].Bonus);
    }

    [Fact]
    public async Task Pf2e_SneakAttack_AddsRoguePrecisionDice()
    {
        var actor = new Character
        {
            Id = "hank", Name = "Hank", ClassLevel = "Rogue 5",
            SystemStats = new Pf2eExtension { Level = 5, DexterityMod = 4 },
        };
        var target = new Character { Id = "pell", Name = "Pell", SystemStats = new Pf2eExtension { ArmorClass = 15 } };
        var fake = Rolls(19, 5, 7);
        var output = await new Pf2eRulesetResolver(fake).ResolveAsync(Context(actor, target), new RulesetAction
        {
            CharacterId = "hank", TargetIds = ["pell"], ActionType = RulesetActionType.Attack, ActionName = "Shortbow",
            Parameters = new Dictionary<string, string> { ["damageDice"] = "1d6", ["weaponTags"] = "ranged", ["sneakAttack"] = "true" },
        }, TestContext.Current.CancellationToken);

        Assert.Contains(fake.RecordedRequests, r => r.Tag == "sneakAttack" && r.Expression == "2d6");
        Assert.Contains("Sneak Attack +7", output.Result.Narrative);
    }

    private static RulesetAction Strike(params string[] targets) => new()
    {
        CharacterId = "hank", TargetIds = [.. targets], ActionType = RulesetActionType.Attack, ActionName = "Longsword",
        Parameters = new Dictionary<string, string> { ["damageDice"] = "1d8" },
    };

    [Fact]
    public async Task Pf2e_Map_AccumulatesAcrossSeparateStrikes_InOneTurn()
    {
        var actor = new Character { Id = "hank", Name = "Hank", SystemStats = new Pf2eExtension { Level = 1, StrengthMod = 4 } };
        var target = new Character { Id = "pell", Name = "Pell", SystemStats = new Pf2eExtension { ArmorClass = 5 } };
        var fake = Rolls(2, 2, 2, 2, 2, 2);
        var resolver = new Pf2eRulesetResolver(fake);
        var state = new CombatantState { CharacterId = "hank", ActionBudget = new Dictionary<string, int> { ["actions"] = 3 } };

        for (var i = 0; i < 3; i++)
        {
            var action = Strike("pell");
            Assert.True(resolver.TryConsumeActionSlot(state, action, out _));
            await resolver.ResolveAsync(Context(actor, target), action, TestContext.Current.CancellationToken);
        }

        var bonuses = fake.RecordedRequests.Where(r => r.Tag == "attack").Select(r => r.Bonus).ToArray();
        Assert.Equal(3, bonuses.Length);
        Assert.Equal(bonuses[0] - 5, bonuses[1]);
        Assert.Equal(bonuses[0] - 10, bonuses[2]);
    }

    [Fact]
    public void Pf2e_Map_CallerCannotSpoofPriorStrikes_AndReactionsDontCount()
    {
        var resolver = new Pf2eRulesetResolver(Rolls());
        var state = new CombatantState { CharacterId = "hank", ActionBudget = new Dictionary<string, int> { ["actions"] = 3 } };

        var reaction = Strike("pell");
        reaction.IsReaction = true;
        Assert.True(resolver.TryConsumeActionSlot(state, reaction, out _));
        Assert.Equal(0, state.ActionBudget.GetValueOrDefault("strikesMade"));

        var first = Strike("pell");
        first.Parameters["priorStrikes"] = "2";
        Assert.True(resolver.TryConsumeActionSlot(state, first, out _));
        Assert.Equal("0", first.Parameters["priorStrikes"]);
        Assert.Equal(1, state.ActionBudget["strikesMade"]);
    }

    // ---- Action Surge (handler level) ----

    private RulesetActionHandler CreateHandler(IRollService rolls)
    {
        IRulesetModule[] modules =
        [
            new Dnd5eRulesetResolver(rolls),
            new Pf2eRulesetResolver(Substitute.For<IRollService>()),
            new NarrativeRulesetResolver(Substitute.For<IRollService>()),
        ];
        var dir = Path.Combine(Path.GetTempPath(), "cv_surge_test_" + Guid.NewGuid());
        return new RulesetActionHandler(
            new RulesetModuleSelector(modules), _keys,
            new SpellDefinitionProvider(dir, Assembly), new FeatDefinitionProvider(dir, Assembly));
    }

    private ChangeContext SurgeContext(Raven.Client.Documents.Session.IAsyncDocumentSession session, Character[] characters, CombatEncounter combat) => new(
        session,
        characters.ToDictionary(c => c.Id),
        new Dictionary<string, Item>(),
        new Dictionary<string, Location>(),
        new Dictionary<string, Faction>(),
        new Dictionary<string, Quest>(),
        NullLogger.Instance,
        [],
        new WorldChangeDispatcher([], new CampaignDocumentKeys(), NullLogger<WorldChangeDispatcher>.Instance),
        combat,
        null,
        _campaign);

    private static Character Surger(int surgeLeft) => new()
    {
        Id = "hank", Name = "Hank", ClassLevel = "Fighter 4", CampaignName = null,
        SystemStats = new Dnd5eExtension
        {
            Level = 4, Dexterity = 18,
            Attributes = { ["proficiencyBonus"] = 2 },
            ResourcePools = { ["action_surge"] = new ResourcePool { Current = surgeLeft, Max = 1 } },
        },
    };

    private static CombatEncounter SpentTurn() => new()
    {
        IsActive = true, Round = 1, ActiveTurnId = "hank",
        Combatants =
        [
            new CombatantState { CharacterId = "hank", ActionBudget = new Dictionary<string, int> { ["action"] = 0, ["bonus"] = 1 } },
            new CombatantState { CharacterId = "pell", ActionBudget = new Dictionary<string, int> { ["action"] = 1 } },
        ],
    };

    private async Task SeedConfig(Raven.Client.Documents.Session.IAsyncDocumentSession session)
    {
        await session.StoreAsync(new CampaignConfig { Id = _keys.Config(_campaign), ActiveSystem = RulesetSystem.Dnd5e }, TestContext.Current.CancellationToken);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static RulesetAction SurgeAttack(string target = "pell") => new()
    {
        CharacterId = "hank", TargetIds = [target], ActionType = RulesetActionType.Attack, ActionName = "Longsword",
        Parameters = new Dictionary<string, string> { ["actionSurge"] = "true", ["damageDice"] = "1d8", ["bonus"] = "5" },
    };

    [Fact]
    public async Task ActionSurge_WithSpentAction_GrantsOneAndSpendsThePool()
    {
        using var session = fixture.Store.OpenAsyncSession();
        await SeedConfig(session);
        var hank = Surger(1);
        var combat = SpentTurn();
        var context = SurgeContext(session, [hank, Pell()], combat);

        var result = await CreateHandler(Rolls(15, 5)).ApplyAsync(SurgeAttack(), context, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Message);
        Assert.Equal(0, ((Dnd5eExtension)hank.SystemStats!).ResourcePools["action_surge"].Current);
        var state = combat.Combatants.Single(c => c.CharacterId == "hank");
        Assert.Equal(0, state.ActionBudget["action"]);
        Assert.Equal(1, state.ActionBudget["actionSurgeUsed"]);
        Assert.Contains("Action Surge", result.Message);
    }

    [Fact]
    public async Task ActionSurge_WithNoUsesLeft_IsRefused()
    {
        using var session = fixture.Store.OpenAsyncSession();
        await SeedConfig(session);
        var context = SurgeContext(session, [Surger(0), Pell()], SpentTurn());

        var result = await CreateHandler(Rolls(15, 5)).ApplyAsync(SurgeAttack(), context, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("no Action Surge use left", result.Message);
    }

    [Fact]
    public async Task ActionSurge_WhenTheSurgedActionFails_RefundsPoolAndAction()
    {
        using var session = fixture.Store.OpenAsyncSession();
        await SeedConfig(session);
        var hank = Surger(1);
        var combat = SpentTurn();
        var context = SurgeContext(session, [hank, Pell()], combat);

        var result = await CreateHandler(Rolls()).ApplyAsync(SurgeAttack("nobody"), context, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(1, ((Dnd5eExtension)hank.SystemStats!).ResourcePools["action_surge"].Current);
        var state = combat.Combatants.Single(c => c.CharacterId == "hank");
        Assert.Equal(0, state.ActionBudget["action"]);
        Assert.False(state.ActionBudget.ContainsKey("actionSurgeUsed"));
    }

    [Fact]
    public async Task ActionSurge_TwiceInOneTurn_IsRefused()
    {
        using var session = fixture.Store.OpenAsyncSession();
        await SeedConfig(session);
        var hank = Surger(2);
        var combat = SpentTurn();
        var context = SurgeContext(session, [hank, Pell()], combat);
        var handler = CreateHandler(Rolls(15, 5, 15, 5));

        Assert.True((await handler.ApplyAsync(SurgeAttack(), context, TestContext.Current.CancellationToken)).Success);
        var second = await handler.ApplyAsync(SurgeAttack(), context, TestContext.Current.CancellationToken);

        Assert.False(second.Success);
        Assert.Contains("already used", second.Message);
    }

    // ---- off-hand, Cunning Action, Jack of All Trades, multi-target warnings ----

    [Fact]
    public async Task OffHand_DropsPositiveAbilityModFromDamage_UnlessTwoWeaponFighting()
    {
        var (output, plain) = await Run(Hank(), Attack("melee,finesse,light", ("offHand", "true")), 15, 4);
        Assert.Equal(0, plain.RecordedRequests[1].Bonus);
        Assert.Contains("off-hand", output.Result.Narrative);

        var twf = Hank(s => s.LevelUpChoices.Add(new LevelUpChoiceRecord { Level = 1, Key = "fightingStyle", Value = "twoWeaponFighting" }));
        var (_, styled) = await Run(twf, Attack("melee,finesse,light", ("offHand", "true")), 15, 4);
        Assert.Equal(4, styled.RecordedRequests[1].Bonus);
    }

    [Fact]
    public async Task OffHand_WithHeavyWeapon_NotesItNeedsLight()
    {
        var (output, _) = await Run(Hank(), Attack("melee,heavy", ("offHand", "true")), 15, 4);

        Assert.Contains("light melee weapon", output.Result.Narrative);
    }

    [Fact]
    public void OffHandAttack_CostsTheBonusAction_AfterTheAttackAction()
    {
        var state = new CombatantState { CharacterId = "hank", ActionBudget = new Dictionary<string, int> { ["action"] = 1, ["bonus"] = 1 } };
        var resolver = new Dnd5eRulesetResolver(Rolls());

        Assert.True(resolver.TryConsumeActionSlot(state, Attack("melee,light"), out _));
        Assert.True(resolver.TryConsumeActionSlot(state, Attack("melee,light", ("offHand", "true")), out _));
        Assert.Equal(0, state.ActionBudget["action"]);
        Assert.Equal(0, state.ActionBudget["bonus"]);
    }

    [Fact]
    public void OffHandAttack_BeforeAnyAttackAction_IsRefused_AndSpendsNothing()
    {
        var state = new CombatantState { CharacterId = "hank", ActionBudget = new Dictionary<string, int> { ["action"] = 1, ["bonus"] = 1 } };

        var ok = new Dnd5eRulesetResolver(Rolls()).TryConsumeActionSlot(state, Attack("melee,light", ("offHand", "true")), out var reason);

        Assert.False(ok);
        Assert.Contains("follows the Attack action", reason);
        Assert.Equal(1, state.ActionBudget["bonus"]);
    }

    private static Item Wielded(string id, string zone, params string[] tags) => new()
    {
        Id = id, Name = id, HolderId = "hank", CoreCategory = ItemCategories.Weapon,
        IsEquipped = true, EquipZones = [zone], Tags = [.. tags],
    };

    private static async Task<ResolverOutput> OffHandWith(params Item[] items) =>
        await new Dnd5eRulesetResolver(Rolls(15, 4)).ResolveAsync(
            ContextWith(items, Hank(), Pell()), Attack("melee,finesse,light", ("offHand", "true")), TestContext.Current.CancellationToken);

    [Fact]
    public async Task OffHand_WithALightWeaponInEachHand_Resolves()
    {
        var output = await OffHandWith(Wielded("dagger", EquipZones.MainHand, "light"), Wielded("shortsword", EquipZones.OffHand, "light"));

        Assert.True(output.Result.Success, output.Result.Narrative);
    }

    [Fact]
    public async Task OffHand_WithOnlyOneWeaponEquipped_IsRefused()
    {
        var output = await OffHandWith(Wielded("dagger", EquipZones.MainHand, "light"));

        Assert.False(output.Result.Success);
        Assert.Equal("InvalidAction", output.Result.ErrorCode);
        Assert.Contains("only 'dagger' is equipped", output.Result.Narrative);
    }

    [Fact]
    public async Task OffHand_WithANonLightWeaponWielded_IsRefused()
    {
        var output = await OffHandWith(Wielded("dagger", EquipZones.MainHand, "light"), Wielded("longsword", EquipZones.OffHand, "versatile"));

        Assert.False(output.Result.Success);
        Assert.Contains("'longsword' is not light", output.Result.Narrative);
    }

    private async Task<ResolverOutput> CunningAsync(Character actor, params (string Key, string Value)[] parameters)
    {
        var action = new RulesetAction { CharacterId = "hank", ActionType = RulesetActionType.Recovery, ActionName = "Cunning Action" };
        foreach (var (k, v) in parameters)
        {
            action.Parameters[k] = v;
        }

        return await new Dnd5eRulesetResolver(Rolls(14)).ResolveAsync(Context(actor), action, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CunningAction_Dash_WorksForARogue_AndCostsTheBonusAction()
    {
        var output = await CunningAsync(Hank(), ("option", "dash"));
        Assert.True(output.Result.Success, output.Result.Narrative);
        Assert.Contains("Dash", output.Result.Narrative);

        var state = new CombatantState { CharacterId = "hank", ActionBudget = new Dictionary<string, int> { ["action"] = 1, ["bonus"] = 1 } };
        Assert.True(new Dnd5eRulesetResolver(Rolls()).TryConsumeActionSlot(state,
            new RulesetAction { CharacterId = "hank", ActionType = RulesetActionType.Recovery, ActionName = "Cunning Action" }, out _));
        Assert.Equal(0, state.ActionBudget["bonus"]);
        Assert.Equal(1, state.ActionBudget["action"]);
    }

    [Fact]
    public async Task CunningAction_Hide_RollsStealthAgainstTheDc()
    {
        var output = await CunningAsync(Hank(), ("option", "hide"), ("dc", "12"));

        Assert.True(output.Result.Success, output.Result.Narrative);
        Assert.Contains("Cunning Action (Hide)", output.Result.Narrative);
        Assert.Contains("Stealth", output.Result.Narrative);
    }

    [Fact]
    public async Task CunningAction_NeedsRogueTwo_AnOption_AndADcForHide()
    {
        var fighter = new Character { Id = "hank", Name = "Hank", ClassLevel = "Fighter 5", SystemStats = new Dnd5eExtension { Level = 5 } };
        Assert.Equal("NoFeature", (await CunningAsync(fighter, ("option", "dash"))).Result.ErrorCode);
        Assert.Equal("InvalidParameter", (await CunningAsync(Hank())).Result.ErrorCode);
        Assert.Equal("InvalidParameter", (await CunningAsync(Hank(), ("option", "hide"))).Result.ErrorCode);
    }

    private static Character Bard(Action<Dnd5eExtension>? tweak = null)
    {
        var stats = new Dnd5eExtension { Level = 5, Charisma = 16, Strength = 10, Dexterity = 10, Attributes = { ["proficiencyBonus"] = 3 } };
        tweak?.Invoke(stats);
        return new Character { Id = "hank", Name = "Lyra", ClassLevel = "Bard 5", SystemStats = stats };
    }

    private static RulesetAction Check(string skill, params (string Key, string Value)[] extra)
    {
        var action = new RulesetAction
        {
            CharacterId = "hank", ActionType = RulesetActionType.SkillCheck, ActionName = skill,
            Parameters = new Dictionary<string, string> { ["dc"] = "10", ["skill"] = skill },
        };
        foreach (var (k, v) in extra)
        {
            action.Parameters[k] = v;
        }

        return action;
    }

    [Fact]
    public async Task JackOfAllTrades_AddsHalfProficiencyToUntrainedChecks_Only()
    {
        var (output, untrained) = await Run(Bard(), Check("Athletics"), 12);
        Assert.Equal(1, untrained.RecordedRequests[0].Bonus);   // STR +0, half of +3 rounded down
        Assert.Contains("Jack of All Trades +1", output.Result.Narrative);

        var trained = Bard(s => s.SkillModifiers["Athletics"] = 3);
        var (_, proficient) = await Run(trained, Check("Athletics"), 12);
        Assert.Equal(3, proficient.RecordedRequests[0].Bonus);
    }

    [Fact]
    public async Task JackOfAllTrades_NotForNonBards_OrBelowLevelTwo_OrWithCallerBonus()
    {
        var fighter = Bard();
        fighter.ClassLevel = "Fighter 5";
        Assert.Equal(0, (await Run(fighter, Check("Athletics"), 12)).Rolls.RecordedRequests[0].Bonus);

        var novice = Bard();
        novice.ClassLevel = "Bard 1";
        Assert.Equal(0, (await Run(novice, Check("Athletics"), 12)).Rolls.RecordedRequests[0].Bonus);

        Assert.Equal(4, (await Run(Bard(), Check("Athletics", ("bonus", "4")), 12)).Rolls.RecordedRequests[0].Bonus);
    }

    [Fact]
    public async Task JackOfAllTrades_AppliesToInitiative()
    {
        var fake = Rolls(10);
        await new Dnd5eRulesetResolver(fake).RollInitiativeAsync(Bard(), TestContext.Current.CancellationToken);

        Assert.Equal(1, fake.RecordedRequests[0].Bonus);
    }

    private static Character Extra(string id) => new() { Id = id, Name = id, SystemStats = new Dnd5eExtension { ArmorClass = 12 } };

    private async Task<ResolverOutput> MultiAsync(Character actor, RulesetAction action, params int[] rolls) =>
        await new Dnd5eRulesetResolver(Rolls(rolls)).ResolveAsync(Context(actor, Pell(), Extra("bo"), Extra("cy")), action, TestContext.Current.CancellationToken);

    [Fact]
    public async Task MultiTarget_ShortAttackCount_WarnsWhichTargetsWereSkipped()
    {
        var action = Attack("melee", ("attackCount", "1"));
        action.TargetIds = ["pell", "bo"];
        var output = await MultiAsync(Hank(), action, 15, 4);

        Assert.Contains("attackCount is 1", output.Result.Narrative);
        Assert.Contains("bo were not attacked", output.Result.Narrative);
    }

    [Fact]
    public async Task MultiTarget_MoreTargetsThanExtraAttackAllows_Warns()
    {
        var action = Attack("melee");
        action.TargetIds = ["pell", "bo", "cy"];
        var output = await MultiAsync(Hank(), action, 15, 4, 15, 4, 15, 4);

        Assert.Contains("makes 1 per Attack action", output.Result.Narrative);

        var fighter = Hank(s => s.Level = 11);
        fighter.ClassLevel = "Fighter 11";
        var ok = await MultiAsync(fighter, action, 15, 4, 15, 4, 15, 4);
        Assert.DoesNotContain("per Attack action", ok.Result.Narrative);
    }
}

internal static class FeatEffectTestExtensions
{
    public static FeatEffect Also(this FeatEffect effect, Action<FeatEffect> tweak)
    {
        tweak(effect);
        return effect;
    }
}
