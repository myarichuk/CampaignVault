using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Autofac;
using CampaignVault.AutofacModules;
using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Data.Templates;
using CampaignVault.Models;
using CampaignVault.Rulesets;
using CampaignVault.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>Conditions as data (M-20 to M-23): the provider reads each condition's effects and the pipeline applies them.</summary>
public class ConditionMechanicsTests
{
    private static readonly ConditionDefinitionProvider Conditions = new(
        Path.Combine(Path.GetTempPath(), "cv-conditions-" + Guid.NewGuid().ToString("N")),
        typeof(ConditionDefinitionProvider).Assembly);

    private static readonly RollModifierPipeline Pipeline = new([new ConditionMechanicsProvider(Conditions)]);

    private static Character With(string id, params string[] statuses)
    {
        var c = new Character { Id = id, Name = id, SystemStats = new Dnd5eExtension() };
        foreach (var s in statuses)
            c.SystemStats.StatusEffects.Add(new StatusEffect { Name = s, Category = "Condition" });
        return c;
    }

    private static RollResolution Roll(string kind, Character actor, Character? other = null, string? subject = null, params string[] tags) =>
        Pipeline.Resolve(new RollQuery(kind, subject, tags, actor, other, "dnd5e", new Dictionary<string, string>()), 0);

    [Fact]
    public void Prone_TargetGivesMeleeAdvantage_AndRangedDisadvantage()
    {
        var attacker = With("a");
        var target = With("t", "Prone");

        Assert.Equal(AdvantageEffect.Advantage, Roll(RollKinds.Attack, attacker, target, null, "melee").Advantage);
        Assert.Equal(AdvantageEffect.Disadvantage, Roll(RollKinds.Attack, attacker, target, null, "ranged").Advantage);
    }

    [Fact]
    public void Prone_AttackerHasDisadvantage_AndMeleeAdvantageCancelsIt()
    {
        var attacker = With("a", "Prone");
        Assert.Equal(AdvantageEffect.Disadvantage, Roll(RollKinds.Attack, attacker, With("t"), null, "melee").Advantage);
        Assert.Equal(AdvantageEffect.None, Roll(RollKinds.Attack, attacker, With("t", "Prone"), null, "melee").Advantage);
    }

    [Theory]
    [InlineData("Strength", true)]
    [InlineData("Dexterity", true)]
    [InlineData("dex", true)]
    [InlineData("Wisdom", false)]
    public void Paralyzed_AutoFailsStrengthAndDexteritySaves(string save, bool autoFails)
    {
        var r = Roll(RollKinds.Save, With("p", "Paralyzed"), null, save);

        Assert.Equal(autoFails, r.AutoFail);
        Assert.Equal(autoFails, r.Notes.Any(n => n.Contains("automatic failure", StringComparison.Ordinal)));
    }

    [Fact]
    public void Restrained_HasDisadvantageOnDexteritySavesOnly()
    {
        var hero = With("h", "Restrained");
        Assert.Equal(AdvantageEffect.Disadvantage, Roll(RollKinds.Save, hero, null, "Dexterity").Advantage);
        Assert.Equal(AdvantageEffect.None, Roll(RollKinds.Save, hero, null, "Strength").Advantage);
    }

    [Fact]
    public void Poisoned_DisadvantageOnAttacksAndChecks_NotSaves()
    {
        var hero = With("h", "Poisoned");
        Assert.Equal(AdvantageEffect.Disadvantage, Roll(RollKinds.Attack, hero).Advantage);
        Assert.Equal(AdvantageEffect.Disadvantage, Roll(RollKinds.Check, hero, null, "athletics").Advantage);
        Assert.Equal(AdvantageEffect.None, Roll(RollKinds.Save, hero, null, "Wisdom").Advantage);
    }

    [Fact]
    public void Frightened_NeedsTheSourceInSight()
    {
        var hero = With("h", "Frightened");
        Assert.Equal(AdvantageEffect.None, Roll(RollKinds.Attack, hero).Advantage);
        Assert.Equal(AdvantageEffect.Disadvantage, Roll(RollKinds.Attack, hero, null, null, "sourceInSight").Advantage);
    }

