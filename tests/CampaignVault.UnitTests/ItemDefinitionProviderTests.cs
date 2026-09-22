using System;
using System.IO;
using CampaignVault.Data.Templates;
using CampaignVault.Models;
using CampaignVault.Services;
using Xunit;

namespace CampaignVault.Tests;

public class ItemDefinitionProviderTests
{
    private static readonly ItemDefinitionProvider Provider = new(
        Path.Combine(Path.GetTempPath(), "cv_itemdef_test_" + Guid.NewGuid()),
        typeof(ItemDefinitionProvider).Assembly);

    [Fact]
    public void GetItemsForSystem_Dnd5e_ReturnsSeedItems()
    {
        var items = Provider.GetItemsForSystem(RulesetSystem.Dnd5e);

        Assert.NotEmpty(items);
        Assert.Contains(items.Keys, k => k == "longsword");
        Assert.Contains(items.Keys, k => k == "climbers_kit");
    }

    [Fact]
    public void GetItemsForSystem_PropertiesBag_RoundTripsScalarValues()
    {
        var items = Provider.GetItemsForSystem(RulesetSystem.Dnd5e);
        var longsword = items["longsword"];

        Assert.Equal(ItemCategories.Weapon, longsword.Category);
        Assert.Contains("martial", longsword.Tags);
        Assert.Equal("1d8", longsword.Properties["damage"]?.ToString());
        Assert.Equal("slashing", longsword.Properties["damageType"]?.ToString());
    }

    [Fact]
    public void GetItemsForSystem_NonWeaponCategory_IsNotRestrictedToWeaponsOrArmor()
    {
        var items = Provider.GetItemsForSystem(RulesetSystem.Dnd5e);
        var kit = items["climbers_kit"];

        Assert.Equal(ItemCategories.Tool, kit.Category);
        Assert.Contains("mountaineering", kit.Tags);
    }

    [Fact]
    public void QueryItems_FiltersByCategoryAndTag()
    {
        var weapons = Provider.QueryItems(RulesetSystem.Dnd5e, category: ItemCategories.Weapon);
        Assert.Contains(weapons, i => i.Name == "longsword");
        Assert.DoesNotContain(weapons, i => i.Name == "climbers_kit");

        var mountaineering = Provider.QueryItems(RulesetSystem.Dnd5e, tag: "mountaineering");
        Assert.Contains(mountaineering, i => i.Name == "climbers_kit");
        Assert.DoesNotContain(mountaineering, i => i.Name == "longsword");
    }

    [Fact]
    public void QueryItems_FiltersByNameQuery()
    {
        var results = Provider.QueryItems(RulesetSystem.Dnd5e, nameQuery: "sword");
        Assert.Contains(results, i => i.Name == "longsword");
        Assert.DoesNotContain(results, i => i.Name == "climbers_kit");
    }

    [Fact]
    public void Merge_ChildOmitsEquipZones_InheritsParent()
    {
        var parent = new ItemDefinition { Name = "base_armor", System = RulesetSystem.Dnd5e, EquipZones = [EquipZones.Torso], EquipLayer = EquipLayers.Armor };
        var child = new ItemDefinition { Name = "fancy_armor", Inherits = ["base_armor"] };

        var merged = ItemDefinition.Merge(child, parent);

        Assert.Equal([EquipZones.Torso], merged.EquipZones);
        Assert.Equal(EquipLayers.Armor, merged.EquipLayer);
    }

    [Fact]
    public void Merge_ChildSetsEquipZones_ChildWins()
    {
        var parent = new ItemDefinition { Name = "base_armor", System = RulesetSystem.Dnd5e, EquipZones = [EquipZones.Torso], EquipLayer = EquipLayers.Armor };
        var child = new ItemDefinition { Name = "helmet_variant", Inherits = ["base_armor"], EquipZones = [EquipZones.Head] };

        var merged = ItemDefinition.Merge(child, parent);

        Assert.Equal([EquipZones.Head], merged.EquipZones);
    }
}
