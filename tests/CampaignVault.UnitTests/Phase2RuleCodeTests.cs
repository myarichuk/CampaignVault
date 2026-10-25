using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Data.Templates;
using CampaignVault.Models;
using CampaignVault.Rulesets;
using CampaignVault.Rulesets.Bootstrap;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>Review remediation Phase 2: rule code fixes (B5, B9–B14, B17, B18, B22, B23, B28–B30, B32, B33, B38).</summary>
public class Phase2RuleCodeTests
{
    private static ChangeContext Ctx(IEnumerable<Character> characters, IEnumerable<Item>? items = null) =>
        new(
            sessionForTests: null,
            characters: characters.ToDictionary(c => c.Id),
            items: (items ?? []).ToDictionary(i => i.Id),
            locations: new Dictionary<string, Location>(),
            factions: new Dictionary<string, Faction>(),
            quests: new Dictionary<string, Quest>(),
            logger: NullLogger.Instance,
            summary: [],
            dispatcher: new WorldChangeDispatcher([], new CampaignDocumentKeys(), NullLogger<WorldChangeDispatcher>.Instance),
            campaignName: null);

    private static IRollService FixedRolls(int result)
    {
        var rolls = Substitute.For<IRollService>();
        rolls.RollAsync(Arg.Any<RollRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(new RollOutcome { Tag = call.Arg<RollRequest>().Tag, Result = result, Summary = $"Rolled {result}" }));
        return rolls;
    }

    private static RulesetAction Spell(string name) => new()
    {
        CharacterId = "chars/a", ActionType = RulesetActionType.Spell, ActionName = name, Parameters = new Dictionary<string, string>(),
    };

    // B5 / B11: spell data decides the action economy and the attack kind.

    [Fact]
    public void SpellDefaults_Pf2eActionCost_IsStampedUnlessTheCallerSaidOtherwise()
    {
        var spell = new SpellDefinition { Name = "heal", System = "pf2e", ActionCost = 2, CastKind = "action" };
        var action = Spell("Heal");
        SpellActionDefaults.Apply(action, spell, RulesetSystem.Pathfinder2e);
        Assert.Equal("2", action.Parameters["actionCost"]);

        var explicitCost = Spell("Heal");
        explicitCost.Parameters["actionCost"] = "3";
        SpellActionDefaults.Apply(explicitCost, spell, RulesetSystem.Pathfinder2e);
        Assert.Equal("3", explicitCost.Parameters["actionCost"]);
    }

    [Fact]
    public void SpellDefaults_Dnd5eBonusAndReaction_UseTheRightBudget()
    {
        var bonus = Spell("Healing Word");
        SpellActionDefaults.Apply(bonus, new SpellDefinition { Name = "healing_word", System = "dnd5e", CastKind = "bonus" }, RulesetSystem.Dnd5e);
        Assert.Equal("true", bonus.Parameters["bonusAction"]);
        Assert.False(bonus.IsReaction);

        var reaction = Spell("Shield");
        SpellActionDefaults.Apply(reaction, new SpellDefinition { Name = "shield", System = "dnd5e", CastKind = "reaction" }, RulesetSystem.Dnd5e);
        Assert.True(reaction.IsReaction);
        Assert.False(reaction.Parameters.ContainsKey("bonusAction"));
    }

    [Fact]
    public void SpellDefaults_TouchSpell_IsAMeleeAttackForConditions()
    {
        var grasp = Spell("Shocking Grasp");
        SpellActionDefaults.Apply(grasp, new SpellDefinition { Name = "shocking_grasp", System = "dnd5e", AttackType = "melee" }, RulesetSystem.Dnd5e);
        Assert.Equal("melee", RulesetResolverBase<Dnd5eExtension>.AttackRangeTag(grasp));

        var bolt = Spell("Fire Bolt");
        SpellActionDefaults.Apply(bolt, new SpellDefinition { Name = "fire_bolt", System = "dnd5e", AttackType = "ranged" }, RulesetSystem.Dnd5e);
        Assert.Equal("ranged", RulesetResolverBase<Dnd5eExtension>.AttackRangeTag(bolt));
        Assert.Equal("ranged", RulesetResolverBase<Dnd5eExtension>.AttackRangeTag(Spell("Unknown")));
    }

