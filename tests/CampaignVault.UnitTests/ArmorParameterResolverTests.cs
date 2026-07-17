using System.Collections.Generic;
using CampaignVault.Models;
using CampaignVault.Rulesets;
using Xunit;

namespace CampaignVault.Tests;

public class ArmorParameterResolverTests
{
    private static Item MakeEquipped(
        string id, EquipZone zone, EquipLayer layer,
        int? acBonus = null, string? armorType = null, bool stacksWithArmor = false, float? warmth = null) =>
        new()
        {
            Id = id,
            Name = id,
            HolderId = "chars/hero",
            CoreCategory = ItemCategory.Armor,
            EquipZones = [zone],
            EquipLayer = layer,
            IsEquipped = true,
            Properties = BuildProperties(acBonus, armorType, stacksWithArmor, warmth),
        };

    private static Dictionary<string, object> BuildProperties(int? acBonus, string? armorType, bool stacksWithArmor, float? warmth)
    {
        var props = new Dictionary<string, object>();
        if (acBonus.HasValue) props["acBonus"] = acBonus.Value.ToString();
        if (armorType != null) props["armorType"] = armorType;
        if (stacksWithArmor) props["stacksWithArmor"] = "true";
        if (warmth.HasValue) props["warmth"] = warmth.Value.ToString();
        return props;
    }

    private static Character MakeCharacter(Dnd5eExtension stats) =>
        new() { Id = "chars/hero", Name = "Hero", SystemStats = stats };

    [Fact]
    public void Apply_SingleArmor_5e_AddsAcBonus()
    {
        var stats = new Dnd5eExtension { Dexterity = 14 }; // +2 mod
        var character = MakeCharacter(stats);
        var chainmail = MakeEquipped("items/chainmail", EquipZone.Torso, EquipLayer.Armor, acBonus: 4, armorType: "medium");

        ArmorParameterResolver.Apply(character, [chainmail]);

        // 10 + min(dex=2, cap=2) + 4 = 16
        Assert.Equal(16, stats.ArmorClass);
    }

    [Fact]
    public void Apply_ArmorPlusShield_Additive()
    {
        var stats = new Dnd5eExtension { Dexterity = 10 }; // +0 mod
        var character = MakeCharacter(stats);
        var armor = MakeEquipped("items/armor", EquipZone.Torso, EquipLayer.Armor, acBonus: 3, armorType: "heavy");
        var shield = MakeEquipped("items/shield", EquipZone.OffHand, EquipLayer.Held, acBonus: 2);

        ArmorParameterResolver.Apply(character, [armor, shield]);

        // 10 + min(0, cap=0) + 3 (armor) + 2 (shield, always adds) = 15
        Assert.Equal(15, stats.ArmorClass);
    }

    [Fact]
    public void Apply_NonStackingRobe_Ignored()
    {
        var stats = new Dnd5eExtension { Dexterity = 10 };
        var character = MakeCharacter(stats);
        var armor = MakeEquipped("items/armor", EquipZone.Torso, EquipLayer.Armor, acBonus: 3, armorType: "heavy");
        var robe = MakeEquipped("items/robe", EquipZone.Torso, EquipLayer.Outer, acBonus: 5, stacksWithArmor: false);

        ArmorParameterResolver.Apply(character, [armor, robe]);

        // Robe has no stacksWithArmor=true, so its acBonus is ignored: 10 + 0 + 3 = 13
        Assert.Equal(13, stats.ArmorClass);
    }

    [Fact]
    public void Apply_StackingRobe_Additive()
    {
        var stats = new Dnd5eExtension { Dexterity = 10 };
        var character = MakeCharacter(stats);
        var armor = MakeEquipped("items/armor", EquipZone.Torso, EquipLayer.Armor, acBonus: 3, armorType: "heavy");
        var robe = MakeEquipped("items/robe", EquipZone.Torso, EquipLayer.Outer, acBonus: 5, stacksWithArmor: true);

        ArmorParameterResolver.Apply(character, [armor, robe]);

        // Robe explicitly stacks: 10 + 0 + 3 + 5 = 18
        Assert.Equal(18, stats.ArmorClass);
    }

    [Fact]
    public void Apply_5e_MediumArmor_CapsDexAtTwo()
    {
        var stats = new Dnd5eExtension { Dexterity = 20 }; // +5 mod
        var character = MakeCharacter(stats);
        var armor = MakeEquipped("items/armor", EquipZone.Torso, EquipLayer.Armor, acBonus: 4, armorType: "medium");

        ArmorParameterResolver.Apply(character, [armor]);

        // 10 + min(5, 2) + 4 = 16
        Assert.Equal(16, stats.ArmorClass);
    }

    [Fact]
    public void Apply_5e_HeavyArmor_CapsDexAtZero()
    {
        var stats = new Dnd5eExtension { Dexterity = 20 }; // +5 mod
        var character = MakeCharacter(stats);
        var armor = MakeEquipped("items/armor", EquipZone.Torso, EquipLayer.Armor, acBonus: 6, armorType: "heavy");

        ArmorParameterResolver.Apply(character, [armor]);

        // 10 + min(5, 0) + 6 = 16
        Assert.Equal(16, stats.ArmorClass);
    }

    [Fact]
    public void Apply_5e_LightArmor_DexUncapped()
    {
        var stats = new Dnd5eExtension { Dexterity = 20 }; // +5 mod
        var character = MakeCharacter(stats);
        var armor = MakeEquipped("items/armor", EquipZone.Torso, EquipLayer.Armor, acBonus: 1, armorType: "light");

        ArmorParameterResolver.Apply(character, [armor]);

        // 10 + 5 (uncapped) + 1 = 16
        Assert.Equal(16, stats.ArmorClass);
    }

    [Fact]
    public void Apply_Pf2eFormula_UsesLevelAndProficiency()
    {
        var stats = new Pf2eExtension
        {
            DexterityMod = 3,
            Level = 5,
            AcProficiency = Pf2eProficiencyRank.Expert,
        };
        var character = new Character { Id = "chars/hero", Name = "Hero", SystemStats = stats };
        var armor = new Item
        {
            Id = "items/armor", Name = "Armor", HolderId = "chars/hero",
            EquipZones = [EquipZone.Torso], EquipLayer = EquipLayer.Armor, IsEquipped = true,
            Properties = new Dictionary<string, object> { ["acBonus"] = "2" },
        };

        ArmorParameterResolver.Apply(character, [armor]);

        // 10 + dexMod(3) + (level 5 + expert 4) + acBonus 2 = 24
        Assert.Equal(24, stats.ArmorClass);
    }

    [Fact]
    public void Apply_Warmth_SumsAcrossAllEquippedItems()
    {
        var stats = new Dnd5eExtension { Dexterity = 10 };
        var character = MakeCharacter(stats);
        var armor = MakeEquipped("items/armor", EquipZone.Torso, EquipLayer.Armor, acBonus: 2, armorType: "medium", warmth: 3f);
        var cloak = MakeEquipped("items/cloak", EquipZone.Back, EquipLayer.Outer, warmth: 4f);

        ArmorParameterResolver.Apply(character, [armor, cloak]);

        Assert.Equal(7f, character.SystemStats.WarmthRating);
    }

    [Fact]
    public void Apply_NoEquippedItems_UnarmoredBaseline()
    {
        var stats = new Dnd5eExtension { Dexterity = 14 };
        var character = MakeCharacter(stats);

        ArmorParameterResolver.Apply(character, []);

        Assert.Equal(12, stats.ArmorClass);
        Assert.Equal(0f, character.SystemStats.WarmthRating);
    }
}
