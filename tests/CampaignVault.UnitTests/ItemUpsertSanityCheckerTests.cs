using System.Collections.Generic;
using CampaignVault.Models;
using Xunit;

namespace CampaignVault.Tests;

public class ItemUpsertSanityCheckerTests
{
    [Fact]
    public void GetNudges_TwoHandedWithoutMainHand_Nudges()
    {
        var request = new ItemUpsertRequest
        {
            Id = "items/greatsword", Name = "Greatsword", Description = "d", HolderId = "chars/hero",
            EquipZones = [EquipZone.OffHand], EquipLayer = EquipLayer.Held, TwoHanded = true,
        };

        var nudges = ItemUpsertSanityChecker.GetNudges(request);

        Assert.Contains(nudges, n => n.Contains("twoHanded"));
    }

    [Fact]
    public void GetNudges_TwoHandedWithMainHand_NoNudge()
    {
        var request = new ItemUpsertRequest
        {
            Id = "items/greatsword", Name = "Greatsword", Description = "d", HolderId = "chars/hero",
            EquipZones = [EquipZone.MainHand], EquipLayer = EquipLayer.Held, TwoHanded = true,
        };

        var nudges = ItemUpsertSanityChecker.GetNudges(request);

        Assert.DoesNotContain(nudges, n => n.Contains("twoHanded"));
    }

    [Fact]
    public void GetNudges_StackGroupWithoutEquipZonesOrLayer_Nudges()
    {
        var request = new ItemUpsertRequest
        {
            Id = "items/junk", Name = "Junk", Description = "d", HolderId = "chars/hero",
            StackGroup = "pauldron-left",
        };

        var nudges = ItemUpsertSanityChecker.GetNudges(request);

        Assert.Contains(nudges, n => n.Contains("StackGroup"));
    }

    [Fact]
    public void GetNudges_StackGroupWithEquipZonesAndLayer_NoNudge()
    {
        var request = new ItemUpsertRequest
        {
            Id = "items/pauldron", Name = "Pauldron", Description = "d", HolderId = "chars/hero",
            EquipZones = [EquipZone.Torso], EquipLayer = EquipLayer.Armor, StackGroup = "pauldron-left",
        };

        var nudges = ItemUpsertSanityChecker.GetNudges(request);

        Assert.DoesNotContain(nudges, n => n.Contains("StackGroup"));
    }

    [Fact]
    public void GetNudges_MainHandPlusBodyZone_Nudges()
    {
        var request = new ItemUpsertRequest
        {
            Id = "items/weird", Name = "Weird", Description = "d", HolderId = "chars/hero",
            EquipZones = [EquipZone.MainHand, EquipZone.Torso], EquipLayer = EquipLayer.Held,
        };

        var nudges = ItemUpsertSanityChecker.GetNudges(request);

        Assert.Contains(nudges, n => n.Contains("MainHand"));
    }

    [Fact]
    public void GetNudges_NormalItem_NoNudges()
    {
        var request = new ItemUpsertRequest
        {
            Id = "items/breastplate", Name = "Breastplate", Description = "d", HolderId = "chars/hero",
            EquipZones = [EquipZone.Torso], EquipLayer = EquipLayer.Armor,
        };

        var nudges = ItemUpsertSanityChecker.GetNudges(request);

        Assert.Empty(nudges);
    }
}
