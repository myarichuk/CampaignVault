using System;
using System.IO;
using CampaignVault.Models;
using CampaignVault.Services;
using Xunit;

namespace CampaignVault.Tests;

public class WeaponDefinitionProviderTests
{
    private static readonly WeaponDefinitionProvider Provider = new(
        Path.Combine(Path.GetTempPath(), "cv_weapondef_test_" + Guid.NewGuid()),
        typeof(WeaponDefinitionProvider).Assembly);

    [Fact]
    public void Provider_LoadsFallout2d20Weapons_FromEmbeddedResources()
    {
        var weapons = Provider.GetWeaponsForSystem(RulesetSystem.Fallout2d20);

        Assert.Equal(6, weapons.Count);
        Assert.True(weapons.ContainsKey("10mm Pistol"));
        Assert.True(weapons.ContainsKey("Laser Rifle"));
    }

    [Fact]
    public void TryGet_10mmPistol_HasExpectedDamageAndSkill()
    {
        var found = Provider.TryGet(RulesetSystem.Fallout2d20, "10mm Pistol", out var weapon);

        Assert.True(found);
        Assert.NotNull(weapon);
        Assert.Equal("2d6", weapon.Damage);
        Assert.Equal("Small Guns", weapon.Skill);
        Assert.Equal("Physical", weapon.DamageType);
    }

    [Fact]
    public void GetWeaponsForSystem_Dnd5e_ReturnsEmpty()
    {
        var weapons = Provider.GetWeaponsForSystem(RulesetSystem.Dnd5e);

        Assert.Empty(weapons);
    }
}