    [Fact]
    public void SpellDefaults_LongCastingTime_IsFlaggedForCombat()
    {
        Assert.True(SpellActionDefaults.TooLongForCombat(new SpellDefinition { Name = "identify", System = "dnd5e", CastKind = "long" }));
        Assert.False(SpellActionDefaults.TooLongForCombat(new SpellDefinition { Name = "fire_bolt", System = "dnd5e", CastKind = "action" }));
    }

    // B9 / B10: PF2e weapon math.

    private static RulesetAction Strike(string tags) => new()
    {
        CharacterId = "chars/a", TargetIds = ["chars/t"], ActionType = RulesetActionType.Attack, ActionName = "Strike",
        Parameters = new Dictionary<string, string> { ["weaponTags"] = tags, ["damageDice"] = "1d6" },
    };

    [Fact]
    public void Pf2eAttack_Untrained_AddsNoLevel()
    {
        var stats = new Pf2eExtension { Level = 5, StrengthMod = 3, Attributes = { ["weaponProficiencyRank"] = 0 } };
        var derived = DerivedAttack.Pf2e(new Character { Id = "chars/a" }, stats, Strike("melee,simple"), explicitBonus: false, explicitDamageBonus: false);
        Assert.Equal(3, derived.ToHit);
        Assert.Contains("untrained +0", derived.Note);

        var trained = new Pf2eExtension { Level = 5, StrengthMod = 3 };
        Assert.Equal(3 + 5 + 2, DerivedAttack.Pf2e(new Character { Id = "chars/a" }, trained, Strike("melee,simple"), false, false).ToHit);
    }

    [Fact]
    public void Pf2eAttack_ThrownAddsStrength_PropulsiveAddsHalf_BowAddsNone()
    {
        var stats = new Pf2eExtension { Level = 1, StrengthMod = 4, DexterityMod = 2 };
        var actor = new Character { Id = "chars/a" };
        Assert.Equal(4, DerivedAttack.Pf2e(actor, stats, Strike("ranged,simple,thrown"), false, false).Damage);
        Assert.Equal(2, DerivedAttack.Pf2e(actor, stats, Strike("propulsive,ranged,simple"), false, false).Damage);
        Assert.Equal(0, DerivedAttack.Pf2e(actor, stats, Strike("ranged,martial"), false, false).Damage);
    }

    // B13: PF2e shields count only while raised.

    private static Item Shield(int ac = 2) => new()
    {
        Id = "items/shield", Name = "Steel Shield", HolderId = "chars/a", IsEquipped = true, Tags = ["shield"],
        EquipZones = [EquipZones.OffHand], EquipLayer = EquipLayers.Held,
        Properties = new Dictionary<string, object> { ["acBonus"] = ac.ToString() },
    };

    [Fact]
    public void Pf2eStaticAc_IgnoresTheShield_Dnd5eCountsIt()
    {
        var pf2e = new Character { Id = "chars/a", Name = "A", SystemStats = new Pf2eExtension { Level = 1, DexterityMod = 1 } };
        ArmorParameterResolver.Apply(pf2e, [Shield()]);
        Assert.Equal(10 + 1 + 3, ((Pf2eExtension)pf2e.SystemStats!).ArmorClass);

        var dnd = new Character { Id = "chars/b", Name = "B", SystemStats = new Dnd5eExtension { Dexterity = 12 } };
        ArmorParameterResolver.Apply(dnd, [Shield()]);
        Assert.Equal(10 + 1 + 2, ((Dnd5eExtension)dnd.SystemStats!).ArmorClass);
    }

