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
        Assert.Contains("conflicts with already-equipped", result.Message);
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
}
