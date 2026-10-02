using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Models;
using CampaignVault.Rulesets;
using CampaignVault.Rulesets.Bootstrap;
using CampaignVault.Rulesets.Creation;
using CampaignVault.Services;
using Xunit;

namespace CampaignVault.UnitTests;

/// <summary>
/// 5e feats that give more than roll effects: a half-feat's ability (picked or fixed), proficiencies, a saving throw, skill
/// and spell picks, and hit points per level. Their choices are slots after the improvement that took the feat.
/// </summary>
public sealed class FeatGrantsTests : IDisposable
{
    private static readonly System.Reflection.Assembly Asm = typeof(RaceDefinitionProvider).Assembly;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cv-feat-grants-" + Guid.NewGuid().ToString("N"));
    private string Host => Path.Combine(_root, "host");

    public FeatGrantsTests()
    {
        var dir = Path.Combine(Host, "dnd5e", "feats");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "steadfast_training.yaml"), """
            name: steadfast_training
            system: dnd5e
            mechanicalSummary: +1 Strength or Constitution and its saving throw, a skill, a wizard cantrip, medium armor, +1 hp a level.
            abilityIncrease: { choose: [Strength, Constitution] }
            savingThrowOfIncrease: true
            skillChoices: 1
            spellChoices: [{ level: 0, count: 1, lists: [wizard] }]
            proficiencies: { armor: [medium] }
            hpPerLevel: 1
            """);
        File.WriteAllText(Path.Combine(dir, "silver_tongue.yaml"), """
            name: silver_tongue
            system: dnd5e
            abilityIncrease: { choose: [Charisma] }
            """);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static Character Fighter(int level = 3) => new()
    {
        Id = "chars/hild", Name = "Hild", ClassLevel = $"Fighter {level}",
        SystemStats = new Dnd5eExtension { Level = level, Strength = 15, Constitution = 13, Charisma = 10, HitDie = "d10" },
    };

    private static Dictionary<string, List<string>> Picks(params (string Slot, string[] Values)[] picks) =>
        picks.ToDictionary(p => p.Slot, p => p.Values.ToList());

    [Fact]
    public void AFeatsChoices_AreSlotsOnceItIsPicked()
    {
        var planner = LevelUpPlannerTests.Create(Host);
        var hild = Fighter();

        var before = planner.Plan(hild, RulesetSystem.Dnd5e)!;
        var after = planner.Plan(hild, RulesetSystem.Dnd5e, picks: Picks(("4.asiOrFeat", ["steadfast_training"])))!;

        Assert.Equal(["4.asiOrFeat"], before.Slots.Select(s => s.Id));
        Assert.Equal(
            ["4.asiOrFeat", "4.steadfast_training.ability", "4.steadfast_training.skills", "4.steadfast_training.spells"],
            after.Slots.Select(s => s.Id));
        Assert.Equal(["Strength", "Constitution"], after.Slots[1].Options.Select(o => o.Id));
        Assert.Contains(after.Slots[3].Options, o => o.Id == "fire_bolt");
        Assert.DoesNotContain(after.Slots[3].Options, o => o.Id == "magic_missile");
    }

    [Fact]
    public void AFeatsChoices_AreRequired_AndApplied()
    {
        var planner = LevelUpPlannerTests.Create(Host);
        var hild = Fighter();
        var featOnly = Picks(("4.asiOrFeat", ["steadfast_training"]));
        var all = Picks(
            ("4.asiOrFeat", ["steadfast_training"]), ("4.steadfast_training.ability", ["Constitution"]),
            ("4.steadfast_training.skills", ["Arcana"]), ("4.steadfast_training.spells", ["fire_bolt"]));

        Assert.Contains(planner.Validate(planner.Plan(hild, RulesetSystem.Dnd5e, picks: featOnly)!, hild, featOnly), p => p.Contains("ability +1"));
        var plan = planner.Plan(hild, RulesetSystem.Dnd5e, picks: all)!;
        Assert.Empty(planner.Validate(plan, hild, all));
        planner.Apply(plan, hild, all);

        var stats = (Dnd5eExtension)hild.SystemStats!;
        Assert.Equal(14, stats.Constitution);
        Assert.Contains("steadfast_training", stats.Feats);
        Assert.Contains(stats.LevelUpChoices, r => r is { Level: 4, Key: "steadfast_training.ability", Value: "Constitution" });
        Assert.Contains(stats.LevelUpChoices, r => r is { Key: "skills", Value: "Arcana" });
        Assert.Contains(stats.LevelUpChoices, r => r is { Key: "steadfast_training.spells", Value: "fire_bolt" });
    }

    [Fact]
    public void AHalfFeatWithOneAbility_RaisesItWithoutAPick()
    {
        var planner = LevelUpPlannerTests.Create(Host);
        var hild = Fighter();
        var picks = Picks(("4.asiOrFeat", ["silver_tongue"]));
        var plan = planner.Plan(hild, RulesetSystem.Dnd5e, picks: picks)!;

        Assert.Single(plan.Slots);
        Assert.Empty(planner.Validate(plan, hild, picks));
        planner.Apply(plan, hild, picks);

        Assert.Equal(11, ((Dnd5eExtension)hild.SystemStats!).Charisma);
    }

    private Character WithFeat(int level, int takenAt) => new()
    {
        Id = "chars/hild", Name = "Hild", ClassLevel = $"Fighter {level}",
        SystemStats = new Dnd5eExtension
        {
            Level = level, Strength = 15, Constitution = 14, HitDie = "d10",
            Feats = ["steadfast_training"],
            LevelUpChoices =
            [
                new LevelUpChoiceRecord { Level = takenAt, Key = "asiOrFeat", Value = "steadfast_training" },
                new LevelUpChoiceRecord { Level = takenAt, Key = "steadfast_training.ability", Value = "Constitution" },
                new LevelUpChoiceRecord { Level = takenAt, Key = "steadfast_training.spells", Value = "fire_bolt" },
            ],
        },
    };

    [Fact]
    public async Task ItsSavingThrow_Armor_AndCantrip_ReachTheSheet()
    {
        var hild = WithFeat(4, 4);
        var context = new BootstrapContext { Character = hild, ActiveSystem = RulesetSystem.Dnd5e };
        var feats = new FeatDefinitionProvider(Host, Asm);

        await new Dnd5eDeriveProficiencyStep(new ClassDefinitionProvider(Host, Asm), featProvider: feats).ApplyAsync(context, TestContext.Current.CancellationToken);
        await new Dnd5eGrantClassSpellsStep(null, feats, new SpellDefinitionProvider(Host, Asm)).ApplyAsync(context, TestContext.Current.CancellationToken);

        var stats = (Dnd5eExtension)hild.SystemStats!;
        Assert.Equal(4, stats.SavingThrowModifiers["Constitution"]);   // +2 modifier, +2 proficiency
        Assert.Contains("medium", stats.ArmorProficiencies);
        Assert.Equal(["fire_bolt"], stats.Spells.Cantrips);
        Assert.Empty(stats.Spells.Known);
    }

    [Fact]
    public async Task HitPointsPerLevel_CountFromTheLevelItIsTaken()
    {
        var feats = new FeatDefinitionProvider(Host, Asm);
        var step = new Dnd5eDeriveHitPointsStep(new DefaultRollService(new Random(1)), feats: feats);

        var hild = WithFeat(4, 4);
        await step.ApplyAsync(new BootstrapContext { Character = hild, ActiveSystem = RulesetSystem.Dnd5e }, TestContext.Current.CancellationToken);
        Assert.Equal(12 + 3 * 8 + 4, hild.MaxHp);   // d10 + 2, three averaged levels of 6 + 2, then 1 a level

        // The level it's taken at gives every earlier level's too; a later level gives one more.
        var gaining = WithFeat(3, 4);
        gaining.MaxHp = 28;
        await step.ApplyLevelGainAsync(
            new BootstrapContext { Character = gaining, ActiveSystem = RulesetSystem.Dnd5e, LevelsGained = 1 }, TestContext.Current.CancellationToken);
        Assert.Equal(28 + 8 + 4, gaining.MaxHp);
    }
}
