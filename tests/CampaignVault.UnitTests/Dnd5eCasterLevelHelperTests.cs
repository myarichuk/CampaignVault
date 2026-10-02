using System.Collections.Generic;
using CampaignVault.Models;
using CampaignVault.Services;
using Xunit;

namespace CampaignVault.Tests;

public class Dnd5eCasterLevelHelperTests
{
    [Fact]
    public void ComputeCasterLevel_FighterWizardMulticlass_CountsWizardOnly()
    {
        var classes = new List<ClassLevelEntry>
        {
            new() { Class = "Fighter", Level = 5 },
            new() { Class = "Wizard", Level = 3 }
        };

        Assert.Equal(3, Dnd5eCasterLevelHelper.ComputeCasterLevel(classes));
    }

    [Fact]
    public void ComputeCasterLevel_PaladinSorcererMulticlass_StacksHalfAndFull()
    {
        var classes = new List<ClassLevelEntry>
        {
            new() { Class = "Paladin", Level = 6 },
            new() { Class = "Sorcerer", Level = 4 }
        };

        Assert.Equal(7, Dnd5eCasterLevelHelper.ComputeCasterLevel(classes));
    }

    [Fact]
    public void ComputeCasterLevel_Warlock_DoesNotContributeToStandardSlots()
    {
        var classes = new List<ClassLevelEntry>
        {
            new() { Class = "Warlock", Level = 5 },
            new() { Class = "Fighter", Level = 3 }
        };

        Assert.Equal(0, Dnd5eCasterLevelHelper.ComputeCasterLevel(classes));
    }

    [Fact]
    public void ComputeCasterLevel_PlainFighter_IsNonCaster()
    {
        // fighter_eldritch_knight.yaml was removed from the embedded set (non-SRD-base
        // subclass); plain "Fighter" must resolve to CasterType.None, not a third-caster.
        var classes = new List<ClassLevelEntry>
        {
            new() { Class = "Fighter", Level = 9 }
        };

        Assert.Equal(0, Dnd5eCasterLevelHelper.ComputeCasterLevel(classes));
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 1)]
    [InlineData(3, 2)]
    [InlineData(5, 3)]  // 4 first-level and 2 second-level slots, not the multiclass table's 3/0
    [InlineData(9, 5)]
    [InlineData(20, 10)]
    public void ComputeCasterLevel_SingleClassHalfCaster_RoundsUp(int level, int expected)
    {
        var classes = new List<ClassLevelEntry> { new() { Class = "Paladin", Level = level } };

        Assert.Equal(expected, Dnd5eCasterLevelHelper.ComputeCasterLevel(classes));
    }

    [Fact]
    public void ComputeCasterLevel_HalfCasterWithNonCasterOrWarlock_StillUsesItsOwnTable()
    {
        // Only one class has the Spellcasting feature, so the multiclass table doesn't apply.
        Assert.Equal(3, Dnd5eCasterLevelHelper.ComputeCasterLevel(new List<ClassLevelEntry>
        {
            new() { Class = "Ranger", Level = 5 },
            new() { Class = "Fighter", Level = 3 }
        }));
        Assert.Equal(3, Dnd5eCasterLevelHelper.ComputeCasterLevel(new List<ClassLevelEntry>
        {
            new() { Class = "Paladin", Level = 5 },
            new() { Class = "Warlock", Level = 2 }
        }));
    }
}