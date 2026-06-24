using CampaignVault.Data;
using CampaignVault.Models;
using Xunit;

namespace CampaignVault.Tests;

public class PartyFlagTests
{
    [Theory]
    [InlineData(true, false, "dragon-heist", true)]
    [InlineData(false, true, "dragon-heist", true)]
    [InlineData(false, false, null, true)]
    [InlineData(false, false, "dragon-heist", true)]
    public void CharacterPartyRules_AllowsValidCombinations(bool isPc, bool isCompanion, string? campaign, bool expected)
    {
        var ok = CharacterPartyRules.TryValidate(isPc, isCompanion, campaign, out var error);
        Assert.Equal(expected, ok);
        Assert.Null(error);
    }

    [Fact]
    public void CharacterPartyRules_RejectsBothFlags()
    {
        var ok = CharacterPartyRules.TryValidate(true, true, "dragon-heist", out var error);
        Assert.False(ok);
        Assert.Contains("both isPc and isPartyCompanion", error);
    }

    [Fact]
    public void CharacterPartyRules_RequiresCampaignSlugForPartyFlags()
    {
        var ok = CharacterPartyRules.TryValidate(true, false, null, out var error);
        Assert.False(ok);
        Assert.Contains("require a campaign slug", error);
    }

    [Fact]
    public void CampaignEntityVisibility_CanonVisibleEverywhere()
    {
        Assert.True(CampaignEntityVisibility.IsVisibleInCampaign(null, "campaign-a"));
        Assert.True(CampaignEntityVisibility.IsVisibleInCampaign("", "campaign-b"));
    }

    [Fact]
    public void CampaignEntityVisibility_CampaignTaggedOnlyInMatchingSlug()
    {
        Assert.True(CampaignEntityVisibility.IsVisibleInCampaign("campaign-a", "campaign-a"));
        Assert.False(CampaignEntityVisibility.IsVisibleInCampaign("campaign-a", "campaign-b"));
    }

    [Fact]
    public void CampaignEntityVisibility_NormalizesSeparatorVariants()
    {
        Assert.True(CampaignEntityVisibility.IsVisibleInCampaign("Dragon Heist", "dragon-heist"));
        Assert.False(CampaignEntityVisibility.IsVisibleInCampaign("dragon-heist", "sword-coast"));
    }

    [Fact]
    public void CampaignEntityVisibility_IsPartyMember_RequiresFlagsAndSlug()
    {
        var pc = new Character { Id = "chars/pc", CampaignName = "camp-a", IsPc = true };
        var companion = new Character { Id = "chars/wolf", CampaignName = "camp-a", IsPartyCompanion = true };
        var keepAliveNpc = new Character { Id = "chars/bard", CampaignName = "camp-a", KeepAlive = true };
        var otherCampaignPc = new Character { Id = "chars/other", CampaignName = "camp-b", IsPc = true };

        Assert.True(CampaignEntityVisibility.IsPartyMember(pc, "camp-a"));
        Assert.True(CampaignEntityVisibility.IsPartyMember(companion, "camp-a"));
        Assert.False(CampaignEntityVisibility.IsPartyMember(keepAliveNpc, "camp-a"));
        Assert.False(CampaignEntityVisibility.IsPartyMember(otherCampaignPc, "camp-a"));
    }

    [Fact]
    public void CampaignEntityVisibility_IsCombatantAllowed_IncludesCanonAndMatchingCampaign()
    {
        var canon = new Character { Id = "chars/bob", CampaignName = null, CurrentHp = 10 };
        var owned = new Character { Id = "chars/pc", CampaignName = "camp-a", CurrentHp = 10 };
        var foreign = new Character { Id = "chars/enemy", CampaignName = "camp-b", CurrentHp = 10 };
        var dead = new Character { Id = "chars/dead", CampaignName = "camp-a", CurrentHp = 0 };

        Assert.True(CampaignEntityVisibility.IsCombatantAllowed(canon, "camp-a"));
        Assert.True(CampaignEntityVisibility.IsCombatantAllowed(owned, "camp-a"));
        Assert.False(CampaignEntityVisibility.IsCombatantAllowed(foreign, "camp-a"));
        Assert.False(CampaignEntityVisibility.IsCombatantAllowed(dead, "camp-a"));
    }
}