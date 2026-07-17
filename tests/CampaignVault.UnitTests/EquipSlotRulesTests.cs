using System.Collections.Generic;
using CampaignVault.Models;
using Xunit;

namespace CampaignVault.Tests;

public class EquipSlotRulesTests
{
    private static Item MakeItem(string id, EquipZone zone, EquipLayer layer, bool twoHanded = false, bool equipped = true) =>
        new()
        {
            Id = id,
            Name = id,
            HolderId = "chars/hero",
            CoreCategory = ItemCategory.Armor,
            EquipZones = [zone],
            EquipLayer = layer,
            TwoHanded = twoHanded,
            IsEquipped = equipped,
        };

    [Fact]
    public void FindConflicts_RobeOverChainmail_Coexist()
    {
        var chainmail = MakeItem("items/chainmail", EquipZone.Torso, EquipLayer.Armor);
        var robe = MakeItem("items/robe", EquipZone.Torso, EquipLayer.Outer, equipped: false);

        var conflicts = EquipSlotRules.FindConflicts(robe, [chainmail]);

        Assert.Empty(conflicts);
    }

    [Fact]
    public void FindConflicts_TwoChainmails_Conflict()
    {
        var chainmail1 = MakeItem("items/chainmail-1", EquipZone.Torso, EquipLayer.Armor);
        var chainmail2 = MakeItem("items/chainmail-2", EquipZone.Torso, EquipLayer.Armor, equipped: false);

        var conflicts = EquipSlotRules.FindConflicts(chainmail2, [chainmail1]);

        var conflict = Assert.Single(conflicts);
        Assert.Equal("items/chainmail-1", conflict.Id);
    }

    [Fact]
    public void FindConflicts_RingCapacityTwo_ThirdRingConflictsWithOneExisting()
    {
        var ring1 = MakeItem("items/ring-1", EquipZone.Ring, EquipLayer.Base);
        var ring2 = MakeItem("items/ring-2", EquipZone.Ring, EquipLayer.Base);
        var ring3 = MakeItem("items/ring-3", EquipZone.Ring, EquipLayer.Base, equipped: false);

        var conflicts = EquipSlotRules.FindConflicts(ring3, [ring1, ring2]);

        // Capacity is 2; two are already worn, so exactly one must be freed for the third.
        Assert.Single(conflicts);
    }

    [Fact]
    public void FindConflicts_RingCapacityTwo_SecondRingFitsWithoutConflict()
    {
        var ring1 = MakeItem("items/ring-1", EquipZone.Ring, EquipLayer.Base);
        var ring2 = MakeItem("items/ring-2", EquipZone.Ring, EquipLayer.Base, equipped: false);

        var conflicts = EquipSlotRules.FindConflicts(ring2, [ring1]);

        Assert.Empty(conflicts);
    }

    [Fact]
    public void FindConflicts_TwoHandedWeapon_BlocksOffHandShield()
    {
        var shield = MakeItem("items/shield", EquipZone.OffHand, EquipLayer.Held);
        var greatsword = MakeItem("items/greatsword", EquipZone.MainHand, EquipLayer.Held, twoHanded: true, equipped: false);

        var conflicts = EquipSlotRules.FindConflicts(greatsword, [shield]);

        var conflict = Assert.Single(conflicts);
        Assert.Equal("items/shield", conflict.Id);
    }

    [Fact]
    public void FindConflicts_ShieldEquip_ConflictsWithAlreadyEquippedTwoHandedWeapon()
    {
        var greatsword = MakeItem("items/greatsword", EquipZone.MainHand, EquipLayer.Held, twoHanded: true);
        var shield = MakeItem("items/shield", EquipZone.OffHand, EquipLayer.Held, equipped: false);

        var conflicts = EquipSlotRules.FindConflicts(shield, [greatsword]);

        var conflict = Assert.Single(conflicts);
        Assert.Equal("items/greatsword", conflict.Id);
    }

    [Fact]
    public void FindConflicts_NotEquippable_ReturnsEmpty()
    {
        var junk = new Item { Id = "items/rock", Name = "Rock", HolderId = "chars/hero" };

        var conflicts = EquipSlotRules.FindConflicts(junk, []);

        Assert.Empty(conflicts);
    }

    [Fact]
    public void FindConflicts_DifferentZones_NoConflict()
    {
        var boots = MakeItem("items/boots", EquipZone.Feet, EquipLayer.Armor);
        var helmet = MakeItem("items/helmet", EquipZone.Head, EquipLayer.Armor, equipped: false);

        var conflicts = EquipSlotRules.FindConflicts(helmet, [boots]);

        Assert.Empty(conflicts);
    }
}
