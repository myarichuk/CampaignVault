using System;
using System.IO;
using CampaignVault.Models;
using CampaignVault.Services;
using Xunit;

namespace CampaignVault.Tests;

public class SkillDefinitionProviderTests
{
    private static readonly SkillDefinitionProvider Provider = new(
        Path.Combine(Path.GetTempPath(), "cv_skilldef_test_" + Guid.NewGuid()),
        typeof(SkillDefinitionProvider).Assembly);

    [Fact]
    public void Provider_LoadsFallout2d20Skills_FromEmbeddedResources()
    {
        var skills = Provider.GetSkillsForSystem(RulesetSystem.Fallout2d20);

        Assert.Equal(13, skills.Count);
        Assert.True(skills.ContainsKey("Small Guns"));
        Assert.True(skills.ContainsKey("Barter"));
    }

    [Fact]
    public void TryGet_SmallGuns_HasExpectedAttribute()
    {
        var found = Provider.TryGet(RulesetSystem.Fallout2d20, "Small Guns", out var skill);

        Assert.True(found);
        Assert.NotNull(skill);
        Assert.Equal("Agility", skill.Attribute);
    }

    [Fact]
    public void GetSkillsForSystem_Dnd5e_ReturnsEmpty()
    {
        var skills = Provider.GetSkillsForSystem(RulesetSystem.Dnd5e);

        Assert.Empty(skills);
    }
}
