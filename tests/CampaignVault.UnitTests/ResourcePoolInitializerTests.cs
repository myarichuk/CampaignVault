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
    public void InitializePools_Pathfinder2eLowLevelWizard_HasNoHighRankSlots()
    {
        var character = new Character
        {
            Id = "chars/pc1",
            ClassLevel = "Human Wizard 3",
            SystemStats = new Pf2eExtension { Level = 3 }
        };

        _sut.InitializePools(character, RulesetSystem.Pathfinder2e, null);

        Assert.False(character.SystemStats.ResourcePools.ContainsKey("spell_slots_5"));
        Assert.False(character.SystemStats.ResourcePools.ContainsKey("spell_slots_10"));
    }

    [Fact]
    public void InitializePools_Pathfinder2eLevel9Wizard_GainsRank5Slot()
    {
        var character = new Character
        {
            Id = "chars/pc1",
            ClassLevel = "Human Wizard 9",
            SystemStats = new Pf2eExtension { Level = 9 }
        };

        _sut.InitializePools(character, RulesetSystem.Pathfinder2e, null);

        Assert.Equal(1, character.SystemStats.ResourcePools["spell_slots_5"].Max);
        Assert.False(character.SystemStats.ResourcePools.ContainsKey("spell_slots_6"));
    }

    [Fact]
    public void InitializePools_Pathfinder2eLevel19Wizard_GainsRank10Slot()
    {
        var character = new Character
        {
            Id = "chars/pc1",
            ClassLevel = "Human Wizard 19",
            SystemStats = new Pf2eExtension { Level = 19 }
        };

        _sut.InitializePools(character, RulesetSystem.Pathfinder2e, null);

        Assert.Equal(1, character.SystemStats.ResourcePools["spell_slots_10"].Max);
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

    [Theory]
    [InlineData(RulesetSystem.Dnd5e)]
    [InlineData(RulesetSystem.Pathfinder2e)]
    public void InitializePools_Dnd5eOrPf2e_GrantsGoldPool(RulesetSystem system)
    {
        var character = new Character
        {
            Id = "chars/pc1",
            ClassLevel = "Human Fighter 1",
            SystemStats = system == RulesetSystem.Dnd5e
                ? new Dnd5eExtension { Level = 1 }
                : new Pf2eExtension { Level = 1 }
        };

        _sut.InitializePools(character, system, null);

        Assert.True(character.SystemStats.ResourcePools.ContainsKey("gold"));
        Assert.Equal(1000000, character.SystemStats.ResourcePools["gold"].Max);
    }

    [Fact]
    public void InitializePools_Warlock_GrantsPactMagicByWarlockLevel()
    {
        var character = new Character
        {
            Id = "chars/warlock",
            ClassLevel = "Warlock 11",
            SystemStats = new Dnd5eExtension { Level = 11 }
        };

        _sut.InitializePools(character, RulesetSystem.Dnd5e, null);

        Assert.True(character.SystemStats.ResourcePools.ContainsKey("pact_magic"));
        Assert.Equal(3, character.SystemStats.ResourcePools["pact_magic"].Max);
    }

    [Fact]
    public void InitializePools_WarlockFighterMulticlass_PactMagicUsesWarlockClassLevelOnly()
    {
        var character = new Character
        {
            Id = "chars/warlock-fighter",
            ClassLevel = "Warlock 3 / Fighter 5",
            SystemStats = new Dnd5eExtension
            {
                Level = 8,
                ClassLevels =
                [
                    new ClassLevelEntry { Class = "Warlock", Level = 3 },
                    new ClassLevelEntry { Class = "Fighter", Level = 5 }
                ]
            }
        };

        _sut.InitializePools(character, RulesetSystem.Dnd5e, null);

        // Pact Magic scales with warlock class level (3), not total character level (8).
        Assert.Equal(2, character.SystemStats.ResourcePools["pact_magic"].Max);
    }

    [Theory]
    [InlineData(RulesetSystem.Dnd5e, "dnd5e")]
    [InlineData(RulesetSystem.Pathfinder2e, "pf2e")]
    [InlineData(RulesetSystem.Narrative, "narrative")]
    public void ToSlug_ReturnsCanonicalSlug(RulesetSystem system, string expected)
    {
        Assert.Equal(expected, system.ToSlug());
    }
}