using System;
using System.IO;
using System.Threading.Tasks;
using CampaignVault.Models;
using CampaignVault.Rulesets;
using CampaignVault.Rulesets.Bootstrap;
using CampaignVault.Services;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>Armor, weapon and tool proficiencies: derived onto the 5e sheet from class, features and background, and checked on attacks.</summary>
public class ProficiencyGrantsTests
{
    private static readonly string Dir = Path.Combine(Path.GetTempPath(), "cv_ruleset_test_" + Guid.NewGuid());
    private static readonly System.Reflection.Assembly Embedded = typeof(ClassDefinitionProvider).Assembly;

    private static Dnd5eDeriveProficiencyStep Step() => new(
        new ClassDefinitionProvider(Dir, Embedded),
        new BackgroundDefinitionProvider(Dir, Embedded),
        new ProgressionDefinitionProvider(Dir, Embedded));

    private static async Task<Dnd5eExtension> Derive(Character character)
    {
        await Step().ApplyAsync(
            new BootstrapContext { Character = character, ActiveSystem = RulesetSystem.Dnd5e },
            TestContext.Current.CancellationToken);
        return Assert.IsType<Dnd5eExtension>(character.SystemStats);
    }

    [Fact]
    public async Task StartingClass_GivesItsFullSet()
    {
        var stats = await Derive(new Character
        {
            Id = "chars/rogue", Name = "Rogue", ClassLevel = "Rogue 1",
            SystemStats = new Dnd5eExtension(),
        });

        Assert.Equal(["light"], stats.ArmorProficiencies);
        Assert.Contains("rapier", stats.WeaponProficiencies);
        Assert.Contains("thieves_tools", stats.ToolProficiencies);
    }

    [Fact]
    public async Task LaterClass_GivesOnlyItsMulticlassSet()
    {
        var stats = await Derive(new Character
        {
            Id = "chars/wiz-fighter", Name = "Mixed", ClassLevel = "Wizard 3 / Fighter 1",
            SystemStats = new Dnd5eExtension
            {
                ClassLevels = [new() { Class = "Wizard", Level = 3 }, new() { Class = "Fighter", Level = 1 }],
            },
        });

        Assert.Contains("medium", stats.ArmorProficiencies);
        Assert.DoesNotContain("heavy", stats.ArmorProficiencies);
        Assert.Contains("martial", stats.WeaponProficiencies);
    }

    [Fact]
    public async Task PickedSubclassFeature_AddsItsProficiency()
    {
        var stats = await Derive(new Character
        {
            Id = "chars/life", Name = "Healer", ClassLevel = "Cleric 1",
            SystemStats = new Dnd5eExtension
            {
                LevelUpChoices = [new LevelUpChoiceRecord { Level = 1, Key = "subclass", Value = "life" }],
            },
        });

        Assert.Contains("heavy", stats.ArmorProficiencies);
        Assert.Contains("shields", stats.ArmorProficiencies);
    }

    [Fact]
    public async Task Derivation_OnlyAdds_KeepingTheDmsEntries()
    {
        var stats = await Derive(new Character
        {
            Id = "chars/wizard", Name = "Wizard", ClassLevel = "Wizard 1",
            SystemStats = new Dnd5eExtension { WeaponProficiencies = ["longbow"] },
        });

        Assert.Contains("longbow", stats.WeaponProficiencies);
        Assert.Contains("quarterstaff", stats.WeaponProficiencies);
    }

    [Theory]
    [InlineData("martial,melee", "longsword", false)]   // wizard: no martial, no longsword
    [InlineData("simple,melee", "quarterstaff", true)]  // by name
    [InlineData("martial,ranged", "crossbow_light", true)] // name spelled with an underscore still matches
    [InlineData("", "", true)]                          // unknown weapon: benefit of the doubt
    public void WeaponProficiency_ByCategoryOrName(string tags, string definition, bool expected)
    {
        var stats = new Dnd5eExtension { WeaponProficiencies = ["dagger", "dart", "sling", "quarterstaff", "crossbow_light"] };
        var action = new RulesetAction { ActionType = RulesetActionType.Attack, ActionName = "Attack", CharacterId = "chars/w" };
        if (tags.Length > 0) action.Parameters["weaponTags"] = tags;
        if (definition.Length > 0) action.Parameters["weaponDefinition"] = definition;

        Assert.Equal(expected, CombatFeatureRules.IsWeaponProficient(stats, action));
    }

    [Fact]
    public void WeaponProficiency_EmptyList_IsProficient()
    {
        var action = new RulesetAction { ActionType = RulesetActionType.Attack, ActionName = "Attack", CharacterId = "chars/old" };
        action.Parameters["weaponTags"] = "martial";

        Assert.True(CombatFeatureRules.IsWeaponProficient(new Dnd5eExtension(), action));
    }

    [Fact]
    public void Attack_WithoutProficiency_DropsTheBonusAndSaysWhy()
    {
        var stats = new Dnd5eExtension { Strength = 14, WeaponProficiencies = ["simple"], Level = 5 };
        stats.Attributes["proficiencyBonus"] = 3;
        var action = new RulesetAction { ActionType = RulesetActionType.Attack, ActionName = "Attack", CharacterId = "chars/a" };
        action.Parameters["weaponTags"] = "martial,melee";

        var attack = DerivedAttack.Dnd5e(new Character { Id = "chars/a", Name = "A", SystemStats = stats }, stats, action, false, false);

        Assert.Equal(2, attack.ToHit);
        Assert.Contains("not proficient", attack.Note);
    }
}
