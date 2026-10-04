using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;
using CampaignVault.Rulesets;
using CampaignVault.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>Remediation plan phase 2 (M-10 to M-15): one damage pipeline.</summary>
public class MechanicsPhase2Tests
{
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

    private static ActiveFeatEffect Effect(string feat, string kind, string? damageType = null, int value = 0) =>
        new(feat, new FeatEffect { Kind = kind, DamageType = damageType, Value = value });

    private static int Mitigate(
        int amount, string? type, SystemExtension stats, IReadOnlyList<ActiveFeatEffect>? effects = null,
        DamageOrdering ordering = DamageOrdering.Dnd5e, RulesetAction? action = null) =>
        DamageResolver.Resolve(amount, type, stats, effects ?? [], action, ordering);

    // ---- M-10: one resolver, once ----

    [Fact]
    public void Resistance_FromStatBlockAndFeat_HalvesOnce()
    {
        var stats = new Dnd5eExtension { DamageModifiers = new() { ["Fire"] = 0.5f } };
        var rage = new List<ActiveFeatEffect> { Effect("Rage", FeatEffectKinds.Resistance, "Fire") };

        Assert.Equal(5, Mitigate(10, "Fire", stats, rage));
    }

    [Fact]
    public void Resistance_FromFeatAlone_Halves_RoundingDown()
    {
        var rage = new List<ActiveFeatEffect> { Effect("Rage", FeatEffectKinds.Resistance, "Slashing") };

        Assert.Equal(3, Mitigate(7, "Slashing", new Dnd5eExtension(), rage));
        Assert.Equal(7, Mitigate(7, "Fire", new Dnd5eExtension(), rage));
    }

    [Fact]
    public void FlatReduction_ComesBeforeResistance_InTheSrdOrder()
    {
        var stats = new Dnd5eExtension
        {
            DamageModifiers = new() { ["Fire"] = 0.5f },
            DamageResistances = new() { ["Fire"] = 3 },
        };

        Assert.Equal(3, Mitigate(10, "Fire", stats)); // (10 - 3) / 2, not 10 / 2 - 3
    }

    [Fact]
    public void Immunity_WinsOverEverything()
    {
        var stats = new Dnd5eExtension { DamageModifiers = new() { ["Poison"] = 0f } };

        Assert.Equal(0, Mitigate(30, "Poison", stats, [Effect("Rage", FeatEffectKinds.Resistance, "Poison")]));
    }

    [Fact]
    public void ResistanceThenVulnerability_BothApply()
    {
        var stats = new Dnd5eExtension { DamageModifiers = new() { ["Fire"] = 2f } };
        var resist = new List<ActiveFeatEffect> { Effect("Ring", FeatEffectKinds.Resistance, "Fire") };

        Assert.Equal(10, Mitigate(10, "Fire", stats, resist)); // halved, then doubled
        Assert.Equal(20, Mitigate(10, "Fire", stats));
    }

    [Fact]
    public void TypeLookups_IgnoreCase()
    {
        var stats = new Dnd5eExtension
        {
            DamageModifiers = new() { ["fire"] = 0.5f },
            DamageResistances = new() { ["COLD"] = 2 },
        };

        Assert.Equal(5, Mitigate(10, "Fire", stats));
        Assert.Equal(8, Mitigate(10, "cold", stats));
    }

    [Fact]
    public void UntypedDamage_ReadsThePhysicalEntry()
    {
        var stats = new Dnd5eExtension { DamageResistances = new() { ["Physical"] = 2 } };

        Assert.Equal(6, Mitigate(8, null, stats));
    }

    [Fact]
    public void FeatFlatReduction_AppliesToThePrimaryPacketOnly()
    {
        var action = new RulesetAction { CharacterId = "a", ActionType = RulesetActionType.Attack, ActionName = "Axe" };
        var armor = new List<ActiveFeatEffect> { Effect("Armor Master", FeatEffectKinds.DamageReduction, value: 3) };

        var result = DamageResolver.Resolve(
            [new DamagePacket(10, "Slashing", Primary: true), new DamagePacket(4, "Fire")],
            new Dnd5eExtension(), armor, action, DamageOrdering.Dnd5e);

        Assert.Equal([7, 4], result.PerPacket);
        Assert.Equal(11, result.Total);
    }

