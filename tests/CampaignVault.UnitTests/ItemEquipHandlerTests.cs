using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CampaignVault.Tests;

[Collection("RavenDB")]
public class ItemEquipHandlerTests : IClassFixture<RavenDBFixture>
{
    private readonly RavenDBFixture _fixture;

    public ItemEquipHandlerTests(RavenDBFixture fixture)
    {
        _fixture = fixture;
    }

    private static Character MakeCharacter(string id) => new() { Id = id, Name = id, CampaignName = "equip-test" };

    private static Item MakeArmor(string id, string holderId, EquipZone zone = EquipZone.Torso, EquipLayer layer = EquipLayer.Armor, bool twoHanded = false) =>
        new()
        {
            Id = id,
            Name = id,
            HolderId = holderId,
            CoreCategory = ItemCategory.Armor,
            EquipZones = [zone],
            EquipLayer = layer,
            TwoHanded = twoHanded,
            CampaignName = "equip-test",
        };

    private ChangeContext BuildContext(
        Raven.Client.Documents.Session.IAsyncDocumentSession session,
        Dictionary<string, Character> characters,
        Dictionary<string, Item> items)
    {
        var dispatcher = new WorldChangeDispatcher([], new CampaignDocumentKeys(), NullLogger<WorldChangeDispatcher>.Instance);
        return new ChangeContext(
            sessionForTests: session,
            characters: characters,
            items: items,
            locations: new Dictionary<string, Location>(),
            factions: new Dictionary<string, Faction>(),
            quests: new Dictionary<string, Quest>(),
            logger: NullLogger.Instance,
            summary: [],
            dispatcher: dispatcher,
            campaignName: "equip-test");
    }

    [Fact]
    public async Task ApplyAsync_Success_EquipsUnwornItem()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        var character = MakeCharacter("chars/equip_success");
        var item = MakeArmor("items/equip_success_armor", character.Id);

        await session.StoreAsync(character);
        await session.StoreAsync(item);
        session.Advanced.WaitForIndexesAfterSaveChanges(timeout: TimeSpan.FromSeconds(10), throwOnTimeout: true);
        await session.SaveChangesAsync();

        var context = BuildContext(session,
            new Dictionary<string, Character> { [character.Id] = character },
            new Dictionary<string, Item> { [item.Id] = item });

        var handler = new ItemEquipHandler();
        var change = new ItemEquip { CharacterId = character.Id, ItemId = item.Id };

        var result = await handler.ApplyAsync(change, context);

