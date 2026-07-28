using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Models;
using CampaignVault.Rulesets.Bootstrap;
using CampaignVault.Services;
using Xunit;

namespace CampaignVault.Tests;

public class BootstrapStepTests
{
    private static BootstrapContext CreateContext(Character character, RulesetSystem system, int? explicitMaxHp = null) =>
        new()
        {
            Character = character,
            ActiveSystem = system,
            ExplicitMaxHp = explicitMaxHp,
        };

    [Fact]
    public async Task Dnd5eDeriveHitPointsStep_AverageLevel1_SetsMaxHp()
    {
        var step = new Dnd5eDeriveHitPointsStep(new DefaultRollService(new Random(42)));
        var character = new Character
        {
            Id = "chars/fighter",
            Name = "Fighter",
            ClassLevel = "Human Fighter 1",
            SystemStats = new Dnd5eExtension { Constitution = 14, HitDie = "d10" },
        };

        var result = await step.ApplyAsync(CreateContext(character, RulesetSystem.Dnd5e));

        Assert.NotNull(result);
        Assert.Equal(12, character.MaxHp);
        Assert.Equal(12, character.CurrentHp);
    }

    [Fact]
    public async Task Dnd5eDeriveHitPointsStep_AverageLevel10_Barbarian()
    {
        var step = new Dnd5eDeriveHitPointsStep(new DefaultRollService(new Random(42)));
        var character = new Character
        {
            Id = "chars/kergil",
            Name = "Kergil",
            ClassLevel = "Human Barbarian 10",
            SystemStats = new Dnd5eExtension
            {
                Constitution = 16,
                HitDie = "d12",
                Level = 10,
            },
        };

        await step.ApplyAsync(CreateContext(character, RulesetSystem.Dnd5e));

        Assert.Equal(105, character.MaxHp);
    }

    [Fact]
    public async Task Dnd5eDeriveHitPointsStep_SkipsWhenExplicitMaxHp()
    {
        var step = new Dnd5eDeriveHitPointsStep(new DefaultRollService(new Random(42)));
        var character = new Character
        {
            Id = "chars/goblin",
            Name = "Goblin",
            MaxHp = 7,
            SystemStats = new Dnd5eExtension { HitDie = "d6", Level = 1 },
        };

        Assert.False(step.CanApply(CreateContext(character, RulesetSystem.Dnd5e, explicitMaxHp: 7)));
    }

    [Fact]
    public async Task Dnd5eDeriveDefenseStep_AppliesDexMod()
    {
        var step = new Dnd5eDeriveDefenseStep();
        var character = new Character
        {
            Id = "chars/rogue",
            Name = "Rogue",
            SystemStats = new Dnd5eExtension { Dexterity = 16 },
        };

        var result = await step.ApplyAsync(CreateContext(character, RulesetSystem.Dnd5e));
        var stats = Assert.IsType<Dnd5eExtension>(character.SystemStats);

        Assert.NotNull(result);
        Assert.Equal(13, stats.ArmorClass);
    }

    [Fact]
    public async Task Dnd5eDeriveProficiencyStep_SetsBonusForLevel5()
    {
        var step = new Dnd5eDeriveProficiencyStep();
        var character = new Character
        {
            Id = "chars/cleric",
            Name = "Cleric",
            ClassLevel = "Human Cleric 5",
            SystemStats = new Dnd5eExtension(),
        };

        var result = await step.ApplyAsync(CreateContext(character, RulesetSystem.Dnd5e));
        var stats = Assert.IsType<Dnd5eExtension>(character.SystemStats);

        Assert.NotNull(result);
        Assert.Equal(3f, stats.Attributes["proficiencyBonus"]);
    }

    [Fact]
    public async Task Dnd5eDeriveProficiencyStep_FirstDerivation_HintsClassSkillChoice()
    {
        var step = new Dnd5eDeriveProficiencyStep();
        var character = new Character
        {
            Id = "chars/hinted-fighter",
            Name = "Borin",
            ClassLevel = "Dwarf Fighter 1",
            SystemStats = new Dnd5eExtension(),
        };
        var context = CreateContext(character, RulesetSystem.Dnd5e);

        var createResult = await step.ApplyAsync(context);
        Assert.NotNull(createResult);
        Assert.Contains(createResult!.LlmHints, h => h.Contains("Fighter") && h.Contains("skillModifiers"));

        // Level gain shouldn't repeat the hint once proficiencyBonus already exists at the same level.
        var levelGainResult = await step.ApplyLevelGainAsync(context);
        Assert.Null(levelGainResult);
    }

