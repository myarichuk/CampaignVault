using CampaignVault.Models;
using CampaignVault.Rulesets.Bootstrap;
using Xunit;

namespace CampaignVault.Tests;

public class BootstrapHpResolverTests
{
    [Fact]
    public void Resolve_OmitsBoth_Derives()
    {
        var character = new Character
        {
            SystemStats = new Dnd5eExtension { Constitution = 14 },
        };

        var hp = BootstrapHpResolver.Resolve(character, null, null);

        Assert.False(hp.HasExplicitMaxHp);
    }

    [Fact]
    public void Resolve_CommitMaxHp_IsExplicit()
    {
        var character = new Character { SystemStats = new Dnd5eExtension() };

        var hp = BootstrapHpResolver.Resolve(character, 7, 5);

        Assert.True(hp.HasExplicitMaxHp);
        Assert.Equal(7, hp.ExplicitMaxHp);
        Assert.Equal(5, hp.ExplicitCurrentHp);
    }

    [Fact]
    public void Resolve_StatBlockHp_IsExplicit()
    {
        var character = new Character
        {
            SystemStats = new Dnd5eExtension { StatBlockHp = 7 },
        };

        var hp = BootstrapHpResolver.Resolve(character, null, null);

        Assert.True(hp.HasExplicitMaxHp);
        Assert.Equal(7, hp.ExplicitMaxHp);
    }

    [Fact]
    public void ApplyExplicitHp_PreservesWoundedCurrentHp()
    {
        var character = new Character
        {
            MaxHp = 0,
            CurrentHp = 3,
            SystemStats = new Dnd5eExtension { StatBlockHp = 7 },
        };

        var hp = BootstrapHpResolver.Resolve(character, null, 3);
        BootstrapHpResolver.ApplyExplicitHp(character, hp);

        Assert.Equal(7, character.MaxHp);
        Assert.Equal(3, character.CurrentHp);
    }
}