using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using CampaignVault.Data.Templates;
using CampaignVault.Models;
using CampaignVault.Services;
using Xunit;

namespace CampaignVault.Tests;

public class ProgressionDefinitionTests
{
    private static ProgressionDefinitionProvider CreateProvider()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cv_progression_test_" + System.Guid.NewGuid());
        return new ProgressionDefinitionProvider(dir, typeof(ProgressionDefinitionProvider).Assembly);
    }

    [Fact]
    public void Dnd5eFighter_Level3_HasTheSrdSubclassChoice()
    {
        var provider = CreateProvider();
        var level3 = provider.GetLevelDefinition(RulesetSystem.Dnd5e, "fighter", 3);

        Assert.NotNull(level3);
        var subclass = Assert.Single(level3!.Choices);
        Assert.Equal("subclass", subclass.Key);
        Assert.Equal(ChoiceType.Enum, subclass.Type);
        Assert.Single(subclass.Options);
        Assert.Contains(subclass.Options, o => o.Id == "champion");
    }

    [Fact]
    public void Dnd5eFighter_Level1_HasFightingStyleChoice()
    {
        var provider = CreateProvider();
        var level1 = provider.GetLevelDefinition(RulesetSystem.Dnd5e, "fighter", 1);

        Assert.NotNull(level1);
        var fightingStyle = Assert.Single(level1!.Choices);
        Assert.Equal("fightingStyle", fightingStyle.Key);
        Assert.Equal(6, fightingStyle.Options.Count);
    }

    [Fact]
    public void Dnd5eWizard_Level4_HasAsiOrFeatChoiceWithAbilityOptions()
    {
        var provider = CreateProvider();
        var level4 = provider.GetLevelDefinition(RulesetSystem.Dnd5e, "wizard", 4);

        Assert.NotNull(level4);
        var asi = Assert.Single(level4!.Choices);
        Assert.Equal("asiOrFeat", asi.Key);
        Assert.Equal(ChoiceType.AsiOrFeat, asi.Type);
        Assert.Equal(6, asi.AbilityOptions.Count);
    }

    [Fact]
    public void Dnd5eRanger_AHuntersLevel7_GainsItsSubclassFeatureAndChoice_WhatLevelUpOffers()
    {
        var provider = CreateProvider();
        Assert.True(provider.TryGetProgression(RulesetSystem.Dnd5e, "ranger", out var ranger));
        IEnumerable<string> Hunter(int level, string key) => key == "subclass" ? ["hunter"] : [];

        var at7 = ranger!.ChoicesUpTo(7, Hunter).Where(c => c.Level == 7).Select(c => c.Choice.Key).ToList();
        var features = ranger.FeaturesUpTo(7, Hunter);

        Assert.Contains("defensiveTactics", at7);
        Assert.Contains(features, f => f is { Level: 7, Feature.Name: "Defensive Tactics", From.Id: "hunter" });
        Assert.DoesNotContain(ranger.ChoicesUpTo(7, (_, _) => []), c => c.Choice.Key == "defensiveTactics");
    }

    [Fact]
    public void Dnd5eWizard_Level2_HasSubclassChoice()
    {
        var provider = CreateProvider();
        var level2 = provider.GetLevelDefinition(RulesetSystem.Dnd5e, "wizard", 2);

        Assert.NotNull(level2);
        var subclass = Assert.Single(level2!.Choices);
        Assert.Equal("subclass", subclass.Key);
        Assert.Equal("evocation", Assert.Single(subclass.Options).Id);
    }

    [Fact]
    public void Dnd5eWarlock_Level2_InvocationChoiceHasLabelledOptions()
    {
        var provider = CreateProvider();
        var level2 = provider.GetLevelDefinition(RulesetSystem.Dnd5e, "warlock", 2);

        Assert.NotNull(level2);
        var invocation = Assert.Single(level2!.Choices);
        Assert.Equal("invocation", invocation.Key);
        Assert.Equal(ChoiceType.FeatSelection, invocation.Type);
        Assert.True(invocation.Options.Count > 5);
        Assert.All(invocation.Options, o => Assert.False(string.IsNullOrWhiteSpace(o.Description)));
        Assert.Contains(invocation.Options, o => o is { Id: "agonizingBlast", Label: "Agonizing Blast" });
    }

    [Fact]
    public void Pf2eFighter_Level1_HasFeatBudgetAndNoEnumeratedChoices()
    {
        var provider = CreateProvider();
        var level1 = provider.GetLevelDefinition(RulesetSystem.Pathfinder2e, "fighter", 1);

        Assert.NotNull(level1);
        Assert.Empty(level1!.Choices);
        Assert.Equal(1, level1.ClassFeats);
        Assert.Null(level1.SkillFeats); // Player Core: a fighter's first skill feat is the background's; the class's come at 2
        Assert.Equal(1, level1.AncestryFeats);
        Assert.Equal(4, level1.AbilityBoosts);
        Assert.Equal(1, provider.GetLevelDefinition(RulesetSystem.Pathfinder2e, "fighter", 2)!.SkillFeats);
    }

    [Fact]
    public void Pf2eWizard_Level1_HasNoClassFeat_AndASpellbookOfTenCantripsAndFiveSpells()
    {
        var provider = CreateProvider();
        Assert.True(provider.TryGetProgression(RulesetSystem.Pathfinder2e, "wizard", out var wizard));

        Assert.Null(wizard!.Levels[1].ClassFeats);
        Assert.Equal(1, wizard.Levels[2].ClassFeats);
        Assert.Equal(10, wizard.CountAtLevel(1, l => l.CantripsKnown));
        Assert.Equal(5, wizard.CountAtLevel(1, l => l.SpellsKnown));
        Assert.Equal(7, wizard.CountAtLevel(2, l => l.SpellsKnown));
        Assert.Equal(4, wizard.Levels[5].AbilityBoosts);
        Assert.Null(wizard.Levels[3].AbilityBoosts);
    }

    [Fact]
    public void AllAuthoredClasses_LoadWithoutError()
    {
        var provider = CreateProvider();

        foreach (var className in new[]
                 {
                     "barbarian", "bard", "cleric", "druid", "fighter", "monk",
                     "paladin", "ranger", "rogue", "sorcerer", "warlock", "wizard",
                 })
        {
            Assert.True(provider.TryGetProgression(RulesetSystem.Dnd5e, className, out var progression),
                $"{className} progression should load");
            Assert.Equal(20, progression!.Levels.Count);
        }

        foreach (var className in new[] { "bard", "cleric", "druid", "fighter", "ranger", "rogue", "witch", "wizard" })
        {
            Assert.True(provider.TryGetProgression(RulesetSystem.Pathfinder2e, className, out var progression),
                $"pf2e {className} progression should load");
            Assert.Equal(20, progression!.Levels.Count);
        }
    }
}