    [Fact]
    public async Task Dnd5eDeriveProficiencyStep_DerivesBackgroundSkillModifiers()
    {
        var assembly = typeof(ClassDefinitionProvider).Assembly;
        var dir = Path.Combine(Path.GetTempPath(), "cv_ruleset_test_" + Guid.NewGuid());
        var backgrounds = new BackgroundDefinitionProvider(dir, assembly);
        var step = new Dnd5eDeriveProficiencyStep(backgroundProvider: backgrounds);
        var character = new Character
        {
            Id = "chars/acolyte",
            Name = "Acolyte",
            ClassLevel = "Human Cleric 1",
            SystemStats = new Dnd5eExtension { Background = "acolyte", Wisdom = 16 },
        };

        var result = await step.ApplyAsync(CreateContext(character, RulesetSystem.Dnd5e));
        var stats = Assert.IsType<Dnd5eExtension>(character.SystemStats);

        Assert.NotNull(result);
        // acolyte grants Insight (Wisdom) + Religion (Intelligence); Wisdom mod +3, proficiencyBonus +2 at level 1
        Assert.Equal(5, stats.SkillModifiers["Insight"]);
        Assert.Equal(2, stats.SkillModifiers["Religion"]);
    }

    [Fact]
    public async Task Dnd5eDeriveProficiencyStep_DerivesClassSavingThrowModifiers()
    {
        var assembly = typeof(ClassDefinitionProvider).Assembly;
        var dir = Path.Combine(Path.GetTempPath(), "cv_ruleset_test_" + Guid.NewGuid());
        var classes = new ClassDefinitionProvider(dir, assembly);
        var step = new Dnd5eDeriveProficiencyStep(classProvider: classes);
        var character = new Character
        {
            Id = "chars/fighter-saves",
            Name = "Fighter",
            ClassLevel = "Human Fighter 1",
            SystemStats = new Dnd5eExtension { Strength = 16, Constitution = 14 },
        };

        var result = await step.ApplyAsync(CreateContext(character, RulesetSystem.Dnd5e));
        var stats = Assert.IsType<Dnd5eExtension>(character.SystemStats);

        Assert.NotNull(result);
        // Fighter is proficient in Strength + Constitution saves; +3 Str mod / +2 Con mod, proficiencyBonus +2 at level 1
        Assert.Equal(5, stats.SavingThrowModifiers["Strength"]);
        Assert.Equal(4, stats.SavingThrowModifiers["Constitution"]);
    }

    [Fact]
    public async Task Dnd5eDeriveProficiencyStep_DoesNotOverwriteExistingSkillModifier()
    {
        var assembly = typeof(ClassDefinitionProvider).Assembly;
        var dir = Path.Combine(Path.GetTempPath(), "cv_ruleset_test_" + Guid.NewGuid());
        var backgrounds = new BackgroundDefinitionProvider(dir, assembly);
        var step = new Dnd5eDeriveProficiencyStep(backgroundProvider: backgrounds);
        var character = new Character
        {
            Id = "chars/acolyte-override",
            Name = "Acolyte",
            ClassLevel = "Human Cleric 1",
            SystemStats = new Dnd5eExtension
            {
                Background = "acolyte",
                Wisdom = 16,
                SkillModifiers = new Dictionary<string, int> { ["Insight"] = 99 },
            },
        };

        await step.ApplyAsync(CreateContext(character, RulesetSystem.Dnd5e));
        var stats = Assert.IsType<Dnd5eExtension>(character.SystemStats);

        Assert.Equal(99, stats.SkillModifiers["Insight"]);
    }

    [Fact]
    public async Task Dnd5eDerivePassivePerceptionStep_UsesSkillModifier()
    {
        var step = new Dnd5eDerivePassivePerceptionStep();
        var character = new Character
        {
            Id = "chars/ranger",
            Name = "Ranger",
            SystemStats = new Dnd5eExtension
            {
                SkillModifiers = new Dictionary<string, int> { ["Perception"] = 5 },
            },
        };

        var result = await step.ApplyAsync(CreateContext(character, RulesetSystem.Dnd5e));
        var stats = Assert.IsType<Dnd5eExtension>(character.SystemStats);

        Assert.NotNull(result);
        Assert.Equal(15f, stats.Attributes["passivePerception"]);
    }

    [Fact]
    public async Task Dnd5eDerivePassivePerceptionStep_OnLevelGain_RefreshesStaleValue()
    {
        var step = new Dnd5eDerivePassivePerceptionStep();
        var character = new Character
        {
            Id = "chars/ranger",
            Name = "Ranger",
            SystemStats = new Dnd5eExtension
            {
                Wisdom = 14,
                Attributes = { ["passivePerception"] = 10f },
            },
        };

        var result = await step.ApplyLevelGainAsync(CreateContext(character, RulesetSystem.Dnd5e));
        var stats = Assert.IsType<Dnd5eExtension>(character.SystemStats);

        Assert.NotNull(result);
        Assert.Equal(12f, stats.Attributes["passivePerception"]);
    }

