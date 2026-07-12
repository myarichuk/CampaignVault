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

    [Theory]
    [InlineData(1, 1)] // RAW rounds UP: (1+1)/2 = 1, not 1/2 = 0
    [InlineData(2, 1)]
    [InlineData(3, 2)]
    [InlineData(20, 10)]
    public void ComputeCasterLevel_Artificer_RoundsCasterLevelUp(int classLevel, int expectedCasterLevel)
    {
        var classes = new List<ClassLevelEntry>
        {
            new() { Class = "Artificer", Level = classLevel }
        };

        Assert.Equal(expectedCasterLevel, Dnd5eCasterLevelHelper.ComputeCasterLevel(classes));
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
}