    [Fact]
    public void Pf2e_WeaknessAdds_ResistanceTakesOff_ImmunityZeroes()
    {
        var stats = new Pf2eExtension
        {
            DamageResistances = new() { ["Fire"] = -5, ["Cold"] = 5 },
            DamageModifiers = new() { ["Poison"] = 0f },
        };

        Assert.Equal(15, Mitigate(10, "Fire", stats, ordering: DamageOrdering.Pf2e));
        Assert.Equal(5, Mitigate(10, "Cold", stats, ordering: DamageOrdering.Pf2e));
        Assert.Equal(0, Mitigate(3, "Cold", stats, ordering: DamageOrdering.Pf2e));
        Assert.Equal(0, Mitigate(10, "Poison", stats, ordering: DamageOrdering.Pf2e));
    }

    // ---- M-10/M-11: every resolver path uses it ----

    private static RulesetAction SaveSpell(string damageType, params string[] targets) => new()
    {
        CharacterId = "chars/caster",
        TargetIds = [.. targets],
        ActionType = RulesetActionType.Spell,
        ActionName = "Fireball",
        DamageType = damageType,
        Parameters = new Dictionary<string, string> { ["dc"] = "15", ["save"] = "Dexterity", ["damageDice"] = "8d6" },
    };

    private static Character Creature(string id, Dnd5eExtension? stats = null) =>
        new() { Id = id, Name = id, SystemStats = stats ?? new Dnd5eExtension() };

    private static RollOutcome Roll(int result) => new() { Result = result, Summary = $"Rolled {result}" };

    [Fact]
    public async Task Dnd5eSaveSpell_FireResistantTarget_TakesHalf_AndHalfAgainOnSave()
    {
        var rolls = new FakeRollService();
        rolls.NextRolls.Enqueue(Roll(5));  // failed save
        rolls.NextRolls.Enqueue(Roll(28)); // fireball damage
        var resolver = new Dnd5eRulesetResolver(rolls);
        var caster = Creature("chars/caster");
        var goblin = Creature("chars/goblin", new Dnd5eExtension { DamageModifiers = new() { ["Fire"] = 0.5f } });

        var output = await resolver.ResolveAsync(Context(caster, goblin), SaveSpell("Fire", goblin.Id), TestContext.Current.CancellationToken);

        Assert.Equal(-14, Assert.Single(output.Mutations.OfType<HpChange>()).Delta);

        rolls.NextRolls.Enqueue(Roll(20)); // saved
        rolls.NextRolls.Enqueue(Roll(28));
        output = await resolver.ResolveAsync(Context(caster, goblin), SaveSpell("Fire", goblin.Id), TestContext.Current.CancellationToken);

        Assert.Equal(-7, Assert.Single(output.Mutations.OfType<HpChange>()).Delta); // half on save, then resisted
    }

    [Fact]
    public async Task Dnd5eSaveSpell_FeatResistance_AppliesOnTheSavePath()
    {
        var rolls = new FakeRollService();
        rolls.NextRolls.Enqueue(Roll(5));
        rolls.NextRolls.Enqueue(Roll(20));
        var resolver = new Dnd5eRulesetResolver(rolls);
        var caster = Creature("chars/caster");
        var barbarian = Creature("chars/grog");
        var action = SaveSpell("Fire", barbarian.Id);
        action.FeatEffects[barbarian.Id] = [Effect("Fire Ward", FeatEffectKinds.Resistance, "Fire")];

        var output = await resolver.ResolveAsync(Context(caster, barbarian), action, TestContext.Current.CancellationToken);

        Assert.Equal(-10, Assert.Single(output.Mutations.OfType<HpChange>()).Delta);
    }

    // ---- M-12: one damage roll per cast ----

    [Fact]
    public async Task Dnd5eAreaSave_RollsDamageOnce_ForEveryTarget()
    {
        var rolls = new FakeRollService();
        rolls.NextRolls.Enqueue(Roll(5));  // goblin 1 fails
        rolls.NextRolls.Enqueue(Roll(24)); // the cast's damage, rolled once
        rolls.NextRolls.Enqueue(Roll(5));  // goblin 2 fails
        rolls.NextRolls.Enqueue(Roll(20)); // goblin 3 saves
        var resolver = new Dnd5eRulesetResolver(rolls);
        var caster = Creature("chars/caster");
        var targets = new[] { Creature("chars/g1"), Creature("chars/g2"), Creature("chars/g3") };

        var output = await resolver.ResolveAsync(
            Context([caster, .. targets]), SaveSpell("Fire", targets.Select(t => t.Id).ToArray()), TestContext.Current.CancellationToken);

        Assert.Single(rolls.RecordedRequests, r => r.Tag == "spell-damage");
        Assert.Equal([-24, -24, -12], output.Mutations.OfType<HpChange>().Select(m => m.Delta));
    }

