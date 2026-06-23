using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Models;
using CampaignVault.Rulesets.Bootstrap;
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

    [Fact]
    public async Task FalloutDeriveHitPointsStep_ComputesFromEnduranceLuckLevel()
    {
        var step = new FalloutDeriveHitPointsStep();
        var character = new Character
        {
            Id = "chars/vault",
            Name = "Vault Dweller",
            SystemStats = new Fallout2d20Extension
            {
                Endurance = 6,
                Luck = 5,
                Level = 3,
            },
        };

        await step.ApplyAsync(CreateContext(character, RulesetSystem.Fallout2d20));

        Assert.Equal(23, character.MaxHp);
    }

    [Fact]
    public async Task FalloutDeriveDefenseStep_SetsDefense2_WhenAgilityAtLeast9()
    {
        var step = new FalloutDeriveDefenseStep();
        var character = new Character
        {
            Id = "chars/wastelander",
            Name = "Wastelander",
            SystemStats = new Fallout2d20Extension { Agility = 9 },
        };

        var result = await step.ApplyAsync(CreateContext(character, RulesetSystem.Fallout2d20));
        var stats = Assert.IsType<Fallout2d20Extension>(character.SystemStats);

        Assert.NotNull(result);
        Assert.Equal(2, stats.Defense);
    }

    [Fact]
    public async Task FalloutDeriveDefenseStep_KeepsDefense1_WhenAgilityBelow9AndAlreadyCorrect()
    {
        var step = new FalloutDeriveDefenseStep();
        var character = new Character
        {
            Id = "chars/wastelander",
            Name = "Wastelander",
            SystemStats = new Fallout2d20Extension { Agility = 8 },
        };

        var result = await step.ApplyAsync(CreateContext(character, RulesetSystem.Fallout2d20));
        var stats = Assert.IsType<Fallout2d20Extension>(character.SystemStats);

        Assert.Null(result);
        Assert.Equal(1, stats.Defense);
    }

    [Fact]
    public async Task FalloutDeriveDefenseStep_ReDerivesWhenAgilityDropsBelowThreshold()
    {
        var step = new FalloutDeriveDefenseStep();
        var character = new Character
        {
            Id = "chars/wastelander",
            Name = "Wastelander",
            SystemStats = new Fallout2d20Extension { Agility = 7, Defense = 2 },
        };

        Assert.True(step.CanApply(CreateContext(character, RulesetSystem.Fallout2d20)));

        var result = await step.ApplyAsync(CreateContext(character, RulesetSystem.Fallout2d20));
        var stats = Assert.IsType<Fallout2d20Extension>(character.SystemStats);

        Assert.NotNull(result);
        Assert.Equal(1, stats.Defense);
    }

    [Fact]
    public void FalloutDeriveDefenseStep_IsUnarmoredDefense_AcceptsFactoryAndDerivedValues()
    {
        Assert.True(FalloutDeriveDefenseStep.IsUnarmoredDefense(1));
        Assert.True(FalloutDeriveDefenseStep.IsUnarmoredDefense(2));
        Assert.False(FalloutDeriveDefenseStep.IsUnarmoredDefense(3));
    }
}