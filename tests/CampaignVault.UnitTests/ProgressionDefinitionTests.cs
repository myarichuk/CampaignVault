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
    public void Dnd5eFighter_Level3_HasSubclassChoiceWithFiveOptions()
    {
        var provider = CreateProvider();
        var level3 = provider.GetLevelDefinition(RulesetSystem.Dnd5e, "fighter", 3);

        Assert.NotNull(level3);
        var subclass = Assert.Single(level3!.Choices);
        Assert.Equal("subclass", subclass.Key);
        Assert.Equal(ChoiceType.Enum, subclass.Type);
        Assert.Equal(5, subclass.Options.Count);
        Assert.Contains(subclass.Options, o => o.Id == "battleMaster");
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
    public void Dnd5eWizard_Level2_HasSubclassChoice()
    {
        var provider = CreateProvider();
        var level2 = provider.GetLevelDefinition(RulesetSystem.Dnd5e, "wizard", 2);

        Assert.NotNull(level2);
        var subclass = Assert.Single(level2!.Choices);
        Assert.Equal("subclass", subclass.Key);
        Assert.Equal(8, subclass.Options.Count);
    }

    [Fact]
    public void Dnd5eWarlock_Level2_InvocationChoiceParsesScalarOptionsAsIdAndLabel()
    {
        var provider = CreateProvider();
        var level2 = provider.GetLevelDefinition(RulesetSystem.Dnd5e, "warlock", 2);

        Assert.NotNull(level2);
        var invocation = Assert.Single(level2!.Choices);
        Assert.Equal("invocation", invocation.Key);
        Assert.Equal(ChoiceType.FeatSelection, invocation.Type);
        Assert.True(invocation.Options.Count > 5);
        Assert.All(invocation.Options, o => Assert.Equal(o.Id, o.Label));
        Assert.Contains(invocation.Options, o => o.Id == "agonizingBlast");
    }

    [Fact]
    public void Pf2eFighter_Level1_HasFeatBudgetAndNoEnumeratedChoices()
    {
        var provider = CreateProvider();
        var level1 = provider.GetLevelDefinition(RulesetSystem.Pathfinder2e, "fighter", 1);

        Assert.NotNull(level1);
        Assert.Empty(level1!.Choices);
        Assert.Equal(1, level1.ClassFeats);
        Assert.Equal(1, level1.SkillFeats);
        Assert.Equal(1, level1.AncestryFeats);
        Assert.Equal(4, level1.AbilityBoosts);
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

        foreach (var className in new[] { "cleric", "fighter", "rogue", "wizard" })
        {
            Assert.True(provider.TryGetProgression(RulesetSystem.Pathfinder2e, className, out var progression),
                $"pf2e {className} progression should load");
            Assert.Equal(20, progression!.Levels.Count);
        }
    }
}