    [Fact]
    public async Task Pf2eRaiseAShield_AddsAcUntilNextTurn()
    {
        var actor = new Character { Id = "chars/a", Name = "Ysa", SystemStats = new Pf2eExtension { Level = 1 } };
        var context = Ctx([actor], [Shield()]);
        var resolver = new Pf2eRulesetResolver(FixedRolls(10));
        var action = new RulesetAction { CharacterId = "chars/a", ActionType = RulesetActionType.SkillCheck, ActionName = "Raise a Shield" };

        var output = await resolver.ResolveAsync(context, action, TestContext.Current.CancellationToken);

        Assert.True(output.Result.Success, output.Result.Narrative);
        var status = Assert.Single(output.Mutations.OfType<StatusChange>());
        Assert.Equal(Pf2eRulesetResolver.RaisedShieldEffectKey, status.Effect!.EffectKey);
        Assert.Equal(2f, status.Effect.StatModifiers["AC"]);
        Assert.True(status.Effect.ExpiresAtOwnTurnStart);
    }

    [Fact]
    public async Task Pf2eRaiseAShield_WithoutAShield_Fails()
    {
        var actor = new Character { Id = "chars/a", Name = "Ysa", SystemStats = new Pf2eExtension { Level = 1 } };
        var output = await new Pf2eRulesetResolver(FixedRolls(10)).ResolveAsync(
            Ctx([actor]), new RulesetAction { CharacterId = "chars/a", ActionType = RulesetActionType.SkillCheck, ActionName = "Raise Shield" }, TestContext.Current.CancellationToken);
        Assert.False(output.Result.Success);
        Assert.Contains("no shield", output.Result.Narrative);
    }

    // B14: armor fields that were never read.

    private static Item BodyArmor(string name, params (string Key, string Value)[] props)
    {
        var item = new Item
        {
            Id = "items/" + name, Name = name, HolderId = "chars/a", IsEquipped = true,
            EquipZones = [EquipZones.Torso], EquipLayer = EquipLayers.Armor, Properties = new Dictionary<string, object>(),
        };
        foreach (var (k, v) in props) item.Properties[k] = v;
        return item;
    }

    [Fact]
    public void Dnd5eHeavyArmor_StrengthMinimum_SlowsTheWearer_AndStealthDisadvantageIsACondition()
    {
        var weak = new Character { Id = "chars/a", Name = "A", SystemStats = new Dnd5eExtension { Strength = 11, Dexterity = 10 } };
        var chainMail = BodyArmor("chain mail", ("acBonus", "6"), ("armorType", "Heavy"), ("strMinimum", "13"), ("stealthDisadvantage", "true"));

        var messages = ArmorParameterResolver.Apply(weak, [chainMail]);

        Assert.Equal(-10f, weak.SystemStats!.MovementModifier);
        Assert.Contains(messages, m => m.Contains("below", StringComparison.Ordinal));
        var noisy = Assert.Single(weak.SystemStats.StatusEffects, e => e.EffectKey == ArmorParameterResolver.StealthPenaltyEffectKey);
        Assert.Equal(ArmorParameterResolver.NoisyArmorCondition, noisy.ConditionName);

        var strong = new Character { Id = "chars/b", Name = "B", SystemStats = new Dnd5eExtension { Strength = 14, Dexterity = 10 } };
        ArmorParameterResolver.Apply(strong, [chainMail]);
        Assert.Equal(0f, strong.SystemStats!.MovementModifier);

        // Taking the armor off takes the condition with it.
        ArmorParameterResolver.Apply(weak, []);
        Assert.DoesNotContain(weak.SystemStats.StatusEffects, e => e.EffectKey == ArmorParameterResolver.StealthPenaltyEffectKey);
    }

