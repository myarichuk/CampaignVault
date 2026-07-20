using System;
using System.Collections.Generic;
using System.Linq;
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

        var result = EquipSlotRules.FindConflicts(robe, [chainmail]);

        Assert.Empty(result.Items);
    }

    [Fact]
    public void FindConflicts_TwoChainmails_Conflict()
    {
        var chainmail1 = MakeItem("items/chainmail-1", EquipZone.Torso, EquipLayer.Armor);
        var chainmail2 = MakeItem("items/chainmail-2", EquipZone.Torso, EquipLayer.Armor, equipped: false);

        var result = EquipSlotRules.FindConflicts(chainmail2, [chainmail1]);

        var conflict = Assert.Single(result.Items);
        Assert.Equal("items/chainmail-1", conflict.Id);
    }

    [Fact]
    public void FindConflicts_RingCapacityTwo_ThirdRingConflictsWithOneExisting()
    {
        var ring1 = MakeItem("items/ring-1", EquipZone.Ring, EquipLayer.Base);
        var ring2 = MakeItem("items/ring-2", EquipZone.Ring, EquipLayer.Base);
        var ring3 = MakeItem("items/ring-3", EquipZone.Ring, EquipLayer.Base, equipped: false);

        var result = EquipSlotRules.FindConflicts(ring3, [ring1, ring2]);

        // Capacity is 2; two are already worn, so exactly one must be freed for the third.
        Assert.Single(result.Items);
    }

    [Fact]
    public void FindConflicts_RingCapacityTwo_SecondRingFitsWithoutConflict()
    {
        var ring1 = MakeItem("items/ring-1", EquipZone.Ring, EquipLayer.Base);
        var ring2 = MakeItem("items/ring-2", EquipZone.Ring, EquipLayer.Base, equipped: false);

        var result = EquipSlotRules.FindConflicts(ring2, [ring1]);

        Assert.Empty(result.Items);
    }

    [Fact]
    public void FindConflicts_TwoHandedWeapon_BlocksOffHandShield()
    {
        var shield = MakeItem("items/shield", EquipZone.OffHand, EquipLayer.Held);
        var greatsword = MakeItem("items/greatsword", EquipZone.MainHand, EquipLayer.Held, twoHanded: true, equipped: false);

        var result = EquipSlotRules.FindConflicts(greatsword, [shield]);

        var conflict = Assert.Single(result.Items);
        Assert.Equal("items/shield", conflict.Id);
    }

    [Fact]
    public void FindConflicts_ShieldEquip_ConflictsWithAlreadyEquippedTwoHandedWeapon()
    {
        var greatsword = MakeItem("items/greatsword", EquipZone.MainHand, EquipLayer.Held, twoHanded: true);
        var shield = MakeItem("items/shield", EquipZone.OffHand, EquipLayer.Held, equipped: false);

        var result = EquipSlotRules.FindConflicts(shield, [greatsword]);

        var conflict = Assert.Single(result.Items);
        Assert.Equal("items/greatsword", conflict.Id);
    }

    [Fact]
    public void FindConflicts_NotEquippable_ReturnsEmpty()
    {
        var junk = new Item { Id = "items/rock", Name = "Rock", HolderId = "chars/hero" };

        var result = EquipSlotRules.FindConflicts(junk, []);

        Assert.Empty(result.Items);
    }

    [Fact]
    public void FindConflicts_DifferentZones_NoConflict()
    {
        var boots = MakeItem("items/boots", EquipZone.Feet, EquipLayer.Armor);
        var helmet = MakeItem("items/helmet", EquipZone.Head, EquipLayer.Armor, equipped: false);

        var result = EquipSlotRules.FindConflicts(helmet, [boots]);

        Assert.Empty(result.Items);
    }

    // --- StackGroup ---

    [Fact]
    public void FindConflicts_SameZoneLayerDifferentStackGroup_Coexist()
    {
        var pauldronLeft = MakeItem("items/pauldron-left", EquipZone.Torso, EquipLayer.Armor);
        pauldronLeft.StackGroup = "pauldron-left";
        var pauldronRight = MakeItem("items/pauldron-right", EquipZone.Torso, EquipLayer.Armor, equipped: false);
        pauldronRight.StackGroup = "pauldron-right";

        var result = EquipSlotRules.FindConflicts(pauldronRight, [pauldronLeft]);

        Assert.Empty(result.Items);
    }

    [Fact]
    public void FindConflicts_SameZoneLayerSameStackGroup_StillConflicts()
    {
        var pauldron1 = MakeItem("items/pauldron-1", EquipZone.Torso, EquipLayer.Armor);
        pauldron1.StackGroup = "pauldron-left";
        var pauldron2 = MakeItem("items/pauldron-2", EquipZone.Torso, EquipLayer.Armor, equipped: false);
        pauldron2.StackGroup = "pauldron-left";

        var result = EquipSlotRules.FindConflicts(pauldron2, [pauldron1]);

        var conflict = Assert.Single(result.Items);
        Assert.Equal("items/pauldron-1", conflict.Id);
    }

    [Fact]
    public void FindConflicts_NullStackGroupVsTaggedStackGroup_SeparatePools_NoConflict()
    {
        var breastplate = MakeItem("items/breastplate", EquipZone.Torso, EquipLayer.Armor); // StackGroup null
        var pauldron = MakeItem("items/pauldron", EquipZone.Torso, EquipLayer.Armor, equipped: false);
        pauldron.StackGroup = "pauldron-left";

        var result = EquipSlotRules.FindConflicts(pauldron, [breastplate]);

        Assert.Empty(result.Items);
    }

    [Fact]
    public void FindConflicts_CapacityTwoZone_FreesOldestEquippedFirst()
    {
        var older = MakeItem("items/ring-old", EquipZone.Ring, EquipLayer.Base);
        older.LastUpdated = DateTime.UtcNow.AddDays(-5);
        var newer = MakeItem("items/ring-new", EquipZone.Ring, EquipLayer.Base);
        newer.LastUpdated = DateTime.UtcNow.AddDays(-1);
        var incoming = MakeItem("items/ring-incoming", EquipZone.Ring, EquipLayer.Base, equipped: false);

        var result = EquipSlotRules.FindConflicts(incoming, [newer, older]);

        var conflict = Assert.Single(result.Items);
        Assert.Equal("items/ring-old", conflict.Id);
    }

    [Fact]
    public void FindConflicts_StructuredResult_CarriesZoneLayerStackGroupCapacityOccupants()
    {
        var chainmail1 = MakeItem("items/chainmail-1", EquipZone.Torso, EquipLayer.Armor);
        var chainmail2 = MakeItem("items/chainmail-2", EquipZone.Torso, EquipLayer.Armor, equipped: false);

        var result = EquipSlotRules.FindConflicts(chainmail2, [chainmail1]);

        var zoneConflict = Assert.Single(result.Zones);
        Assert.Equal(EquipZone.Torso, zoneConflict.Zone);
        Assert.Equal(EquipLayer.Armor, zoneConflict.Layer);
        Assert.Null(zoneConflict.StackGroup);
        Assert.Equal(1, zoneConflict.Capacity);
        Assert.Equal(1, zoneConflict.Occupied);
        var freed = Assert.Single(zoneConflict.ToFree);
        Assert.Equal("items/chainmail-1", freed.Id);
    }

    [Fact]
    public void FindConflicts_MultiZoneItem_OneZoneConflictEntryPerZone()
    {
        var torsoOccupant = MakeItem("items/torso-occupant", EquipZone.Torso, EquipLayer.Armor);
        var legsOccupant = new Item
        {
            Id = "items/legs-occupant", Name = "items/legs-occupant", HolderId = "chars/hero",
            CoreCategory = ItemCategory.Armor, EquipZones = [EquipZone.Legs], EquipLayer = EquipLayer.Armor, IsEquipped = true,
        };
        var spanningItem = new Item
        {
            Id = "items/bodysuit", Name = "items/bodysuit", HolderId = "chars/hero",
            CoreCategory = ItemCategory.Armor, EquipZones = [EquipZone.Torso, EquipZone.Legs], EquipLayer = EquipLayer.Armor, IsEquipped = false,
        };

        var result = EquipSlotRules.FindConflicts(spanningItem, [torsoOccupant, legsOccupant]);

        Assert.Equal(2, result.Zones.Count);
        Assert.Contains(result.Zones, z => z.Zone == EquipZone.Torso);
        Assert.Contains(result.Zones, z => z.Zone == EquipZone.Legs);
        Assert.Equal(2, result.Items.Count);
    }

    // --- Tag prerequisites / incompatibilities ---

    [Fact]
    public void FindTagIncompatibilities_MissingPrerequisite_ReturnsMissingTagResult()
    {
        var pauldron = MakeItem("items/pauldron", EquipZone.Torso, EquipLayer.Armor, equipped: false);
        pauldron.RequiresEquippedTags = ["chest-armor"];

        var result = EquipSlotRules.FindTagIncompatibilities(pauldron, []);

        var missing = Assert.Single(result.MissingPrerequisiteTags);
        Assert.Equal("chest-armor", missing);
        Assert.Empty(result.Incompatibilities);
        Assert.True(result.HasIssues);
    }

    [Fact]
    public void FindTagIncompatibilities_PrerequisiteSatisfied_ReturnsEmpty()
    {
        var breastplate = MakeItem("items/breastplate", EquipZone.Torso, EquipLayer.Armor);
        breastplate.Tags = ["chest-armor"];
        var pauldron = MakeItem("items/pauldron", EquipZone.Torso, EquipLayer.Armor, equipped: false);
        pauldron.RequiresEquippedTags = ["chest-armor"];

        var result = EquipSlotRules.FindTagIncompatibilities(pauldron, [breastplate]);

        Assert.False(result.HasIssues);
    }

    [Fact]
    public void FindTagIncompatibilities_MultiplePrerequisiteTags_AllRequiredIndependently()
    {
        var breastplate = MakeItem("items/breastplate", EquipZone.Torso, EquipLayer.Armor);
        breastplate.Tags = ["chest-armor"];
        var pauldron = MakeItem("items/pauldron", EquipZone.Torso, EquipLayer.Armor, equipped: false);
        pauldron.RequiresEquippedTags = ["chest-armor", "belt"];

        var result = EquipSlotRules.FindTagIncompatibilities(pauldron, [breastplate]);

        var missing = Assert.Single(result.MissingPrerequisiteTags);
        Assert.Equal("belt", missing);
    }

    [Fact]
    public void FindTagIncompatibilities_IncompatibleTagPresent_ReturnsConflictResult()
    {
        var trousers = MakeItem("items/trousers", EquipZone.Legs, EquipLayer.Base);
        trousers.Tags = ["legwear-outer"];
        var loincloth = MakeItem("items/loincloth", EquipZone.Legs, EquipLayer.Outer, equipped: false);
        loincloth.IncompatibleWithEquippedTags = ["legwear-outer"];

        var result = EquipSlotRules.FindTagIncompatibilities(loincloth, [trousers]);

        var incompat = Assert.Single(result.Incompatibilities);
        Assert.Equal("legwear-outer", incompat.Tag);
        Assert.Equal("items/trousers", incompat.ConflictingItem.Id);
    }

    [Fact]
    public void FindTagIncompatibilities_NoOverlap_ReturnsEmpty()
    {
        var boots = MakeItem("items/boots", EquipZone.Feet, EquipLayer.Armor);
        boots.Tags = ["footwear"];
        var loincloth = MakeItem("items/loincloth", EquipZone.Legs, EquipLayer.Outer, equipped: false);
        loincloth.IncompatibleWithEquippedTags = ["legwear-outer"];

        var result = EquipSlotRules.FindTagIncompatibilities(loincloth, [boots]);

        Assert.False(result.HasIssues);
    }

    [Fact]
    public void FindTagIncompatibilities_BothListsNullOrEmpty_ReturnsEmpty()
    {
        var trousers = MakeItem("items/trousers", EquipZone.Legs, EquipLayer.Base);
        trousers.Tags = ["legwear-outer"];
        var plainItem = MakeItem("items/plain", EquipZone.Torso, EquipLayer.Armor, equipped: false);

        var result = EquipSlotRules.FindTagIncompatibilities(plainItem, [trousers]);

        Assert.False(result.HasIssues);
    }
}
