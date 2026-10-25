using System.Collections.Generic;
using System.IO;
using CampaignVault.Models;
using CampaignVault.Services;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>
/// The shipped 5e pool tables match the SRD 5.1 class tables level by level. The slot tables are keyed by caster
/// level, so a full caster is checked at every level and a half caster through the caster-level mapping.
/// </summary>
public class SrdPoolTableTests
{
    private readonly ResourcePoolInitializer _sut = RulesetDataTestHelper.CreateServices().Initializer;

    // SRD 5.1 "The Wizard" table: slots per spell level 1..9, by class level 1..20.
    private static readonly int[][] FullCaster =
    [
        [2, 0, 0, 0, 0, 0, 0, 0, 0], [3, 0, 0, 0, 0, 0, 0, 0, 0], [4, 2, 0, 0, 0, 0, 0, 0, 0], [4, 3, 0, 0, 0, 0, 0, 0, 0],
        [4, 3, 2, 0, 0, 0, 0, 0, 0], [4, 3, 3, 0, 0, 0, 0, 0, 0], [4, 3, 3, 1, 0, 0, 0, 0, 0], [4, 3, 3, 2, 0, 0, 0, 0, 0],
        [4, 3, 3, 3, 1, 0, 0, 0, 0], [4, 3, 3, 3, 2, 0, 0, 0, 0], [4, 3, 3, 3, 2, 1, 0, 0, 0], [4, 3, 3, 3, 2, 1, 0, 0, 0],
        [4, 3, 3, 3, 2, 1, 1, 0, 0], [4, 3, 3, 3, 2, 1, 1, 0, 0], [4, 3, 3, 3, 2, 1, 1, 1, 0], [4, 3, 3, 3, 2, 1, 1, 1, 0],
        [4, 3, 3, 3, 2, 1, 1, 1, 1], [4, 3, 3, 3, 3, 1, 1, 1, 1], [4, 3, 3, 3, 3, 2, 1, 1, 1], [4, 3, 3, 3, 3, 2, 2, 1, 1],
    ];

    private static Character Dnd5e(string className, int level) => new()
    {
        Id = $"chars/{className.ToLowerInvariant()}-{level}",
        ClassLevel = $"{className} {level}",
        SystemStats = new Dnd5eExtension
        {
            Level = level,
            ClassLevels = [new ClassLevelEntry { Class = className, Level = level }],
        },
    };

    private static int SlotMax(Character c, int spellLevel) =>
        c.SystemStats!.ResourcePools.TryGetValue($"spell_slots_{spellLevel}", out var pool) ? pool.Max : 0;

    [Theory]
    [InlineData("Wizard")]
    [InlineData("Cleric")]
    [InlineData("Sorcerer")]
    [InlineData("Druid")]
    [InlineData("Bard")]
    public void FullCaster_SlotsMatchTheSrdTable_AtEveryLevel(string className)
    {
        for (var level = 1; level <= 20; level++)
        {
            var c = Dnd5e(className, level);
            _sut.InitializePools(c, RulesetSystem.Dnd5e, null);
            for (var spell = 1; spell <= 9; spell++)
            {
                Assert.True(FullCaster[level - 1][spell - 1] == SlotMax(c, spell),
                    $"{className} {level}: level-{spell} slots expected {FullCaster[level - 1][spell - 1]}, got {SlotMax(c, spell)}");
            }
        }
    }