        Assert.True(result.Success);
        Assert.True(item.IsEquipped);
    }

    [Fact]
    public async Task ApplyAsync_Fails_WhenItemNotCarriedByCharacter()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        var character = MakeCharacter("chars/equip_notcarried");
        var item = MakeArmor("items/equip_notcarried_armor", "chars/someone_else");

        var context = BuildContext(session,
            new Dictionary<string, Character> { [character.Id] = character },
            new Dictionary<string, Item> { [item.Id] = item });

        var handler = new ItemEquipHandler();
        var change = new ItemEquip { CharacterId = character.Id, ItemId = item.Id };

        var result = await handler.ApplyAsync(change, context);

        Assert.False(result.Success);
        Assert.Contains("not carried by", result.Message);
        Assert.False(item.IsEquipped);
    }

    [Fact]
    public async Task ApplyAsync_Fails_CleanlyWhenItemHolderIdIsNull_NoNullReferenceException()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        var character = MakeCharacter("chars/equip_null_holder");
        var item = MakeArmor("items/equip_null_holder_armor", "chars/equip_null_holder");
        item.HolderId = null!; // Freshly-materialized/ground item — HolderId genuinely unset.

        var context = BuildContext(session,
            new Dictionary<string, Character> { [character.Id] = character },
            new Dictionary<string, Item> { [item.Id] = item });

        var handler = new ItemEquipHandler();
        var change = new ItemEquip { CharacterId = character.Id, ItemId = item.Id };

        var result = await handler.ApplyAsync(change, context);

        Assert.False(result.Success);
        Assert.Contains("not carried by", result.Message);
        Assert.False(item.IsEquipped);
    }

    [Fact]
    public async Task Unequip_ApplyAsync_Fails_CleanlyWhenItemHolderIdIsNull_NoNullReferenceException()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        var character = MakeCharacter("chars/unequip_null_holder");
        var item = MakeArmor("items/unequip_null_holder_armor", "chars/unequip_null_holder");
        item.HolderId = null!;
        item.IsEquipped = true;

        var context = BuildContext(session,
            new Dictionary<string, Character> { [character.Id] = character },
            new Dictionary<string, Item> { [item.Id] = item });

        var handler = new ItemUnequipHandler();
        var change = new ItemUnequip { CharacterId = character.Id, ItemId = item.Id };

        var result = await handler.ApplyAsync(change, context);

        Assert.False(result.Success);
        Assert.Contains("not carried by", result.Message);
        Assert.True(item.IsEquipped);
    }

    [Fact]
    public async Task ApplyAsync_Fails_OnConflict_WithoutReplaceConflicts()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        var character = MakeCharacter("chars/equip_conflict");
        var chainmail1 = MakeArmor("items/equip_conflict_chainmail1", character.Id);
        chainmail1.IsEquipped = true;
        var chainmail2 = MakeArmor("items/equip_conflict_chainmail2", character.Id);

        await session.StoreAsync(character);
        await session.StoreAsync(chainmail1);
        await session.StoreAsync(chainmail2);
        session.Advanced.WaitForIndexesAfterSaveChanges(timeout: TimeSpan.FromSeconds(10), throwOnTimeout: true);
        await session.SaveChangesAsync();

        var context = BuildContext(session,
            new Dictionary<string, Character> { [character.Id] = character },
            new Dictionary<string, Item> { [chainmail1.Id] = chainmail1, [chainmail2.Id] = chainmail2 });

        var handler = new ItemEquipHandler();
        var change = new ItemEquip { CharacterId = character.Id, ItemId = chainmail2.Id };

        var result = await handler.ApplyAsync(change, context);

        Assert.False(result.Success);
        Assert.Contains("ENGINE WARNING", result.Message);
        Assert.Contains("slot conflict", result.Message);
        Assert.Contains("Torso/Armor", result.Message);
        Assert.False(chainmail2.IsEquipped);
        Assert.True(chainmail1.IsEquipped);
    }

    [Fact]
    public async Task ApplyAsync_ReplaceConflicts_UnequipsAndEquips()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        var character = MakeCharacter("chars/equip_replace");
        var chainmail1 = MakeArmor("items/equip_replace_chainmail1", character.Id);
        chainmail1.IsEquipped = true;
        var chainmail2 = MakeArmor("items/equip_replace_chainmail2", character.Id);

        await session.StoreAsync(character);
        await session.StoreAsync(chainmail1);
        await session.StoreAsync(chainmail2);
        session.Advanced.WaitForIndexesAfterSaveChanges(timeout: TimeSpan.FromSeconds(10), throwOnTimeout: true);
        await session.SaveChangesAsync();

        var context = BuildContext(session,
            new Dictionary<string, Character> { [character.Id] = character },
            new Dictionary<string, Item> { [chainmail1.Id] = chainmail1, [chainmail2.Id] = chainmail2 });

        var handler = new ItemEquipHandler();
        var change = new ItemEquip { CharacterId = character.Id, ItemId = chainmail2.Id, ReplaceConflicts = true };

        var result = await handler.ApplyAsync(change, context);

        Assert.True(result.Success);
        Assert.True(chainmail2.IsEquipped);
        Assert.False(chainmail1.IsEquipped);
    }

    [Fact]
    public async Task ApplyAsync_Succeeds_WhenOtherTrackedItemHasNullHolderId_NoNullReferenceExceptionDuringConflictScan()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        var character = MakeCharacter("chars/equip_ground_item_in_context");
        var item = MakeArmor("items/equip_ground_item_worn", character.Id);
        var groundItem = MakeArmor("items/equip_ground_item_untouched", character.Id, EquipZone.Feet);
        groundItem.HolderId = null!; // Ground/unheld item tracked in the same batch context.

        await session.StoreAsync(character);
        await session.StoreAsync(item);
        session.Advanced.WaitForIndexesAfterSaveChanges(timeout: TimeSpan.FromSeconds(10), throwOnTimeout: true);
        await session.SaveChangesAsync();

        var context = BuildContext(session,
            new Dictionary<string, Character> { [character.Id] = character },
            new Dictionary<string, Item> { [item.Id] = item, [groundItem.Id] = groundItem });

        var handler = new ItemEquipHandler();
        var change = new ItemEquip { CharacterId = character.Id, ItemId = item.Id };

        var result = await handler.ApplyAsync(change, context);

        Assert.True(result.Success);
        Assert.True(item.IsEquipped);
    }

    [Fact]
    public async Task ApplyAsync_RobeOverChainmail_BothEquippedNoConflict()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        var character = MakeCharacter("chars/equip_layering");
        var chainmail = MakeArmor("items/equip_layering_chainmail", character.Id, EquipZone.Torso, EquipLayer.Armor);
        chainmail.IsEquipped = true;
        var robe = MakeArmor("items/equip_layering_robe", character.Id, EquipZone.Torso, EquipLayer.Outer);
        robe.Properties["stacksWithArmor"] = "true";

        await session.StoreAsync(character);
        await session.StoreAsync(chainmail);
        await session.StoreAsync(robe);
        session.Advanced.WaitForIndexesAfterSaveChanges(timeout: TimeSpan.FromSeconds(10), throwOnTimeout: true);
        await session.SaveChangesAsync();

        var context = BuildContext(session,
            new Dictionary<string, Character> { [character.Id] = character },
            new Dictionary<string, Item> { [chainmail.Id] = chainmail, [robe.Id] = robe });

        var handler = new ItemEquipHandler();
        var change = new ItemEquip { CharacterId = character.Id, ItemId = robe.Id };

        var result = await handler.ApplyAsync(change, context);

        Assert.True(result.Success);
        Assert.True(robe.IsEquipped);
        Assert.True(chainmail.IsEquipped);
    }

    [Fact]
    public async Task Unequip_ApplyAsync_Success_ClearsEquippedFlag_ItemStaysCarried()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        var character = MakeCharacter("chars/unequip_success");
        var item = MakeArmor("items/unequip_success_armor", character.Id);
        item.IsEquipped = true;

        var context = BuildContext(session,
            new Dictionary<string, Character> { [character.Id] = character },
            new Dictionary<string, Item> { [item.Id] = item });

        var handler = new ItemUnequipHandler();
        var change = new ItemUnequip { CharacterId = character.Id, ItemId = item.Id };

        var result = await handler.ApplyAsync(change, context);

        Assert.True(result.Success);
        Assert.False(item.IsEquipped);
        Assert.Equal(character.Id, item.HolderId);
    }

    [Fact]
    public async Task ApplyAsync_StackGroupedPauldron_EquipsAlongsideUngroupedBreastplate()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        var character = MakeCharacter("chars/equip_stackgroup");
        var breastplate = MakeArmor("items/equip_stackgroup_breastplate", character.Id);
        breastplate.IsEquipped = true;
        var pauldron = MakeArmor("items/equip_stackgroup_pauldron", character.Id);
        pauldron.StackGroup = "pauldron-left";

        await session.StoreAsync(character);
        await session.StoreAsync(breastplate);
        await session.StoreAsync(pauldron);
        session.Advanced.WaitForIndexesAfterSaveChanges(timeout: TimeSpan.FromSeconds(10), throwOnTimeout: true);
        await session.SaveChangesAsync();

        var context = BuildContext(session,
            new Dictionary<string, Character> { [character.Id] = character },
            new Dictionary<string, Item> { [breastplate.Id] = breastplate, [pauldron.Id] = pauldron });

        var handler = new ItemEquipHandler();
        var change = new ItemEquip { CharacterId = character.Id, ItemId = pauldron.Id };

        var result = await handler.ApplyAsync(change, context);

        Assert.True(result.Success);
        Assert.True(pauldron.IsEquipped);
        Assert.True(breastplate.IsEquipped);
    }

    [Fact]
    public async Task ApplyAsync_SameStackGroup_SecondItemHardFailsWithStackGroupWording()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        var character = MakeCharacter("chars/equip_stackgroup_conflict");
        var pauldron1 = MakeArmor("items/equip_stackgroup_conflict_1", character.Id);
        pauldron1.StackGroup = "pauldron-left";
        pauldron1.IsEquipped = true;
        var pauldron2 = MakeArmor("items/equip_stackgroup_conflict_2", character.Id);
        pauldron2.StackGroup = "pauldron-left";

        await session.StoreAsync(character);
        await session.StoreAsync(pauldron1);
        await session.StoreAsync(pauldron2);
        session.Advanced.WaitForIndexesAfterSaveChanges(timeout: TimeSpan.FromSeconds(10), throwOnTimeout: true);
        await session.SaveChangesAsync();

        var context = BuildContext(session,
            new Dictionary<string, Character> { [character.Id] = character },
            new Dictionary<string, Item> { [pauldron1.Id] = pauldron1, [pauldron2.Id] = pauldron2 });

        var handler = new ItemEquipHandler();
        var change = new ItemEquip { CharacterId = character.Id, ItemId = pauldron2.Id };

        var result = await handler.ApplyAsync(change, context);

        Assert.False(result.Success);
        Assert.Contains("StackGroup 'pauldron-left'", result.Message);
        Assert.False(pauldron2.IsEquipped);
    }

    [Fact]
    public async Task ApplyAsync_ConflictWithLaterUnequipInSameBatch_SurfacesReorderNudge()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        var character = MakeCharacter("chars/equip_reorder");
        var chainmail1 = MakeArmor("items/equip_reorder_chainmail1", character.Id);
        chainmail1.IsEquipped = true;
        var chainmail2 = MakeArmor("items/equip_reorder_chainmail2", character.Id);

        await session.StoreAsync(character);
        await session.StoreAsync(chainmail1);
        await session.StoreAsync(chainmail2);
        session.Advanced.WaitForIndexesAfterSaveChanges(timeout: TimeSpan.FromSeconds(10), throwOnTimeout: true);
        await session.SaveChangesAsync();

        var context = BuildContext(session,
            new Dictionary<string, Character> { [character.Id] = character },
            new Dictionary<string, Item> { [chainmail1.Id] = chainmail1, [chainmail2.Id] = chainmail2 });

        var equipChange = new ItemEquip { CharacterId = character.Id, ItemId = chainmail2.Id };
        var laterUnequip = new ItemUnequip { CharacterId = character.Id, ItemId = chainmail1.Id };
        context.Batch = [equipChange, laterUnequip];
        context.BatchIndex = 0;

        var handler = new ItemEquipHandler();
        var result = await handler.ApplyAsync(equipChange, context);

        Assert.False(result.Success);
        Assert.Contains("this batch also unequips it later", result.Message);
        Assert.Contains("reorder", result.Message);
    }

    [Fact]
    public async Task ApplyAsync_MissingRequiredTag_HardFailsWithNamedPrerequisite()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        var character = MakeCharacter("chars/equip_missing_prereq");
        var pauldron = MakeArmor("items/equip_missing_prereq_pauldron", character.Id);
        pauldron.RequiresEquippedTags = ["chest-armor"];

        await session.StoreAsync(character);
        await session.StoreAsync(pauldron);
        session.Advanced.WaitForIndexesAfterSaveChanges(timeout: TimeSpan.FromSeconds(10), throwOnTimeout: true);
        await session.SaveChangesAsync();

        var context = BuildContext(session,
            new Dictionary<string, Character> { [character.Id] = character },
            new Dictionary<string, Item> { [pauldron.Id] = pauldron });

        var handler = new ItemEquipHandler();
        var change = new ItemEquip { CharacterId = character.Id, ItemId = pauldron.Id };

        var result = await handler.ApplyAsync(change, context);

        Assert.False(result.Success);
        Assert.Contains("ENGINE WARNING", result.Message);
        Assert.Contains("chest-armor", result.Message);
        Assert.False(pauldron.IsEquipped);
    }

    [Fact]
    public async Task ApplyAsync_IncompatibleTagPresent_HardFailsNoAutoResolve()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        var character = MakeCharacter("chars/equip_incompatible_tag");
        var trousers = MakeArmor("items/equip_incompatible_tag_trousers", character.Id, EquipZone.Legs, EquipLayer.Base);
        trousers.Tags = ["legwear-outer"];
        trousers.IsEquipped = true;
        var loincloth = MakeArmor("items/equip_incompatible_tag_loincloth", character.Id, EquipZone.Legs, EquipLayer.Outer);
        loincloth.IncompatibleWithEquippedTags = ["legwear-outer"];

        await session.StoreAsync(character);
        await session.StoreAsync(trousers);
        await session.StoreAsync(loincloth);
        session.Advanced.WaitForIndexesAfterSaveChanges(timeout: TimeSpan.FromSeconds(10), throwOnTimeout: true);
        await session.SaveChangesAsync();

        var context = BuildContext(session,
            new Dictionary<string, Character> { [character.Id] = character },
            new Dictionary<string, Item> { [trousers.Id] = trousers, [loincloth.Id] = loincloth });

        var handler = new ItemEquipHandler();
        // replaceConflicts:true must NOT bypass a tag-based incompatibility.
        var change = new ItemEquip { CharacterId = character.Id, ItemId = loincloth.Id, ReplaceConflicts = true };

        var result = await handler.ApplyAsync(change, context);

        Assert.False(result.Success);
        Assert.Contains("legwear-outer", result.Message);
        Assert.False(loincloth.IsEquipped);
        Assert.True(trousers.IsEquipped);
    }
}