    [Fact]
    public void NoisyArmorCondition_GivesStealthDisadvantage()
    {
        var conditions = RulesetDataTestHelper.CreateConditionProvider();
        var noisy = conditions.GetConditionsForSystem(RulesetSystem.Dnd5e).Values.Single(c => c.Name == ArmorParameterResolver.NoisyArmorCondition);
        var effect = Assert.Single(noisy.Effects);
        Assert.Equal(ConditionEffectKinds.Disadvantage, effect.Kind);
        Assert.Equal("check", effect.On);
        Assert.Equal("stealth", effect.Subject);
    }

    [Fact]
    public void Pf2eArmor_CheckPenaltyUnlessStrong_SpeedPenaltyLighterWhenStrong()
    {
        var fullPlate = BodyArmor("full plate", ("acBonus", "6"), ("dexCap", "0"), ("checkPenalty", "-3"), ("speedModifier", "-10"), ("strength", "4"));

        var weak = new Character { Id = "chars/a", Name = "A", SystemStats = new Pf2eExtension { Level = 1, StrengthMod = 2 } };
        ArmorParameterResolver.Apply(weak, [fullPlate]);
        var penalty = Assert.Single(weak.SystemStats!.StatusEffects, e => e.EffectKey == ArmorParameterResolver.CheckPenaltyEffectKey);
        Assert.Equal(-3f, penalty.StatModifiers["Stealth"]);
        Assert.Equal(-3f, penalty.StatModifiers["Athletics"]);
        Assert.Equal(-10f, weak.SystemStats.MovementModifier);

        var strong = new Character { Id = "chars/b", Name = "B", SystemStats = new Pf2eExtension { Level = 1, StrengthMod = 4 } };
        ArmorParameterResolver.Apply(strong, [fullPlate]);
        Assert.DoesNotContain(strong.SystemStats!.StatusEffects, e => e.EffectKey == ArmorParameterResolver.CheckPenaltyEffectKey);
        Assert.Equal(-5f, strong.SystemStats.MovementModifier);
    }

    [Fact]
    public void VersatileWeapon_TwoHanded_UsesTheLargerDie()
    {
        var longsword = new Item
        {
            Id = "items/longsword", Name = "Longsword", Tags = ["martial", "melee", "versatile"],
            Properties = new Dictionary<string, object> { ["damageDice"] = "1d8", ["damageVersatile"] = "1d10" },
        };
        var oneHanded = new RulesetAction { CharacterId = "chars/a", ActionType = RulesetActionType.Attack, ActionName = "Longsword" };
        WeaponParameterResolver.ApplyWeaponItemProperties(oneHanded, longsword);
        Assert.Equal("1d8", oneHanded.Parameters["damageDice"]);

        var twoHanded = new RulesetAction
        {
            CharacterId = "chars/a", ActionType = RulesetActionType.Attack, ActionName = "Longsword",
            Parameters = new Dictionary<string, string> { ["twoHanded"] = "true" },
        };
        WeaponParameterResolver.ApplyWeaponItemProperties(twoHanded, longsword);
        Assert.Equal("1d10", twoHanded.Parameters["damageDice"]);
    }

    // B17: PF2e grapple degrees, Escape DC, MAP.

    private static (Character Actor, Character Target) Wrestlers()
    {
        var actor = new Character
        {
            Id = "chars/a", Name = "Brak", SystemStats = new Pf2eExtension { Level = 1, SkillModifiers = { ["Athletics"] = 7, ["Acrobatics"] = 1 } },
        };
        var target = new Character
        {
            Id = "chars/t", Name = "Guard", SystemStats = new Pf2eExtension { Level = 1, SavingThrowModifiers = { ["Fortitude"] = 5 }, SkillModifiers = { ["Athletics"] = 4, ["Acrobatics"] = 9 } },
        };
        return (actor, target);
    }

    private static RulesetAction Grapple(string name, params (string Key, string Value)[] parameters)
    {
        var action = new RulesetAction
        {
            CharacterId = "chars/a", TargetIds = ["chars/t"], ActionType = RulesetActionType.ContestedCheck, ActionName = name,
            ActionCategory = ActionCategory.Maneuver,
        };
        foreach (var (k, v) in parameters) action.Parameters[k] = v;
        return action;
    }

