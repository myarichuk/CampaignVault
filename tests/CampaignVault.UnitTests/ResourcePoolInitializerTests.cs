using System.Collections.Generic;
using CampaignVault.Models;
using CampaignVault.Services;
using Xunit;

namespace CampaignVault.Tests;

public class ResourcePoolInitializerTests
{
    private readonly ResourcePoolInitializer _sut = RulesetDataTestHelper.CreateServices().Initializer;

    [Fact]
    public void InitializePools_Pathfinder2eWizard_CreatesPf2eCasterPools()
    {
        var character = new Character
        {
            Id = "chars/pc1",
            ClassLevel = "Human Wizard 3",
            SystemStats = new Pf2eExtension { Level = 3 }
        };

        _sut.InitializePools(character, RulesetSystem.Pathfinder2e, null);

        Assert.True(character.SystemStats.ResourcePools.ContainsKey("spell_slots_1"));
        Assert.True(character.SystemStats.ResourcePools.ContainsKey("spell_slots_2"));
        Assert.True(character.SystemStats.ResourcePools.ContainsKey("focus_points"));
        Assert.Equal(1, character.SystemStats.ResourcePools["spell_slots_1"].Max);
    }

    [Fact]
    public void InitializePools_Pathfinder2eFighter_HasNoSpellSlots()
    {
        var character = new Character
        {
            Id = "chars/pc1",
            ClassLevel = "Human Fighter 3",
            SystemStats = new Pf2eExtension { Level = 3 }
        };

        _sut.InitializePools(character, RulesetSystem.Pathfinder2e, null);

        Assert.DoesNotContain(character.SystemStats.ResourcePools.Keys, k => k.StartsWith("spell_slots_"));
        Assert.False(character.SystemStats.ResourcePools.ContainsKey("focus_points"));
        Assert.False(character.SystemStats.ResourcePools.ContainsKey("action_surge"));
    }

    [Fact]
    public void InitializePools_Dnd5eWizard_CreatesSpellSlots()
    {
        var character = new Character
        {
            Id = "chars/pc1",
            ClassLevel = "Human Wizard 3",
            SystemStats = new Dnd5eExtension { Level = 3 }
        };

        _sut.InitializePools(character, RulesetSystem.Dnd5e, null);

        Assert.Equal(4, character.SystemStats.ResourcePools["spell_slots_1"].Max);
        Assert.Equal(2, character.SystemStats.ResourcePools["spell_slots_2"].Max);
        Assert.False(character.SystemStats.ResourcePools.ContainsKey("spell_slots_3"));
        Assert.False(character.SystemStats.ResourcePools.ContainsKey("ki_points"));
    }

    [Fact]
    public void InitializePools_Dnd5eFighter_HasClassPoolsOnly()
    {
        var character = new Character
        {
            Id = "chars/pc1",
            ClassLevel = "Human Fighter 5",
            SystemStats = new Dnd5eExtension { Level = 5 }
        };

        _sut.InitializePools(character, RulesetSystem.Dnd5e, null);

        Assert.DoesNotContain(character.SystemStats.ResourcePools.Keys, k => k.StartsWith("spell_slots_"));
        Assert.Equal(1, character.SystemStats.ResourcePools["action_surge"].Max);
        Assert.False(character.SystemStats.ResourcePools.ContainsKey("ki_points"));
    }

    [Fact]
    public void InitializePools_Dnd5eMulticlass_UsesCombinedCasterLevel()
    {
        var character = new Character
        {
            Id = "chars/gish",
            ClassLevel = "Fighter 5 / Wizard 3",
            SystemStats = new Dnd5eExtension
            {
                Level = 8,
                ClassLevels =
                [
                    new ClassLevelEntry { Class = "Fighter", Level = 5 },
                    new ClassLevelEntry { Class = "Wizard", Level = 3 }
                ]
            }
        };

        _sut.InitializePools(character, RulesetSystem.Dnd5e, null);

        Assert.Equal(4, character.SystemStats.ResourcePools["spell_slots_1"].Max);
        Assert.Equal(2, character.SystemStats.ResourcePools["spell_slots_2"].Max);
        Assert.Equal(1, character.SystemStats.ResourcePools["action_surge"].Max);
    }