    [Fact]
    public async Task Pf2eAreaSave_RollsDamageOnce_AndScalesByDegree()
    {
        var rolls = new FakeRollService();
        rolls.NextRolls.Enqueue(Roll(10));  // t1 fails
        rolls.NextRolls.Enqueue(Roll(12));  // damage
        rolls.NextRolls.Enqueue(Roll(5));   // t2 critically fails
        rolls.NextRolls.Enqueue(Roll(15));  // t3 succeeds
        var resolver = new Pf2eRulesetResolver(rolls);
        Character Pc(string id) => new() { Id = id, Name = id, SystemStats = new Pf2eExtension() };
        var caster = new Character { Id = "chars/caster", Name = "caster", SystemStats = new Pf2eExtension() };
        var action = SaveSpell("Fire", "chars/t1", "chars/t2", "chars/t3");
        action.Parameters["save"] = "Reflex";

        var output = await resolver.ResolveAsync(
            Context(caster, Pc("chars/t1"), Pc("chars/t2"), Pc("chars/t3")), action, TestContext.Current.CancellationToken);

        Assert.Single(rolls.RecordedRequests, r => r.Tag == "spell-damage");
        Assert.Equal([-12, -24, -6], output.Mutations.OfType<HpChange>().Select(m => m.Delta));
    }

    [Fact]
    public async Task Pf2eSave_ResistanceAndWeakness_ApplyAfterTheDegree()
    {
        var rolls = new FakeRollService();
        rolls.NextRolls.Enqueue(Roll(10)); // fails
        rolls.NextRolls.Enqueue(Roll(12));
        rolls.NextRolls.Enqueue(Roll(10)); // fails
        var resolver = new Pf2eRulesetResolver(rolls);
        var caster = new Character { Id = "chars/caster", Name = "caster", SystemStats = new Pf2eExtension() };
        var tough = new Character { Id = "chars/tough", Name = "tough", SystemStats = new Pf2eExtension { DamageResistances = new() { ["Fire"] = 5 } } };
        var frail = new Character { Id = "chars/frail", Name = "frail", SystemStats = new Pf2eExtension { DamageResistances = new() { ["Fire"] = -5 } } };
        var action = SaveSpell("Fire", tough.Id, frail.Id);
        action.Parameters["save"] = "Reflex";

        var output = await resolver.ResolveAsync(Context(caster, tough, frail), action, TestContext.Current.CancellationToken);

        Assert.Equal([-7, -17], output.Mutations.OfType<HpChange>().Select(m => m.Delta));
    }

    [Fact]
    public async Task Dnd5ePoolSave_ResistanceTouchesOnlyItsOwnPool()
    {
        var rolls = new FakeRollService();
        var resolver = new Dnd5eRulesetResolver(rolls, spellDefinitionProvider: SpellProvider());
        var caster = new Character { Id = "chars/caster", Name = "caster", SystemStats = new Dnd5eExtension { Level = 7 } };
        var yeti = Creature("chars/yeti", new Dnd5eExtension { DamageModifiers = new() { ["cold"] = 0f } });
        var action = new RulesetAction
        {
            CharacterId = caster.Id,
            TargetIds = [yeti.Id],
            ActionType = RulesetActionType.Spell,
            ActionName = "Ice Storm",
            Parameters = new Dictionary<string, string> { ["dc"] = "15", ["save"] = "Dexterity", ["damageDice"] = "2d8+4d6" },
        };
        rolls.NextRolls.Enqueue(Roll(5));  // failed save
        rolls.NextRolls.Enqueue(Roll(7));  // bludgeoning pool
        rolls.NextRolls.Enqueue(Roll(12)); // cold pool

        var output = await resolver.ResolveAsync(Context(caster, yeti), action, TestContext.Current.CancellationToken);

        Assert.Equal(-7, Assert.Single(output.Mutations.OfType<HpChange>()).Delta); // immune to the cold pool only
    }

