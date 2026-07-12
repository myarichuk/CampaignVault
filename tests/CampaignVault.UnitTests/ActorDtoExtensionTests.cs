using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using CampaignVault.Models;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>
/// Tests for Stage 4: Actor DTO Expansion.
/// Verifies that race, background, and feat fields are properly serialized and deserialized.
/// </summary>
public class ActorDtoExtensionTests
{
    private readonly JsonSerializerOptions _options = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    [Fact]
    public void Dnd5eExtension_RaceAndFeats_RoundtripThroughJson()
    {
        // Arrange
        var ext = new Dnd5eExtension
        {
            Race = "half-elf",
            Background = "soldier",
            Feats = ["great_weapon_master", "alert"],
            Strength = 16
        };

        // Act
        var json = JsonSerializer.Serialize(ext, _options);
        var deserialized = JsonSerializer.Deserialize<Dnd5eExtension>(json, _options);

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal("half-elf", deserialized.Race);
        Assert.Equal("soldier", deserialized.Background);
        Assert.Equal(2, deserialized.Feats.Count);
        Assert.Contains("great_weapon_master", deserialized.Feats);
        Assert.Contains("alert", deserialized.Feats);
        Assert.Equal(16, deserialized.Strength);
    }

    [Fact]
    public void Dnd5eExtension_NullFields_DeserializeToNullOrEmpty()
    {
        // Arrange
        var json = """
                   {
                     "strength": 14,
                     "constitution": 12
                   }
                   """;

        // Act
        var ext = JsonSerializer.Deserialize<Dnd5eExtension>(json, _options);

        // Assert
        Assert.NotNull(ext);
        Assert.Null(ext.Race);
        Assert.Null(ext.Background);
        Assert.Empty(ext.Feats);
        Assert.Equal(14, ext.Strength);
    }

    [Fact]
    public void Pf2eExtension_AncestryFields_DoNotConflictWithAncestryHp()
    {
        // Arrange
        var ext = new Pf2eExtension
        {
            Ancestry = "dwarf",
            Heritage = "dwarven_clan_drinker",
            Background = "scholar",
            AncestryFeats = ["dwarven_resilience"],
            ClassFeats = ["shield_warden"],
            SkillFeats = ["assurance"],
            GeneralFeats = ["toughness"],
            AncestryHp = 10, // Numeric field for HP bootstrap
            StrengthMod = 2
        };

        // Act
        var json = JsonSerializer.Serialize(ext, _options);
        var deserialized = JsonSerializer.Deserialize<Pf2eExtension>(json, _options);

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal("dwarf", deserialized.Ancestry);
        Assert.Equal("dwarven_clan_drinker", deserialized.Heritage);
        Assert.Equal("scholar", deserialized.Background);
        Assert.Single(deserialized.AncestryFeats);
        Assert.Single(deserialized.ClassFeats);
        Assert.Single(deserialized.SkillFeats);
        Assert.Single(deserialized.GeneralFeats);
        Assert.Equal(10, deserialized.AncestryHp); // AncestryHp still works
        Assert.Equal(2, deserialized.StrengthMod);
    }

    [Fact]
    public void Pf2eExtension_MultipleFeatsPerCategory_SerializeCorrectly()
    {
        // Arrange
        var ext = new Pf2eExtension
        {
            ClassFeats = ["shield_warden", "fighter_dedication", "demoralize"],
            AncestryFeats = ["dwarven_resilience", "slow_fall"],
            SkillFeats = ["assurance", "trick_magic_item"]
        };

        // Act
        var json = JsonSerializer.Serialize(ext, _options);
        var deserialized = JsonSerializer.Deserialize<Pf2eExtension>(json, _options);

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal(3, deserialized.ClassFeats.Count);
        Assert.Equal(2, deserialized.AncestryFeats.Count);
        Assert.Equal(2, deserialized.SkillFeats.Count);
        Assert.Empty(deserialized.GeneralFeats);
    }

    [Fact]
    public void CharacterCreate_WithDnd5eSystemStats_PreservesIdentityFields()
    {
        // Arrange
        var change = new CharacterCreate
        {
            CharacterId = "chars/aragorn",
            Name = "Aragorn son of Arathorn",
            SystemStats = new Dnd5eExtension
            {
                Race = "human",
                Background = "ranger",
                Feats = ["alert", "lucky"],
                Level = 5,
                Strength = 16
            }
        };

        // Act
        var json = JsonSerializer.Serialize(change, _options);
        var deserialized = JsonSerializer.Deserialize<CharacterCreate>(json, _options);

        // Assert
        Assert.NotNull(deserialized);
        Assert.NotNull(deserialized.SystemStats);
        var stats = (Dnd5eExtension)deserialized.SystemStats;
        Assert.Equal("human", stats.Race);
        Assert.Equal("ranger", stats.Background);
        Assert.Equal(2, stats.Feats.Count);
        Assert.Equal(5, stats.Level);
        Assert.Equal(16, stats.Strength);
    }

    [Fact]
    public void CharacterCreate_WithPf2eSystemStats_PreservesAncestryFields()
    {
        // Arrange
        var change = new CharacterCreate
        {
            CharacterId = "chars/khalid",
            Name = "Khalid the Magnificent",
            SystemStats = new Pf2eExtension
            {
                Ancestry = "elf",
                Heritage = "arctic_elf",
                Background = "merchant",
                ClassFeats = ["shield_warden"],
                Level = 3
            }
        };

        // Act
        var json = JsonSerializer.Serialize(change, _options);
        var deserialized = JsonSerializer.Deserialize<CharacterCreate>(json, _options);

        // Assert
        Assert.NotNull(deserialized);
        Assert.NotNull(deserialized.SystemStats);
        var stats = (Pf2eExtension)deserialized.SystemStats;
        Assert.Equal("elf", stats.Ancestry);
        Assert.Equal("arctic_elf", stats.Heritage);
        Assert.Equal("merchant", stats.Background);
        Assert.Single(stats.ClassFeats);
        Assert.Equal(3, stats.Level);
    }

    [Fact]
    public void SystemStatsChange_WithDnd5eExtension_CanUpdateIdentityFields()
    {
        // Arrange: Simulate applying a SystemStatsChange that adds a feat
        var change = new SystemStatsChange
        {
            CharacterId = "chars/thief",
            SystemStats = new Dnd5eExtension
            {
                Race = "halfling",
                Background = "criminal",
                Feats = ["expertise", "cunning_action"]
            }
        };

        // Act: Serialize and deserialize to verify polymorphic dispatch
        var json = JsonSerializer.Serialize<WorldChange>(change, _options);
        var deserialized = JsonSerializer.Deserialize<WorldChange>(json, _options);

        // Assert
        Assert.NotNull(deserialized);
        var statsChange = Assert.IsType<SystemStatsChange>(deserialized);
        var stats = Assert.IsType<Dnd5eExtension>(statsChange.SystemStats);
        Assert.Equal("halfling", stats.Race);
        Assert.Equal("criminal", stats.Background);
        Assert.Equal(2, stats.Feats.Count);
    }
}