    [Fact]
    public void InitializePools_Dnd5eHalfCasterMulticlass_StacksCasterLevel()
    {
        var character = new Character
        {
            Id = "chars/paladin-sorc",
            ClassLevel = "Paladin 6 / Sorcerer 4",
            SystemStats = new Dnd5eExtension
            {
                Level = 10,
                ClassLevels =
                [
                    new ClassLevelEntry { Class = "Paladin", Level = 6 },
                    new ClassLevelEntry { Class = "Sorcerer", Level = 4 }
                ]
            }
        };

        _sut.InitializePools(character, RulesetSystem.Dnd5e, null);

        // Caster level 7 = 4 (sorcerer) + 3 (paladin 6 / 2)
        Assert.Equal(4, character.SystemStats.ResourcePools["spell_slots_1"].Max);
        Assert.Equal(3, character.SystemStats.ResourcePools["spell_slots_2"].Max);
        Assert.Equal(1, character.SystemStats.ResourcePools["spell_slots_4"].Max);
        Assert.Equal(4, character.SystemStats.ResourcePools["font_of_magic"].Max);
    }

    [Fact]
    public void InitializePools_ExistingCharacter_PreservesSpentPools()
    {
        var character = new Character
        {
            Id = "chars/wizard",
            ClassLevel = "Wizard 3",
            SystemStats = new Dnd5eExtension
            {
                Level = 3,
                ResourcePools = new Dictionary<string, ResourcePool>
                {
                    ["spell_slots_1"] = new() { Current = 1, Max = 4, Recovery = RecoveryType.LongRest }
                }
            }
        };

        _sut.InitializePools(character, RulesetSystem.Dnd5e, null);

        Assert.Equal(1, character.SystemStats.ResourcePools["spell_slots_1"].Current);
        Assert.Equal(4, character.SystemStats.ResourcePools["spell_slots_1"].Max);
    }

    [Fact]
    public void InitializePools_ClassChange_RemovesStalePools()
    {
        var character = new Character
        {
            Id = "chars/retrain",
            ClassLevel = "Fighter 5",
            SystemStats = new Dnd5eExtension
            {
                Level = 5,
                ResourcePools = new Dictionary<string, ResourcePool>
                {
                    ["spell_slots_1"] = new() { Current = 2, Max = 4, Recovery = RecoveryType.LongRest },
                    ["ki_points"] = new() { Current = 3, Max = 5, Recovery = RecoveryType.ShortRest }
                }
            }
        };

        _sut.InitializePools(character, RulesetSystem.Dnd5e, null);

        Assert.DoesNotContain(character.SystemStats.ResourcePools.Keys, k => k.StartsWith("spell_slots_"));
        Assert.False(character.SystemStats.ResourcePools.ContainsKey("ki_points"));
        Assert.True(character.SystemStats.ResourcePools.ContainsKey("action_surge"));
    }

    [Fact]
    public void InitializePools_Fallout2d20_CreatesActionPoints()
    {
        var character = new Character
        {
            Id = "chars/vault-dweller",
            ClassLevel = "Survivor 1",
            SystemStats = new Fallout2d20Extension { Level = 1 }
        };

        _sut.InitializePools(character, RulesetSystem.Fallout2d20, null);

        Assert.Equal(10, character.SystemStats.ResourcePools["action_points"].Max);
        Assert.Equal(10, character.SystemStats.ResourcePools["action_points"].Current);
    }

    [Theory]
    [InlineData(RulesetSystem.Dnd5e, "dnd5e")]
    [InlineData(RulesetSystem.Pathfinder2e, "pf2e")]
    [InlineData(RulesetSystem.Fallout2d20, "fallout2d20")]
    [InlineData(RulesetSystem.Narrative, "narrative")]
    public void ToSlug_ReturnsCanonicalSlug(RulesetSystem system, string expected)
    {
        Assert.Equal(expected, system.ToSlug());
    }
}