    [Fact]
    public async Task Dnd5ePoolSave_Saved_StillHalvesTheSum_NotEachPool()
    {
        var rolls = new FakeRollService();
        var resolver = new Dnd5eRulesetResolver(rolls, spellDefinitionProvider: SpellProvider());
        var caster = new Character { Id = "chars/caster", Name = "caster", SystemStats = new Dnd5eExtension { Level = 7 } };
        var goblin = Creature("chars/goblin");
        var action = new RulesetAction
        {
            CharacterId = caster.Id,
            TargetIds = [goblin.Id],
            ActionType = RulesetActionType.Spell,
            ActionName = "Ice Storm",
            Parameters = new Dictionary<string, string> { ["dc"] = "15", ["save"] = "Dexterity", ["damageDice"] = "2d8+4d6" },
        };
        rolls.NextRolls.Enqueue(Roll(18)); // saved
        rolls.NextRolls.Enqueue(Roll(7));
        rolls.NextRolls.Enqueue(Roll(7));

        var output = await resolver.ResolveAsync(Context(caster, goblin), action, TestContext.Current.CancellationToken);

        Assert.Equal(-7, Assert.Single(output.Mutations.OfType<HpChange>()).Delta); // floor(14 / 2), not 3 + 3
    }

    private static CampaignVault.Services.SpellDefinitionProvider SpellProvider() =>
        new(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cv_phase2_" + Guid.NewGuid()), typeof(CampaignVault.Services.SpellDefinitionProvider).Assembly);

    // ---- M-14: evasion ----

    [Fact]
    public async Task Evasion_OnADexteritySave_NoneOnSuccess_HalfOnFailure()
    {
        var rolls = new FakeRollService();
        var resolver = new Dnd5eRulesetResolver(rolls);
        var caster = Creature("chars/caster");
        var rogue = Creature("chars/rogue");
        var action = SaveSpell("Fire", rogue.Id);
        action.FeatEffects[rogue.Id] = [Effect("Evasion", FeatEffectKinds.Evasion)];

        rolls.NextRolls.Enqueue(Roll(20)); // saves
        rolls.NextRolls.Enqueue(Roll(28));
        var saved = await resolver.ResolveAsync(Context(caster, rogue), action, TestContext.Current.CancellationToken);
        Assert.Empty(saved.Mutations.OfType<HpChange>());

        rolls.NextRolls.Enqueue(Roll(5)); // fails
        rolls.NextRolls.Enqueue(Roll(28));
        var failed = await resolver.ResolveAsync(Context(caster, rogue), action, TestContext.Current.CancellationToken);
        Assert.Equal(-14, Assert.Single(failed.Mutations.OfType<HpChange>()).Delta);
    }

    [Fact]
    public async Task Evasion_DoesNothing_OnANonDexteritySave()
    {
        var rolls = new FakeRollService();
        rolls.NextRolls.Enqueue(Roll(5));
        rolls.NextRolls.Enqueue(Roll(28));
        var resolver = new Dnd5eRulesetResolver(rolls);
        var rogue = Creature("chars/rogue");
        var action = SaveSpell("Necrotic", rogue.Id);
        action.Parameters["save"] = "Constitution";
        action.FeatEffects[rogue.Id] = [Effect("Evasion", FeatEffectKinds.Evasion)];

        var output = await resolver.ResolveAsync(Context(Creature("chars/caster"), rogue), action, TestContext.Current.CancellationToken);

        Assert.Equal(-28, Assert.Single(output.Mutations.OfType<HpChange>()).Delta);
    }

    private static readonly ProgressionDefinitionProvider Progressions =
        new(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cv_phase2_prog_" + Guid.NewGuid()), typeof(ProgressionDefinitionProvider).Assembly);

    [Theory]
    [InlineData("Rogue 7", true)]
    [InlineData("Rogue 6", false)]
    [InlineData("Monk 7", true)]
    [InlineData("Monk 6", false)]
    [InlineData("Fighter 7", false)]
    public void ClassProgressionData_GivesEvasionAtLevelSeven(string classLevel, bool expected)
    {
        var character = new Character { Id = "chars/x", Name = "x", ClassLevel = classLevel, SystemStats = new Dnd5eExtension { Level = int.Parse(classLevel.Split(' ')[1]) } };

        var effects = CharacterClassFeatures.Effects(character, RulesetSystem.Dnd5e, Progressions);

        Assert.Equal(expected, effects.Any(e => e.Effect.Kind == FeatEffectKinds.Evasion));
    }

    [Fact]
    public async Task Evasion_FromRealRogueData_NoDamageOnASuccessfulDexteritySave()
    {
        var rolls = new FakeRollService();
        rolls.NextRolls.Enqueue(Roll(20));
        rolls.NextRolls.Enqueue(Roll(28));
        var resolver = new Dnd5eRulesetResolver(rolls);
        var rogue = new Character { Id = "chars/rogue", Name = "rogue", ClassLevel = "Rogue 7", SystemStats = new Dnd5eExtension { Level = 7 } };
        var action = SaveSpell("Fire", rogue.Id);
        action.FeatEffects[rogue.Id] = [.. CharacterClassFeatures.Effects(rogue, RulesetSystem.Dnd5e, Progressions)];

        var output = await resolver.ResolveAsync(Context(Creature("chars/caster"), rogue), action, TestContext.Current.CancellationToken);

        Assert.Empty(output.Mutations.OfType<HpChange>());
    }

    [Fact]
    public void Evasion_IsAValidValuelessEffectKind()
    {
        Assert.Empty(FeatEffectRules.Validate([new FeatEffect { Kind = "evasion" }]));
    }

    // ---- M-13: one hit is one HpChange ----

    [Fact]
    public async Task CriticalHit_FlagsTheHpChange()
    {
        var rolls = new FakeRollService();
        rolls.NextRolls.Enqueue(new RollOutcome { Result = 25, HasCritical = true, Summary = "nat 20" });
        rolls.NextRolls.Enqueue(Roll(6)); // damage
        rolls.NextRolls.Enqueue(Roll(5)); // crit dice
        var resolver = new Dnd5eRulesetResolver(rolls);
        var actor = Creature("chars/valen");
        var target = Creature("chars/merc", new Dnd5eExtension { ArmorClass = 12 });
        var action = new RulesetAction
        {
            CharacterId = actor.Id, TargetIds = [target.Id], ActionType = RulesetActionType.Attack, ActionName = "Shortbow",
            Parameters = new Dictionary<string, string> { ["damageDice"] = "1d6" },
        };

        var output = await resolver.ResolveAsync(Context(actor, target), action, TestContext.Current.CancellationToken);

        var hp = Assert.Single(output.Mutations.OfType<HpChange>());
        Assert.Equal(-11, hp.Delta);
        Assert.True(hp.Critical);
    }

    [Fact]
    public async Task OrdinaryHit_IsNotFlaggedCritical()
    {
        var rolls = new FakeRollService();
        rolls.NextRolls.Enqueue(Roll(15));
        rolls.NextRolls.Enqueue(Roll(4));
        var resolver = new Dnd5eRulesetResolver(rolls);
        var actor = Creature("chars/valen");
        var target = Creature("chars/merc", new Dnd5eExtension { ArmorClass = 12 });
        var action = new RulesetAction
        {
            CharacterId = actor.Id, TargetIds = [target.Id], ActionType = RulesetActionType.Attack, ActionName = "Shortbow",
            Parameters = new Dictionary<string, string> { ["damageDice"] = "1d6" },
        };

        var output = await resolver.ResolveAsync(Context(actor, target), action, TestContext.Current.CancellationToken);

        Assert.False(Assert.Single(output.Mutations.OfType<HpChange>()).Critical);
    }

    [Fact]
    public async Task RiderDamage_ResistedByItsOwnType_WithTheHit()
    {
        var rolls = new FakeRollService();
        rolls.NextRolls.Enqueue(Roll(15));
        rolls.NextRolls.Enqueue(Roll(4));
        rolls.NextRolls.Enqueue(Roll(6));
        var resolver = new Dnd5eRulesetResolver(rolls);
        var actor = Creature("chars/valen");
        var target = Creature("chars/merc", new Dnd5eExtension { ArmorClass = 12, DamageModifiers = new() { ["fire"] = 0.5f } });
        var action = new RulesetAction
        {
            CharacterId = actor.Id, TargetIds = [target.Id], ActionType = RulesetActionType.Attack, ActionName = "Shortbow",
            Parameters = new Dictionary<string, string> { ["damageDice"] = "1d6", ["riderDice"] = "1d6", ["riderType"] = "fire" },
        };

        var output = await resolver.ResolveAsync(Context(actor, target), action, TestContext.Current.CancellationToken);

        Assert.Equal(-7, Assert.Single(output.Mutations.OfType<HpChange>()).Delta); // 4 + 6/2
        Assert.Contains("Rider: +3 fire", output.Result.Narrative);
    }
}