    [Fact]
    public void LegacyStatusWithNoConditionName_StillResolves_AndNotesAppear()
    {
        var r = Roll(RollKinds.Attack, With("h", "poisoned"));
        Assert.Equal(AdvantageEffect.Disadvantage, r.Advantage);
        Assert.Contains(r.Notes, n => n.Contains("Poisoned", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ConditionName_WinsOverADisplayName()
    {
        var c = new Character { Id = "h", Name = "h", SystemStats = new Dnd5eExtension() };
        c.SystemStats.StatusEffects.Add(new StatusEffect { Name = "Venom from the spider", Category = "Poison", ConditionName = "poisoned" });
        Assert.Equal(AdvantageEffect.Disadvantage, Roll(RollKinds.Attack, c).Advantage);
    }

    [Theory]
    [InlineData("Exhaustion 1", AdvantageEffect.Disadvantage, AdvantageEffect.None)]
    [InlineData("Exhaustion 3", AdvantageEffect.Disadvantage, AdvantageEffect.Disadvantage)]
    public void Exhaustion_AccumulatesByLevel(string status, AdvantageEffect check, AdvantageEffect attack)
    {
        var hero = With("h", status);
        Assert.Equal(check, Roll(RollKinds.Check, hero, null, "athletics").Advantage);
        Assert.Equal(attack, Roll(RollKinds.Attack, hero).Advantage);
    }

    [Fact]
    public void Exhaustion_ResolvesItsLevelFromTheName()
    {
        var defs = Conditions.GetConditionsForSystem("dnd5e");
        var status = new StatusEffect { Name = "Exhaustion 3" };

        Assert.True(ConditionMechanics.TryResolve(defs, status, out var def, out var level));
        Assert.Equal("exhaustion", def.Name);
        Assert.Equal(3, level);
    }

    [Fact]
    public void Unconscious_SuppressesIncapacitated_ButProneStaysItsOwnCondition()
    {
        // SRD 5.1: an unconscious creature falls prone, and prone keeps its own effect (disadvantage on ranged attacks
        // against it, which cancels unconscious's advantage into a straight roll). Only incapacitated is subsumed.
        var active = ConditionMechanics.Active(With("h", "Unconscious", "Prone", "Incapacitated"), Conditions, "dnd5e");
        Assert.Equal(["unconscious", "prone"], active.Select(a => a.Definition.Name));
    }

    [Fact]
    public void AutoCrit_AppliesToMeleeOnParalyzedAndUnconscious_NotRanged()
    {
        var provider = new ConditionMechanicsProvider(Conditions);
        Assert.True(provider.AutoCrit(With("t", "Paralyzed"), "dnd5e", ["melee"]));
        Assert.True(provider.AutoCrit(With("t", "Unconscious"), "dnd5e", ["melee"]));
        Assert.False(provider.AutoCrit(With("t", "Paralyzed"), "dnd5e", ["ranged"]));
        Assert.False(provider.AutoCrit(With("t", "Prone"), "dnd5e", ["melee"]));
    }

    [Fact]
    public void ProviderIsWiredIntoThePipelineTheContainerBuilds()
    {
        var builder = new ContainerBuilder();
        builder.RegisterModule<CampaignVaultModule>();
        builder.RegisterInstance(new TestFakeEmbeddingService()).As<ILocalEmbeddingService>();
        using var container = builder.Build();
        using var scope = container.BeginLifetimeScope();

        var pipeline = scope.Resolve<RollModifierPipeline>();
        var r = pipeline.Resolve(
            new RollQuery(RollKinds.Attack, null, ["melee"], With("a"), With("t", "Prone"), "dnd5e", new Dictionary<string, string>()), 0);

        Assert.Equal(AdvantageEffect.Advantage, r.Advantage);
    }

    [Fact]
    public void EveryShippedDnd5eConditionEffectUsesKnownVocabulary()
    {
        foreach (var (name, def) in Conditions.GetConditionsForSystem("dnd5e"))
        {
            foreach (var e in def.Effects.Concat(def.Levels.Values.SelectMany(l => l)))
            {
                Assert.Contains(e.Kind, ConditionEffectKinds.All);
                Assert.True(e.Scope is null || ConditionScopes.All.Contains(e.Scope), $"{name}: bad scope {e.Scope}");
                Assert.True(e.Range is null or "melee" or "ranged", $"{name}: bad range {e.Range}");
                Assert.True(e.On is null or "attack" or "check" or "save", $"{name}: bad on {e.On}");
            }
        }
    }

    [Theory]
    [InlineData("blinded"), InlineData("frightened"), InlineData("grappled"), InlineData("incapacitated"), InlineData("invisible")]
    [InlineData("paralyzed"), InlineData("petrified"), InlineData("poisoned"), InlineData("prone"), InlineData("restrained")]
    [InlineData("stunned"), InlineData("unconscious")]
    public void ShippedDnd5eCondition_HasEffects(string name)
    {
        Assert.NotEmpty(Conditions.GetConditionsForSystem("dnd5e")[name].Effects);
    }

    [Fact]
    public void Merge_CarriesEffectsAndLevels()
    {
        var parent = new ConditionDefinition
        {
            Effects = [new ConditionEffect { Kind = ConditionEffectKinds.BlocksActions }],
            Levels = { [2] = [new ConditionEffect { Kind = ConditionEffectKinds.Death }] },
        };
        var merged = ConditionDefinition.Merge(new ConditionDefinition(), parent);

        Assert.Single(merged.Effects);
        Assert.Contains(merged.Levels, kv => kv.Key == 2);
    }

    [Fact]
    public void EffectsAt_IsCumulative()
    {
        var def = Conditions.GetConditionsForSystem("dnd5e")["exhaustion"];
        Assert.Equal(2, def.EffectsAt(2).Count());
        Assert.Contains(def.EffectsAt(6), e => e.Kind == ConditionEffectKinds.Death);
        Assert.DoesNotContain(def.EffectsAt(5), e => e.Kind == ConditionEffectKinds.Death);
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
        dispatcher: new WorldChangeDispatcher([], new CampaignDocumentKeys(), NullLogger<WorldChangeDispatcher>.Instance),
        campaignName: null);

    private static FakeRollService Rolls(params int[] results)
    {
        var rolls = new FakeRollService();
        foreach (var r in results)
            rolls.NextRolls.Enqueue(new RollOutcome { Result = r, Summary = r.ToString() });
        return rolls;
    }

    [Theory]
    [InlineData("melee", true)]
    [InlineData("ranged", false)]
    public async Task Attack_OnAParalyzedTarget_IsACritOnlyFromWithinReach(string weapon, bool crit)
    {
        var attacker = With("hank");
        var target = With("pell", "Paralyzed");
        ((Dnd5eExtension)target.SystemStats!).ArmorClass = 12;
        var action = new RulesetAction
        {
            CharacterId = "hank",
            TargetIds = ["pell"],
            ActionType = RulesetActionType.Attack,
            ActionName = "Strike",
            Parameters = new Dictionary<string, string> { ["damageDice"] = "1d6", ["weaponTags"] = weapon },
        };

        // Paralyzed gives the attacker advantage, so the attack roll is the first request; then damage, then crit dice if any.
        var fake = Rolls(15, 4, 3);
        var output = await new Dnd5eRulesetResolver(fake, rollModifiers: Pipeline)
            .ResolveAsync(Context(attacker, target), action, TestContext.Current.CancellationToken);

        Assert.Equal(crit, output.Result.Narrative.Contains("CRITICAL HIT", StringComparison.Ordinal));
        Assert.Equal(DiceMechanic.Advantage, fake.RecordedRequests[0].Mechanic);
    }

    [Fact]
    public async Task Save_ByAParalyzedCreature_FailsWhateverTheDiceShow()
    {
        var hero = With("hero", "Paralyzed");
        var action = new RulesetAction
        {
            CharacterId = "hero",
            ActionType = RulesetActionType.SavingThrow,
            ActionName = "Dodge",
            Parameters = new Dictionary<string, string> { ["dc"] = "5", ["save"] = "Dexterity" },
        };

        var output = await new Dnd5eRulesetResolver(Rolls(20), rollModifiers: Pipeline)
            .ResolveAsync(Context(hero), action, TestContext.Current.CancellationToken);

        Assert.Contains("Failure", output.Result.Narrative, StringComparison.Ordinal);
        Assert.Contains("automatic failure", output.Result.Narrative, StringComparison.Ordinal);
    }

    private static Character Mover(params string[] statuses)
    {
        var c = With("m", statuses);
        ((Dnd5eExtension)c.SystemStats!).Movement = 30;
        return c;
    }

    private static readonly Dictionary<string, string> NoOptions = new();

    [Theory]
    [InlineData("Restrained", 0, 5)]
    [InlineData("Grappled", 0, 5)]
    [InlineData("Exhaustion 2", 15, 15)]
    [InlineData("Exhaustion 5", 0, 5)]
    [InlineData("Poisoned", 30, 30)]
    public void Speed_ConditionsCanReachZeroInCombat_ButTravelKeepsItsFloor(string status, int combat, int travel)
    {
        var hero = Mover(status);

        Assert.Equal(combat, Pipeline.CombatSpeed(hero, NoOptions, "dnd5e"));
        Assert.Equal(travel, Pipeline.Speed(hero, NoOptions, "dnd5e"));
    }

    [Fact]
    public void Travel_StretchStaysCapped_ForARestrainedCaptive()
    {
        var stretch = SpeedRules.TravelStretch(Mover("Restrained"), Pipeline, NoOptions, "dnd5e");

        Assert.Equal(SpeedRules.MaxTravelStretch, stretch);
    }

    private static RollResolution Pf2e(string kind, Character actor, string? subject = null, params string[] tags) =>
        Pipeline.Resolve(new RollQuery(kind, subject, tags, actor, null, "pf2e", new Dictionary<string, string>()), 0);

    [Fact]
    public void Pf2e_Frightened_IsAStatusPenaltyToChecksAttacksSavesAndAc()
    {
        var hero = With("h", "Frightened 2");

        Assert.Equal(-2, Pf2e(RollKinds.Check, hero, "athletics").Bonus);
        Assert.Equal(-2, Pf2e(RollKinds.Attack, hero).Bonus);
        Assert.Equal(-2, Pf2e(RollKinds.Save, hero, "Will").Bonus);
        Assert.Equal(-2, Pf2e(RollKinds.ArmorClass, hero).Bonus);
    }

    [Fact]
    public void Pf2e_StatusPenaltiesOfDifferentConditionsDoNotStack_TheWorstCounts()
    {
        Assert.Equal(-2, Pf2e(RollKinds.Check, With("h", "Frightened 2", "Sickened 1"), "athletics").Bonus);
    }

    [Fact]
    public void Pf2e_Prone_IsOffGuard_AndPenalisesItsOwnAttacks()
    {
        var hero = With("h", "Prone");

        Assert.Equal(-2, Pf2e(RollKinds.ArmorClass, hero).Bonus);
        Assert.Equal(-2, Pf2e(RollKinds.Attack, hero).Bonus);
    }

    [Fact]
    public void Pf2e_TwoSourcesOfOffGuard_AreOne()
    {
        Assert.Equal(-2, Pf2e(RollKinds.ArmorClass, With("h", "Prone", "Restrained", "Off-Guard")).Bonus);
    }

    [Fact]
    public void Pf2e_Unconscious_TakesTheStatusPenaltyAndOffGuard_AndSuppressesProne()
    {
        var hero = With("h", "Unconscious", "Prone");

        Assert.Equal(-6, Pf2e(RollKinds.ArmorClass, hero).Bonus);   // -4 status, -2 circumstance
        Assert.Equal(-4, Pf2e(RollKinds.Save, hero, "Reflex").Bonus);
        Assert.Equal(0, Pf2e(RollKinds.Attack, hero).Bonus);        // prone's attack penalty is suppressed
    }

    [Fact]
    public void Pf2e_Clumsy_PenalisesDexterityRollsOnly()
    {
        var hero = With("h", "Clumsy 1");

        Assert.Equal(-1, Pf2e(RollKinds.Save, hero, "Reflex").Bonus);
        Assert.Equal(0, Pf2e(RollKinds.Save, hero, "Fortitude").Bonus);
        Assert.Equal(-1, Pf2e(RollKinds.Check, hero, "Stealth").Bonus);
        Assert.Equal(-1, Pf2e(RollKinds.Attack, hero, null, "ranged").Bonus);
        Assert.Equal(0, Pf2e(RollKinds.Attack, hero, null, "melee").Bonus);
    }

    [Theory]
    [InlineData("Prone", true)]
    [InlineData("Grabbed", true)]
    [InlineData("Restrained", true)]
    [InlineData("Paralyzed", true)]
    [InlineData("Unconscious", true)]
    [InlineData("Off-Guard", true)]
    [InlineData("Frightened 1", false)]
    public void Pf2e_OffGuard_IsImpliedByTheConditionsThatCauseIt(string status, bool offGuard)
    {
        Assert.Equal(offGuard, new ConditionMechanicsProvider(Conditions).Has(With("h", status), "pf2e", "off-guard"));
    }

    [Fact]
    public void Pf2e_Restrained_OverridesGrabbed_AndStopsMovement()
    {
        var hero = With("h", "Restrained", "Grabbed");
        Assert.Equal(["restrained"], ConditionMechanics.Active(hero, Conditions, "pf2e").Select(a => a.Definition.Name));
    }

    [Fact]
    public void EveryShippedPf2eConditionEffectUsesKnownVocabulary()
    {
        foreach (var (name, def) in Conditions.GetConditionsForSystem("pf2e"))
        {
            foreach (var e in def.Effects.Concat(def.Levels.Values.SelectMany(l => l)))
            {
                Assert.Contains(e.Kind, ConditionEffectKinds.All);
                Assert.True(e.BonusType is null or "status" or "circumstance" or "untyped", $"{name}: bad bonus type {e.BonusType}");
                Assert.True(e.On is null or "attack" or "check" or "save" or "ac", $"{name}: bad on {e.On}");
            }
        }
    }

    [Fact]
    public void Pf2e_OnlyTheDocumentedConditionsAreProseOnly()
    {
        // Each of these is a relation, an action-economy rule or a conditional the roll cannot see (see the note in its file).
        string[] proseOnly = ["blinded", "charmed", "deafened", "doomed", "invisible", "stunned", "slowed", "wounded"];
        var empty = Conditions.GetConditionsForSystem("pf2e").Where(kv => kv.Value.Effects.Count == 0 && kv.Value.Levels.Count == 0)
            .Select(kv => kv.Key).OrderBy(n => n).ToList();

        Assert.Equal(proseOnly.OrderBy(n => n), empty);
    }

    [Fact]
    public void Pf2e_NoStaleOrMissingConditionNames()
    {
        var defs = Conditions.GetConditionsForSystem("pf2e");
        foreach (var name in new[] { "off-guard", "slowed", "dying", "wounded", "drained", "enfeebled", "clumsy", "stupefied" })
            Assert.True(defs.ContainsKey(name), $"missing {name}");
        foreach (var def in defs.Values)
            Assert.All(def.Suppresses, target => Assert.True(defs.ContainsKey(target), $"{def.Name} suppresses unknown '{target}'"));
    }

    [Fact]
    public void TheSameConditionTwice_CountsOnce_AtItsHighestLevel()
    {
        var hero = Mover("Exhaustion 3", "Exhaustion 4");
        Assert.Equal(15, Pipeline.CombatSpeed(hero, NoOptions, "dnd5e"));   // halved once, not quartered

        var both = With("h", "Exhaustion 4", "Exhaustion 5");
        both.MaxHp = 40;
        Assert.Equal(20, ConditionMechanics.EffectiveMaxHp(both, Conditions));   // halved once
    }

    [Fact]
    public void ProductionResolvers_AreBuiltWithTheContainersConditionAwarePipeline()
    {
        var builder = new ContainerBuilder();
        builder.RegisterModule<CampaignVaultModule>();
        builder.RegisterInstance(new TestFakeEmbeddingService()).As<ILocalEmbeddingService>();
        using var container = builder.Build();
        using var scope = container.BeginLifetimeScope();

        var modules = scope.Resolve<IEnumerable<IRulesetModule>>().ToList();
        foreach (var resolver in modules.Where(m => m is Dnd5eRulesetResolver or Pf2eRulesetResolver))
        {
            var property = resolver.GetType().BaseType!.GetProperty("Pipeline", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var pipeline = (RollModifierPipeline)property.GetValue(resolver)!;

            Assert.True(pipeline.AutoCrit(With("t", "Paralyzed"), "dnd5e", ["melee"]), resolver.GetType().Name);
        }

        Assert.Contains(modules, m => m is Dnd5eRulesetResolver);
    }

    [Fact]
    public async Task Attack_ByAFrightenedCreature_HasDisadvantage_OnlyWhenTheDmAssertsTheSourceIsInSight()
    {
        async Task<DiceMechanic> AttackMechanic(params (string Key, string Value)[] extra)
        {
            var action = new RulesetAction
            {
                CharacterId = "hank",
                TargetIds = ["pell"],
                ActionType = RulesetActionType.Attack,
                ActionName = "Strike",
                Parameters = new Dictionary<string, string> { ["damageDice"] = "1d6", ["weaponTags"] = "melee" },
            };
            foreach (var (k, v) in extra)
                action.Parameters[k] = v;
            var fake = Rolls(15, 4);
            await new Dnd5eRulesetResolver(fake, rollModifiers: Pipeline)
                .ResolveAsync(Context(With("hank", "Frightened"), With("pell")), action, TestContext.Current.CancellationToken);
            return fake.RecordedRequests[0].Mechanic;
        }

        Assert.Equal(DiceMechanic.Standard, await AttackMechanic());
        Assert.Equal(DiceMechanic.Disadvantage, await AttackMechanic(("assert", "sourceInSight")));
    }
}