    [Fact]
    public async Task Pf2eGrapple_CriticalSuccess_Restrains()
    {
        var (actor, target) = Wrestlers();
        var output = await new Pf2eRulesetResolver(FixedRolls(30)).ResolveAsync(Ctx([actor, target]), Grapple("Grapple"), TestContext.Current.CancellationToken);

        Assert.True(output.Result.Success, output.Result.Narrative);
        var status = Assert.Single(output.Mutations.OfType<StatusChange>());
        Assert.Equal(EngagementMutationHelper.RestrainedCondition, status.Effect!.ConditionName);
        Assert.Contains("restrained", output.Result.Narrative);
    }

    [Fact]
    public async Task Pf2eGrapple_CriticalFailure_DropsTheHold()
    {
        var (actor, target) = Wrestlers();
        var output = await new Pf2eRulesetResolver(FixedRolls(2)).ResolveAsync(Ctx([actor, target]), Grapple("Grapple"), TestContext.Current.CancellationToken);

        Assert.True(output.Result.Success, output.Result.Narrative);
        Assert.Contains(output.Mutations, m => m is StatusRemove { CharacterId: "chars/t" });
        Assert.Contains("Critical failure", output.Result.Narrative);
    }

    [Fact]
    public async Task Pf2eEscape_UsesTheGrapplersAthleticsDc_WhateverTheEscaperRolls()
    {
        var (actor, target) = Wrestlers();
        // The escaper (chars/a) uses Acrobatics; the DC is 10 + the grappler's (chars/t) Athletics 4 = 14, not their Acrobatics 9.
        var output = await new Pf2eRulesetResolver(FixedRolls(15)).ResolveAsync(
            Ctx([actor, target]), Grapple("Escape Grapple", ("skill", "Acrobatics"), ("maneuver", "escape")), TestContext.Current.CancellationToken);

        Assert.True(output.Result.Success, output.Result.Narrative);
        Assert.Contains("Escape DC 14", output.Result.Narrative);
    }

    [Fact]
    public async Task Pf2eGrapple_TakesTheMultipleAttackPenalty()
    {
        var (actor, target) = Wrestlers();
        var rolls = Substitute.For<IRollService>();
        RollRequest? seen = null;
        rolls.RollAsync(Arg.Any<RollRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            seen = call.Arg<RollRequest>();
            return Task.FromResult(new RollOutcome { Tag = seen.Tag, Result = 15, Summary = "" });
        });
        await new Pf2eRulesetResolver(rolls).ResolveAsync(Ctx([actor, target]), Grapple("Grapple", ("priorStrikes", "1")), TestContext.Current.CancellationToken);