    [Fact]
    public async Task Pf2eDeriveHitPointsStep_ComputesFromAncestryAndClass()
    {
        var step = new Pf2eDeriveHitPointsStep();
        var character = new Character
        {
            Id = "chars/pf",
            Name = "Elara",
            ClassLevel = "Human Fighter 2",
            SystemStats = new Pf2eExtension
            {
                ClassHpPerLevel = 10,
                AncestryHp = 8,
                Level = 2,
                ConstitutionMod = 2,
            },
        };

        await step.ApplyAsync(CreateContext(character, RulesetSystem.Pathfinder2e));

        Assert.Equal(32, character.MaxHp);
    }

    [Fact]
    public async Task Pf2eDeriveProficiencyStep_DerivesNumericSkillAndSaveModifiers()
    {
        var profStep = new Pf2eDeriveProficiencyStep();
        var character = new Character
        {
            Id = "chars/pf-skills",
            Name = "Elara",
            SystemStats = new Pf2eExtension { Level = 3, DexterityMod = 3, WisdomMod = 2 },
        };

        var result = await profStep.ApplyAsync(CreateContext(character, RulesetSystem.Pathfinder2e));
        var stats = Assert.IsType<Pf2eExtension>(character.SystemStats);

        Assert.NotNull(result);
        // Trained (rank value 2) + level 3 + Dex mod 3 = 8
        Assert.Equal(8, stats.SkillModifiers["Stealth"]);
        // Reflex save: Trained (2) + level 3 + Dex mod 3 = 8
        Assert.Equal(8, stats.SavingThrowModifiers["Reflex"]);
        // Will save: Trained (2) + level 3 + Wis mod 2 = 7
        Assert.Equal(7, stats.SavingThrowModifiers["Will"]);
    }

    [Fact]
    public async Task Pf2eDeriveDefenseStep_AppliesDexMod()
    {
        var step = new Pf2eDeriveDefenseStep();
        var character = new Character
        {
            Id = "chars/pf",
            Name = "Elara",
            SystemStats = new Pf2eExtension { DexterityMod = 3 },
        };

        var result = await step.ApplyAsync(CreateContext(character, RulesetSystem.Pathfinder2e));
        var stats = Assert.IsType<Pf2eExtension>(character.SystemStats);

        Assert.NotNull(result);
        Assert.Equal(13, stats.ArmorClass);
    }

    [Theory]
    [InlineData(1, Pf2eProficiencyRank.Trained)]
    [InlineData(7, Pf2eProficiencyRank.Expert)]
    [InlineData(15, Pf2eProficiencyRank.Master)]
    public async Task Pf2eDeriveSpellcastingStep_DerivesProficiencyFromLevel(int level, Pf2eProficiencyRank expectedRank)
    {
        var profStep = new Pf2eDeriveProficiencyStep();
        var spellStep = new Pf2eDeriveSpellcastingStep();
        var character = new Character
        {
            Id = "chars/caster",
            Name = "Caster",
            ClassLevel = "Wizard " + level,
            SystemStats = new Pf2eExtension { Level = level, IntelligenceMod = 4 },
        };
        var context = CreateContext(character, RulesetSystem.Pathfinder2e);

        await profStep.ApplyAsync(context);
        await spellStep.ApplyAsync(context);
        var stats = Assert.IsType<Pf2eExtension>(character.SystemStats);

        Assert.Equal(expectedRank, stats.SpellcastingProficiency);
        var expectedProficiencyBonus = expectedRank == Pf2eProficiencyRank.Untrained ? 0 : level + (int)expectedRank;
        Assert.Equal(10 + 4 + expectedProficiencyBonus, stats.SpellDc);
    }

    [Fact]
    public async Task Pf2eDeriveSpellcastingStep_ExplicitSpellcastingProficiency_IsRespectedNotOverwritten()
    {
        var profStep = new Pf2eDeriveProficiencyStep();
        var spellStep = new Pf2eDeriveSpellcastingStep();
        var character = new Character
        {
            Id = "chars/caster",
            Name = "Caster",
            ClassLevel = "Wizard 1",
            SystemStats = new Pf2eExtension
            {
                Level = 1,
                IntelligenceMod = 4,
                SpellcastingProficiency = Pf2eProficiencyRank.Legendary,
            },
        };
        var context = CreateContext(character, RulesetSystem.Pathfinder2e);

        await profStep.ApplyAsync(context);
        await spellStep.ApplyAsync(context);
        var stats = Assert.IsType<Pf2eExtension>(character.SystemStats);

        Assert.Equal(Pf2eProficiencyRank.Legendary, stats.SpellcastingProficiency);
        Assert.Equal(10 + 4 + (1 + 8), stats.SpellDc);
    }

}
