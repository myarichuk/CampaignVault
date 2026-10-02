using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CampaignVault.Models;
using CampaignVault.Rulesets;
using CampaignVault.Rulesets.Creation;
using CampaignVault.Services;
using Xunit;

namespace CampaignVault.UnitTests;

/// <summary>A level-up for an existing character reads the builder's level choice slots and applies the picks.</summary>
public sealed class LevelUpPlannerTests : IDisposable
{
    private static readonly System.Reflection.Assembly Asm = typeof(RaceDefinitionProvider).Assembly;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cv-levelup-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private LevelUpPlanner Planner() => Create(Path.Combine(_root, "host"));

    internal static LevelUpPlanner Create(string host)
    {
        Directory.CreateDirectory(host);
        var progressions = new ProgressionDefinitionProvider(host, Asm);
        var sources = new CreationSources(
            new RaceDefinitionProvider(host, Asm), new ClassDefinitionProvider(host, Asm), new BackgroundDefinitionProvider(host, Asm),
            new FeatDefinitionProvider(host, Asm), new SpellDefinitionProvider(host, Asm), new CreatureDefinitionProvider(host, Asm), progressions);
        return new LevelUpPlanner(sources, progressions);
    }

    private static Character Fighter5e(int level = 3, int strength = 15) => new()
    {
        Name = "Hild",
        ClassLevel = $"Human Fighter {level}",
        SystemStats = new Dnd5eExtension { Level = level, Strength = strength, Constitution = 14, HitDie = "d10" },
    };

    private static Dictionary<string, List<string>> Picks(string id, params string[] values) => new() { [id] = [.. values] };

    [Fact]
    public void TheNextLevelsChoicesAreItsSlots()
    {
        var plan = Planner().Plan(Fighter5e(3), RulesetSystem.Dnd5e)!;

        Assert.Equal(4, plan.ClassLevel);
        var slot = Assert.Single(plan.Slots);
        Assert.Equal("4.asiOrFeat", slot.Id);
        Assert.True(slot.Required);
        Assert.Contains(slot.Options, o => o.Id == "Strength");
    }

    [Fact]
    public void AnAbilityScoreImprovementRaisesTheScoreAndIsRecorded()
    {
        var planner = Planner();
        var hild = Fighter5e(3);
        var plan = planner.Plan(hild, RulesetSystem.Dnd5e)!;
        var picks = Picks("4.asiOrFeat", "Strength", "Constitution");

        Assert.Empty(planner.Validate(plan, hild, picks));
        planner.Apply(plan, hild, picks);

        var stats = (Dnd5eExtension)hild.SystemStats!;
        Assert.Equal(16, stats.Strength);
        Assert.Equal(15, stats.Constitution);
        Assert.Contains(stats.LevelUpChoices, r => r.Key == "asiOrFeat" && r.Level == 4 && r.Value == "Strength +1, Constitution +1");
    }

    [Fact]
    public void AFeatInsteadOfTheImprovement_NeedsItsPrerequisites_AsInTheBuilder()
    {
        var planner = Planner();
        var weak = Fighter5e(3, strength: 12);
        var plan = planner.Plan(weak, RulesetSystem.Dnd5e)!;
        var problem = Assert.Single(planner.Validate(plan, weak, Picks("4.asiOrFeat", "grappler")));
        Assert.Contains("needs Strength 13", problem);

        var strong = Fighter5e(3, strength: 13);
        Assert.Empty(planner.Validate(planner.Plan(strong, RulesetSystem.Dnd5e)!, strong, Picks("4.asiOrFeat", "grappler")));
    }

    [Theory]
    [InlineData("Strength", "Strength", "to raise Strength by 2, pick it once")]
    [InlineData("Strength", "Dexterity;Constitution", "not both")]
    [InlineData("Nonsense", null, "isn't offered")]
    public void BadImprovementsAreRefusedWithTheReason(string first, string? second, string reason)
    {
        var planner = Planner();
        var hild = Fighter5e(3);
        var plan = planner.Plan(hild, RulesetSystem.Dnd5e)!;
        var values = second is null ? new[] { first } : new[] { first }.Concat(second.Split(';')).ToArray();
        // "Dexterity;Constitution" after "Strength" is three abilities; the second case checks a feat mixed in.
        if (reason == "not both")
            values = ["Strength", plan.Slots[0].Options.First(o => !plan.Slots[0].Abilities!.Contains(o.Id)).Id];

        var problems = planner.Validate(plan, hild, Picks("4.asiOrFeat", values));

        Assert.Contains(problems, p => p.Contains(reason));
    }