        Assert.NotNull(seen);
        Assert.Equal(7 - 5, seen!.Bonus);
    }

    [Fact]
    public void Pf2eActionSlots_GrappleCountsAsAnAttackForMap()
    {
        var resolver = new Pf2eRulesetResolver(FixedRolls(10));
        var state = new CombatantState { CharacterId = "chars/a", ActionBudget = new Dictionary<string, int> { ["actions"] = 3 } };
        var grapple = Grapple("Grapple");
        Assert.True(resolver.TryConsumeActionSlot(state, grapple, out _));
        Assert.Equal("0", grapple.Parameters["priorStrikes"]);

        var strike = Strike("melee");
        Assert.True(resolver.TryConsumeActionSlot(state, strike, out _));
        Assert.Equal("1", strike.Parameters["priorStrikes"]);
    }

    // B18: the 5e defender picks the skill.

    [Fact]
    public async Task Dnd5eGrapple_DefenderMayUseAcrobatics()
    {
        var actor = new Character { Id = "chars/a", Name = "A", SystemStats = new Dnd5eExtension { Strength = 16, SkillModifiers = { ["Athletics"] = 5 } } };
        var target = new Character { Id = "chars/t", Name = "T", SystemStats = new Dnd5eExtension { Dexterity = 18, SkillModifiers = { ["Acrobatics"] = 6, ["Athletics"] = 0 } } };
        var output = await new Dnd5eRulesetResolver(FixedRolls(12)).ResolveAsync(
            Ctx([actor, target]), Grapple("Grapple", ("defenderSkill", "Acrobatics")), TestContext.Current.CancellationToken);

        Assert.True(output.Result.Success, output.Result.Narrative);
        Assert.Contains("(Acrobatics)", output.Result.Narrative);
    }

    // B22: a long rest is eight hours.

    [Fact]
    public async Task LongRest_DeclaredButShort_CountsAsAShortRest()
    {
        var character = new Character
        {
            Id = "chars/f", Name = "Brea", IsPc = true, CurrentLocationId = "loc/1", MaxHp = 40, CurrentHp = 12,
            SystemStats = new Dnd5eExtension { Level = 4 },
        };
        var summary = new List<string>();
        var dispatcher = new WorldChangeDispatcher(
            [RulesetDataTestHelper.CreateStatusChangeHandler(), new ResourceChangeHandler()], new CampaignDocumentKeys(), NullLogger<WorldChangeDispatcher>.Instance);
        var handler = new RestChangeHandler(new EncounterResolver(() => 1.0), RulesetDataTestHelper.CreateConditionProvider());
        var context = ChangeContextTestHelper.Create(
            characters: new Dictionary<string, Character> { [character.Id] = character },
            locations: new Dictionary<string, Location> { ["loc/1"] = new Location { Id = "loc/1", Type = LocationType.Settlement } },
            logger: NullLogger.Instance, summary: summary, dispatcher: dispatcher);

        var result = await handler.ApplyAsync(
            new RestChange { CharacterId = character.Id, LocationId = "loc/1", IntendedHours = 4, RestType = RestType.LongRest }, context, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Message);
        Assert.Equal(RestType.ShortRest, character.LastRestType);
        Assert.Equal(12, character.CurrentHp);
        Assert.Contains(summary, m => m.Contains("counts as a short rest", StringComparison.Ordinal));
    }

    // B23: the paired slot must hold the spell.

    [Fact]
    public void SlotPairing_RejectsALowerSlot_AcceptsAHigherOne()
    {
        var caster = new Character
        {
            Id = "chars/w", Name = "Wren",
            SystemStats = new Dnd5eExtension
            {
                ResourcePools = new Dictionary<string, ResourcePool>
                {
                    ["spell_slots_1"] = new() { Current = 2, Max = 2 }, ["spell_slots_2"] = new() { Current = 1, Max = 1 }, ["spell_slots_3"] = new() { Current = 1, Max = 1 },
                },
            },
        };
        var spell = new SpellDefinition { Name = "Scorching Ray", System = "dnd5e", Level = 2 };
        var action = Spell("Scorching Ray");
        action.CharacterId = "chars/w";

        List<WorldChange> Batch(string pool) => [new ResourceChange { CharacterId = "chars/w", PoolName = pool, Delta = -1 }];

        Assert.Contains("level 2 or higher", SpellBookkeeping.CheckSlotPairing(action, caster, spell, Batch("spell_slots_1")));
        Assert.Null(SpellBookkeeping.CheckSlotPairing(action, caster, spell, Batch("spell_slots_2")));
        Assert.Null(SpellBookkeeping.CheckSlotPairing(action, caster, spell, Batch("spell_slots_3")));
    }

    // B28: Legendary at 19.

    [Fact]
    public async Task Pf2eSpellcastingProficiency_LegendaryAtNineteen()
    {
        var character = new Character { Id = "chars/c", Name = "C", ClassLevel = "Wizard 19", SystemStats = new Pf2eExtension { Level = 19, IntelligenceMod = 5 } };
        await new Pf2eDeriveProficiencyStep().ApplyAsync(new BootstrapContext { Character = character, ActiveSystem = RulesetSystem.Pathfinder2e }, TestContext.Current.CancellationToken);
        Assert.Equal(Pf2eProficiencyRank.Legendary, ((Pf2eExtension)character.SystemStats!).SpellcastingProficiency);
    }

    // B29 / B30: range goes through the pipeline; a hostile in reach must be able to act.

    [Fact]
    public void RangeRollModifier_ReadsWhatTheHandlerStamped()
    {
        var action = Strike("ranged");
        Assert.Equal(RangeRollModifier.None, RangeRollModifier.Read(action));

        action.Parameters[RangeRollModifier.DisadvantageParameter] = "t is Far";
        var read = RangeRollModifier.Read(action);
        Assert.True(read.Disadvantage);
        Assert.Equal(0, read.Penalty);

        var pf2e = Strike("ranged");
        pf2e.Parameters[RangeRollModifier.PenaltyParameter] = "-4";
        Assert.Equal(-4, RangeRollModifier.Read(pf2e).Penalty);
        Assert.False(RangeRollModifier.Read(pf2e).Disadvantage);
    }

    [Fact]
    public void RangeDisadvantage_AndAConditionAdvantage_LeaveAStraightRoll()
    {
        // The pipeline treats the stamped range disadvantage as one more source: advantage from elsewhere cancels it, never wins.
        var pipeline = RollModifierPipeline.BuiltIn;
        var actor = new Character { Id = "chars/a", SystemStats = new Dnd5eExtension() };
        var query = new RollQuery(RollKinds.Attack, null, [], actor, null, RulesetSystem.Dnd5e, new Dictionary<string, string>());
        var resolved = pipeline.Resolve(query, 5, AdvantageEffect.Advantage, alsoDisadvantage: true);
        Assert.Equal(AdvantageEffect.None, resolved.Advantage);
    }

    private static Character Pc5e(string id, bool pc) => new() { Id = id, Name = id, IsPc = pc, CurrentHp = 10, MaxHp = 10, SystemStats = new Dnd5eExtension() };

    [Fact]
    public void RangedInMelee_IgnoresAnIncapacitatedHostile_AndCoversThrownWeapons()
    {
        var a = Pc5e("a", true);
        var foe = Pc5e("foe", false);
        var far = Pc5e("far", false);
        a.SystemStats!.SpatialPositions.Add(new SpatialPosition { TargetId = "foe", DistanceBand = SpatialDistanceBand.Touch });
        a.SystemStats.SpatialPositions.Add(new SpatialPosition { TargetId = "far", DistanceBand = SpatialDistanceBand.Near });

        RulesetAction Attack(string tags, string range)
        {
            var action = new RulesetAction { CharacterId = "a", TargetIds = ["far"], ActionType = RulesetActionType.Attack, ActionName = "Shot" };
            action.Parameters["weaponTags"] = tags;
            action.Parameters["range"] = range;
            return action;
        }

        Assert.NotNull(RangeValidationHelper.Evaluate(Attack("ranged", "Near"), Ctx([a, foe, far])).DisadvantageReason);
        Assert.NotNull(RangeValidationHelper.Evaluate(Attack("finesse,light,thrown", "Near"), Ctx([a, foe, far])).DisadvantageReason);
        // A dagger stabbed at Touch is a melee attack.
        Assert.Null(RangeValidationHelper.Evaluate(Attack("finesse,light,thrown", "Touch"), Ctx([a, foe, far])).DisadvantageReason);

        foe.SystemStats!.StatusEffects.Add(new StatusEffect { Name = "Paralyzed", ConditionName = "paralyzed", Category = "Condition" });
        Assert.Null(RangeValidationHelper.Evaluate(Attack("ranged", "Near"), Ctx([a, foe, far])).DisadvantageReason);
    }

    [Fact]
    public void LongRange_ReportsHowManyBandsBeyond()
    {
        var a = Pc5e("a", true);
        var t = Pc5e("t", false);
        a.SystemStats!.SpatialPositions.Add(new SpatialPosition { TargetId = "t", DistanceBand = SpatialDistanceBand.Far });
        var action = new RulesetAction { CharacterId = "a", TargetIds = ["t"], ActionType = RulesetActionType.Attack, ActionName = "Shot" };
        action.Parameters["range"] = SpatialDistanceBand.Close;
        action.Parameters["longRange"] = SpatialDistanceBand.Far;

        var outcome = RangeValidationHelper.Evaluate(action, Ctx([a, t]));
        Assert.Null(outcome.Error);
        Assert.Equal(2, outcome.BandsBeyond);
    }

    // B32 / B33: sneak attack weapons and the save fallback.

    [Fact]
    public async Task Pf2eSneakAttack_NeedsAnAgileFinesseOrRangedWeapon()
    {
        var rogue = new Character
        {
            Id = "chars/a", Name = "Rogue", ClassLevel = "Rogue 1",
            SystemStats = new Pf2eExtension { Level = 1, StrengthMod = 2, DexterityMod = 4 },
        };
        var target = new Character { Id = "chars/t", Name = "T", SystemStats = new Pf2eExtension { ArmorClass = 10 } };
        var resolver = new Pf2eRulesetResolver(FixedRolls(15));

        var heavy = Strike("melee,martial,two-hand");
        heavy.Parameters["sneakAttack"] = "true";
        var heavyOut = await resolver.ResolveAsync(Ctx([rogue, target]), heavy, TestContext.Current.CancellationToken);
        Assert.DoesNotContain("Sneak Attack +", heavyOut.Result.Narrative);

        var dagger = Strike("agile,finesse,melee,simple");
        dagger.Parameters["sneakAttack"] = "true";
        var daggerOut = await resolver.ResolveAsync(Ctx([rogue, target]), dagger, TestContext.Current.CancellationToken);
        Assert.Contains("Sneak Attack +", daggerOut.Result.Narrative);
    }

    [Fact]
    public async Task Pf2eSave_WithoutARecordedModifier_CountsAsTrained()
    {
        var actor = new Character { Id = "chars/a", Name = "A", SystemStats = new Pf2eExtension { Level = 3, DexterityMod = 2 } };
        var rolls = Substitute.For<IRollService>();
        RollRequest? seen = null;
        rolls.RollAsync(Arg.Any<RollRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            seen = call.Arg<RollRequest>();
            return Task.FromResult(new RollOutcome { Tag = seen.Tag, Result = 15, Summary = "" });
        });
        var action = new RulesetAction
        {
            CharacterId = "chars/a", ActionType = RulesetActionType.SavingThrow, ActionName = "Reflex Save",
            Parameters = new Dictionary<string, string> { ["dc"] = "15", ["save"] = "Reflex" },
        };
        await new Pf2eRulesetResolver(rolls).ResolveAsync(Ctx([actor]), action, TestContext.Current.CancellationToken);
        Assert.Equal(2 + 3 + 2, seen!.Bonus);
    }

    // B38: stabilize without a roll.

    [Fact]
    public async Task DeathSave_Stabilize_SetsStableAndResetsTheTally()
    {
        var pc = new Character { Id = "chars/m", Name = "Maeve", MaxHp = 20, CurrentHp = 0, IsPc = true, SystemStats = new Dnd5eExtension(), DeathSaves = new DeathSaveTally { Failures = 2, Successes = 1 } };
        var context = Ctx([pc]);
        var result = await new DeathSaveChangeHandler(Substitute.For<IRollService>())
            .ApplyAsync(new DeathSaveChange { CharacterId = pc.Id, Stabilize = true }, context, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Message);
        Assert.True(pc.DeathSaves!.Stable);
        Assert.Equal(0, pc.DeathSaves.Failures);
        Assert.Equal(0, pc.DeathSaves.Successes);
        Assert.Equal(0, pc.CurrentHp);
    }
}