    [Theory]
    [InlineData(2, 2, 0, 0, 0, 0)]
    [InlineData(5, 4, 2, 0, 0, 0)]
    [InlineData(9, 4, 3, 2, 0, 0)]
    [InlineData(13, 4, 3, 3, 1, 0)]
    [InlineData(17, 4, 3, 3, 3, 1)]
    [InlineData(20, 4, 3, 3, 3, 2)]
    public void Paladin_HalfCasterSlots_MatchTheSrdTable(int level, int s1, int s2, int s3, int s4, int s5)
    {
        var c = Dnd5e("Paladin", level);
        _sut.InitializePools(c, RulesetSystem.Dnd5e, null);
        Assert.Equal((s1, s2, s3, s4, s5), (SlotMax(c, 1), SlotMax(c, 2), SlotMax(c, 3), SlotMax(c, 4), SlotMax(c, 5)));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(10, 2)]
    [InlineData(11, 3)]
    [InlineData(17, 4)]
    public void Warlock_PactMagicSlots(int level, int expected)
    {
        var c = Dnd5e("Warlock", level);
        _sut.InitializePools(c, RulesetSystem.Dnd5e, null);
        Assert.Equal(expected, c.SystemStats!.ResourcePools["pact_magic"].Max);
    }

    [Theory]
    [InlineData(2, 2)]
    [InlineData(9, 9)]
    [InlineData(15, 15)]
    [InlineData(20, 20)]
    public void Sorcerer_SorceryPointsEqualLevel(int level, int expected)
    {
        var c = Dnd5e("Sorcerer", level);
        _sut.InitializePools(c, RulesetSystem.Dnd5e, null);
        Assert.Equal(expected, c.SystemStats!.ResourcePools["font_of_magic"].Max);
    }

    [Theory]
    [InlineData("Cleric", 1, 0)]
    [InlineData("Cleric", 2, 1)]
    [InlineData("Cleric", 6, 2)]
    [InlineData("Cleric", 18, 3)]
    [InlineData("Paladin", 2, 0)]
    [InlineData("Paladin", 3, 1)]
    [InlineData("Paladin", 6, 1)]
    [InlineData("Paladin", 20, 1)]
    public void ChannelDivinity_ByClass(string className, int level, int expected)
    {
        var c = Dnd5e(className, level);
        _sut.InitializePools(c, RulesetSystem.Dnd5e, null);
        var actual = c.SystemStats!.ResourcePools.TryGetValue("channel_divinity", out var pool) ? pool.Max : 0;
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 2)]
    [InlineData(8, 2)]
    [InlineData(20, 2)]
    public void Druid_WildShape_IsTwoUses(int level, int expected)
    {
        var c = Dnd5e("Druid", level);
        _sut.InitializePools(c, RulesetSystem.Dnd5e, null);
        Assert.Equal(expected, c.SystemStats!.ResourcePools.TryGetValue("wildshape_uses", out var pool) ? pool.Max : 0);
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(3, 3)]
    [InlineData(6, 4)]
    [InlineData(12, 5)]
    [InlineData(17, 6)]
    public void Barbarian_Rages(int level, int expected)
    {
        var c = Dnd5e("Barbarian", level);
        _sut.InitializePools(c, RulesetSystem.Dnd5e, null);
        Assert.Equal(expected, c.SystemStats!.ResourcePools["rage"].Max);
    }

    [Fact]
    public void Paladin_LayOnHands_IsFivePerLevel()
    {
        var c = Dnd5e("Paladin", 7);
        _sut.InitializePools(c, RulesetSystem.Dnd5e, null);
        Assert.Equal(35, c.SystemStats!.ResourcePools["lay_on_hands"].Max);
    }

    [Theory]
    [InlineData(8, 0)]
    [InlineData(9, 1)]
    [InlineData(13, 2)]
    [InlineData(17, 3)]
    public void Fighter_Indomitable(int level, int expected)
    {
        var c = Dnd5e("Fighter", level);
        _sut.InitializePools(c, RulesetSystem.Dnd5e, null);
        Assert.Equal(expected, c.SystemStats!.ResourcePools.TryGetValue("indomitable", out var pool) ? pool.Max : 0);
    }

    [Fact]
    public void Wizard_ArcaneRecovery_OncePerDay()
    {
        var c = Dnd5e("Wizard", 1);
        _sut.InitializePools(c, RulesetSystem.Dnd5e, null);
        Assert.Equal(1, c.SystemStats!.ResourcePools["arcane_recovery"].Max);
    }
}