    [Fact]
    public void AScoreStopsAtTwenty()
    {
        var planner = Planner();
        var hild = Fighter5e(3, strength: 19);
        var plan = planner.Plan(hild, RulesetSystem.Dnd5e)!;

        Assert.Contains(planner.Validate(plan, hild, Picks("4.asiOrFeat", "Strength")), p => p.Contains("stop at 20"));
        Assert.Empty(planner.Validate(plan, hild, Picks("4.asiOrFeat", "Strength", "Dexterity")));
    }

    [Fact]
    public void AMissingRequiredChoiceAndAnUnknownSlotAreReported()
    {
        var planner = Planner();
        var hild = Fighter5e(3);
        var plan = planner.Plan(hild, RulesetSystem.Dnd5e)!;

        Assert.Contains(planner.Validate(plan, hild, new Dictionary<string, List<string>>()), p => p.Contains("choose"));
        Assert.Contains(planner.Validate(plan, hild, Picks("9.subclass", "x")), p => p.Contains("isn't a choice at level 4"));
    }

    private static Character Fighter2e(int level = 4) => new()
    {
        Name = "Ione",
        ClassLevel = $"Human Fighter {level}",
        SystemStats = new Pf2eExtension
        {
            Level = level,
            StrengthMod = 4,
            DexterityMod = 2,
            ConstitutionMod = 2,
            SkillProficiencies = new() { ["Athletics"] = Pf2eProficiencyRank.Trained, ["Acrobatics"] = Pf2eProficiencyRank.Trained },
        },
    };

    [Fact]
    public void Pf2eBoostsAndASkillIncreaseApplyInPlay()
    {
        var planner = Planner();
        var ione = Fighter2e(4);
        var plan = planner.Plan(ione, RulesetSystem.Pathfinder2e)!;
        Assert.Equal(["5.attributeBoosts", "5.skillIncrease"], plan.Slots.Select(s => s.Id).Order().ToArray());

        var picks = new Dictionary<string, List<string>>
        {
            ["5.attributeBoosts"] = ["Strength", "Dexterity", "Constitution", "Wisdom"],
            ["5.skillIncrease"] = ["Athletics"],
        };
        Assert.Empty(planner.Validate(plan, ione, picks));
        planner.Apply(plan, ione, picks);

        var stats = (Pf2eExtension)ione.SystemStats!;
        // Strength was +4: the first boost to it is half a boost, so it stays; the others rise by one.
        Assert.Equal(4, stats.StrengthMod);
        Assert.Equal(3, stats.DexterityMod);
        Assert.Equal(3, stats.ConstitutionMod);
        Assert.Equal(1, stats.WisdomMod);
        Assert.Equal(Pf2eProficiencyRank.Expert, stats.SkillProficiencies["Athletics"]);
    }

    [Fact]
    public void ASecondBoostAtPlusFourCompletesThePair()
    {
        var planner = Planner();
        var ione = Fighter2e(4);
        var plan = planner.Plan(ione, RulesetSystem.Pathfinder2e)!;
        var picks = Picks("5.attributeBoosts", "Strength", "Dexterity", "Constitution", "Wisdom");
        planner.Apply(plan, ione, picks);
        planner.Apply(plan, ione, picks);

        Assert.Equal(5, ((Pf2eExtension)ione.SystemStats!).StrengthMod);
    }

    [Fact]
    public void ASkillAtMasterBeforeLevelSevenIsRefused()
    {
        var planner = Planner();
        var ione = Fighter2e(4);
        ((Pf2eExtension)ione.SystemStats!).SkillProficiencies["Athletics"] = Pf2eProficiencyRank.Expert;
        var plan = planner.Plan(ione, RulesetSystem.Pathfinder2e)!;

        Assert.Contains(
            planner.Validate(plan, ione, new Dictionary<string, List<string>> { ["5.skillIncrease"] = ["Athletics"], ["5.attributeBoosts"] = ["Strength", "Dexterity", "Constitution", "Wisdom"] }),
            p => p.Contains("master needs level 7"));
    }
}
