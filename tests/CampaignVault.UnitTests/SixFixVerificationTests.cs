using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;
using CampaignVault.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>
/// Scratch verification tests for the 2026-07-22 fix batch (ItemDetail.Participants persistence,
/// Character.LastUpdated bumps, ItemDetailSummary on party views, transfer auto-unequip message,
/// and the Armor/Held Properties-key sanity nudge). Candidate for deletion once the caller has
/// reviewed the results.
/// </summary>
[Collection("RavenDB")]
public class SixFixVerificationTests : IClassFixture<RavenDBFixture>
{
    private readonly RavenDBFixture _fixture;

    public SixFixVerificationTests(RavenDBFixture fixture)
    {
        _fixture = fixture;
    }

    private sealed class StubEmbeddingService : ILocalEmbeddingService
    {
        public Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken ct = default)
            => Task.FromResult(new float[] { 1f, 0f });
    }

    private static ChangeContext BuildContext(
        Raven.Client.Documents.Session.IAsyncDocumentSession session,
        WorldChangeDispatcher dispatcher,
        string campaignName,
        Dictionary<string, Character>? characters = null,
        Dictionary<string, Item>? items = null,
        int currentDay = 10) =>
        new(
            session,
            characters ?? new Dictionary<string, Character>(),
            items ?? new Dictionary<string, Item>(),
            new Dictionary<string, Location>(),
            new Dictionary<string, Faction>(),
            new Dictionary<string, Quest>(),
            NullLogger.Instance,
            () => Task.FromResult(new CampaignTime { TotalDaysElapsed = currentDay }),
            () => Task.FromResult(new Dictionary<string, string>()),
            _ => Task.CompletedTask,
            [],
            dispatcher,
            campaignName: campaignName);

    // ---- Item 1: ItemDetail.Participants persists on the detail itself ----

    [Fact]
    public async Task Item1_UpsertItemDetail_PersistsParticipantsOnDetail()
    {
        const string campaign = "fix-verify-participants";
        using var session = _fixture.Store.OpenAsyncSession();

        var witness = new Character { Id = "chars/fixverify_witness", Name = "Witness", CampaignName = campaign };
        var item = new Item { Id = "items/fixverify_participants", Name = "Urn", Description = "An urn.", HolderId = "locations/tomb", CampaignName = campaign };
        await session.StoreAsync(witness);
        await session.StoreAsync(item);
        await session.SaveChangesAsync();

        var handler = new ItemUpdateHandler(new StubEmbeddingService());
        var dispatcher = new WorldChangeDispatcher([handler, new KnowledgeUpdateHandler()], new CampaignDocumentKeys(), NullLogger<WorldChangeDispatcher>.Instance);
        var context = BuildContext(session, dispatcher, campaign);

        var change = new ItemUpdate
        {
            ItemId = item.Id,
            UpsertItemDetail = new ItemDetailUpsertRequest
            {
                Name = "Cracked seal",
                Description = "The wax seal is cracked.",
                Participants = [new ItemDetailParticipant { Id = witness.Id, Role = ItemDetailParticipantRole.Witnessed }],
            },
        };

        var result = await handler.ApplyAsync(change, context);

        Assert.True(result.Success);
        var detail = Assert.Single(item.ItemDetails);
        Assert.NotNull(detail.Participants);
        var participant = Assert.Single(detail.Participants!);
        Assert.Equal(witness.Id, participant.Id);
        Assert.Equal(ItemDetailParticipantRole.Witnessed, participant.Role);
    }

    // ---- Item 2: Character.LastUpdated bumps ----

    [Fact]
    public async Task Item2_ArmorParameterResolver_ApplyAsync_BumpsLastUpdated()
    {
        const string campaign = "fix-verify-lastupdated-armor";
        using var session = _fixture.Store.OpenAsyncSession();

        var oldStamp = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var character = new Character { Id = "chars/fixverify_armor", Name = "Hero", CampaignName = campaign, LastUpdated = oldStamp };
        await session.StoreAsync(character);
        await session.SaveChangesAsync();

        var dispatcher = new WorldChangeDispatcher([new KnowledgeUpdateHandler()], new CampaignDocumentKeys(), NullLogger<WorldChangeDispatcher>.Instance);
        var context = BuildContext(session, dispatcher, campaign);

        await CampaignVault.Rulesets.ArmorParameterResolver.ApplyAsync(character, context);

        Assert.True(character.LastUpdated > oldStamp);
    }

    [Fact]
    public async Task Item2_KnowledgeUpdateHandler_ApplyAsync_BumpsLastUpdated()
    {
        const string campaign = "fix-verify-lastupdated-knowledge";
        using var session = _fixture.Store.OpenAsyncSession();

        var oldStamp = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var character = new Character { Id = "chars/fixverify_knowledge", Name = "Sage", CampaignName = campaign, LastUpdated = oldStamp };
        await session.StoreAsync(character);
        await session.SaveChangesAsync();

        var handler = new KnowledgeUpdateHandler();
        var dispatcher = new WorldChangeDispatcher([handler], new CampaignDocumentKeys(), NullLogger<WorldChangeDispatcher>.Instance);
        var context = BuildContext(session, dispatcher, campaign);

        var change = new KnowledgeUpdate { CharacterId = character.Id, Topic = "the-old-well", Details = "It's cursed.", CreateMemory = true };
        var result = await handler.ApplyAsync(change, context);

        Assert.True(result.Success);
        Assert.True(character.LastUpdated > oldStamp);
    }

    // ---- Item 3: ItemSummaryView.ItemDetails ----

    [Fact]
    public void Item3_ItemSummaryView_From_PopulatesNonRetiredDetails()
    {
        var item = new Item
        {
            Id = "items/fixverify_summary",
            Name = "Lantern",
            Description = "d",
            HolderId = "chars/hero",
            ItemDetails =
            [
                new ItemDetail { Id = "d1", Name = "Dent", Description = "dented", Status = "Noted" },
                new ItemDetail { Id = "d2", Name = "Old crack", Description = "cracked", IsRetired = true, Status = "Retired" },
            ],
        };

        var view = ItemSummaryView.From(item);

        Assert.NotNull(view.ItemDetails);
        var detail = Assert.Single(view.ItemDetails!);
        Assert.Equal("d1", detail.Id);
        Assert.Equal("Dent", detail.Name);
        Assert.Equal("Noted", detail.Status);
    }

    [Fact]
    public void Item3_ItemSummaryView_From_ReturnsNullWhenNoActiveDetails()
    {
        var itemWithRetiredOnly = new Item
        {
            Id = "items/fixverify_summary_retired",
            Name = "Old cloak",
            Description = "d",
            HolderId = "chars/hero",
            ItemDetails = [new ItemDetail { Id = "d1", Name = "Old stain", Description = "stained", IsRetired = true }],
        };
        var itemWithNoDetails = new Item { Id = "items/fixverify_summary_none", Name = "Plain rock", Description = "d", HolderId = "chars/hero" };

        Assert.Null(ItemSummaryView.From(itemWithRetiredOnly).ItemDetails);
        Assert.Null(ItemSummaryView.From(itemWithNoDetails).ItemDetails);
    }

    [Fact]
    public async Task Item3_GetParty_SurfacesItemDetailSummariesForHeldItems()
    {
        var repo = _fixture.CreateRepository();
        var tools = TestCampaignToolsFactory.Create(_fixture, repository: repo);
        var campaignName = "fixverify-getparty-" + Guid.NewGuid();

        var pcId = "chars/fixverify-pc-" + Guid.NewGuid();
        var equippedItemId = "items/fixverify-equipped-" + Guid.NewGuid();
        var carriedItemId = "items/fixverify-carried-" + Guid.NewGuid();

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            var configId = new CampaignDocumentKeys().Config(campaignName);
            await session.StoreAsync(new CampaignConfig { Id = configId });

            await repo.UpsertCharacterAsync(_fixture.CreateCampaignSession(session, campaignName), new CharacterUpsertRequest
            {
                Id = pcId,
                Name = "Detail Bearer",
                IsPc = true,
                KeepAlive = true,
            });

            await repo.UpsertItemAsync(_fixture.CreateCampaignSession(session, campaignName), new ItemUpsertRequest
            {
                Id = equippedItemId,
                Name = "Notched Blade",
                Description = "A sword.",
                HolderId = pcId,
                IsEquipped = true,
                ItemDetails =
                [
                    new ItemDetailUpsertRequest { Name = "Notch", Description = "A notch near the hilt." },
                    new ItemDetailUpsertRequest { Name = "Old rust", Description = "Long since cleaned." },
                ],
            });

            await repo.UpsertItemAsync(_fixture.CreateCampaignSession(session, campaignName), new ItemUpsertRequest
            {
                Id = carriedItemId,
                Name = "Plain Pouch",
                Description = "A pouch.",
                HolderId = pcId,
                IsEquipped = false,
            });

            await session.SaveChangesAsync();
        }

        // Retire one of the two details on the equipped item so we can assert it's excluded.
        using (var session = _fixture.Store.OpenAsyncSession())
        {
            var item = await session.LoadAsync<Item>(equippedItemId);
            var retired = item.ItemDetails.Single(d => d.Name == "Old rust");
            retired.IsRetired = true;
            retired.Status = "Retired";
            await session.SaveChangesAsync();
        }

        var indexWaitStart = DateTime.UtcNow;
        while ((DateTime.UtcNow - indexWaitStart).TotalSeconds < 10)
        {
            var stats = _fixture.Store.Maintenance.Send(new Raven.Client.Documents.Operations.GetStatisticsOperation());
            if (stats.Indexes.Any(x => x.Name == "Character/Search" && x.IsStale == false))
            {
                break;
            }
            await Task.Delay(100);
        }

        var result = await tools.GetParty(campaignName);

        Assert.True(result.Success);
        var member = Assert.Single(result.Data!, m => m.Id == pcId);

        var equippedSummary = Assert.Single(member.Equipped!, i => i.Id == equippedItemId);
        var detailSummary = Assert.Single(equippedSummary.ItemDetails!);
        Assert.Equal("Notch", detailSummary.Name);

        var carriedSummary = Assert.Single(member.Carried!, i => i.Id == carriedItemId);
        Assert.Null(carriedSummary.ItemDetails);
    }

    // ---- Item 4: transfer auto-unequip message ----

    [Fact]
    public async Task Item4_ItemTransferHandler_AppendsAutoUnequipNote_WhenTransferUnequips()
    {
        const string campaign = "fix-verify-transfer-note";
        using var session = _fixture.Store.OpenAsyncSession();

        var fromChar = new Character { Id = "chars/fixverify_from", Name = "From", CampaignName = campaign };
        var toChar = new Character { Id = "chars/fixverify_to", Name = "To", CampaignName = campaign };
        var item = new Item
        {
            Id = "items/fixverify_transfer",
            Name = "Ring",
            Description = "d",
            HolderId = fromChar.Id,
            CampaignName = campaign,
            IsEquipped = true,
        };
        await session.StoreAsync(fromChar);
        await session.StoreAsync(toChar);
        await session.StoreAsync(item);
        await session.SaveChangesAsync();

        var handler = new ItemTransferHandler();
        var dispatcher = new WorldChangeDispatcher([handler], new CampaignDocumentKeys(), NullLogger<WorldChangeDispatcher>.Instance);
        var characters = new Dictionary<string, Character> { [fromChar.Id] = fromChar, [toChar.Id] = toChar };
        var items = new Dictionary<string, Item> { [item.Id] = item };
        var summary = new List<string>();
        var context = new ChangeContext(
            session,
            characters,
            items,
            new Dictionary<string, Location>(),
            new Dictionary<string, Faction>(),
            new Dictionary<string, Quest>(),
            NullLogger.Instance,
            () => Task.FromResult(new CampaignTime()),
            () => Task.FromResult(new Dictionary<string, string>()),
            _ => Task.CompletedTask,
            summary,
            dispatcher,
            campaignName: campaign);

        var change = new ItemTransfer { ItemId = item.Id, ToHolderId = toChar.Id };
        var result = await handler.ApplyAsync(change, context);

        Assert.True(result.Success);
        Assert.False(item.IsEquipped);
        Assert.Contains(summary, m => m == $"Item {item.Id} moved to {toChar.Id} (auto-unequipped from {fromChar.Id})");
    }

    [Fact]
    public async Task Item4_ItemTransferHandler_NoNote_WhenNotEquipped()
    {
        const string campaign = "fix-verify-transfer-note-none";
        using var session = _fixture.Store.OpenAsyncSession();

        var fromChar = new Character { Id = "chars/fixverify_from2", Name = "From2", CampaignName = campaign };
        var toChar = new Character { Id = "chars/fixverify_to2", Name = "To2", CampaignName = campaign };
        var item = new Item { Id = "items/fixverify_transfer2", Name = "Coin", Description = "d", HolderId = fromChar.Id, CampaignName = campaign, IsEquipped = false };
        await session.StoreAsync(fromChar);
        await session.StoreAsync(toChar);
        await session.StoreAsync(item);
        await session.SaveChangesAsync();

        var handler = new ItemTransferHandler();
        var dispatcher = new WorldChangeDispatcher([handler], new CampaignDocumentKeys(), NullLogger<WorldChangeDispatcher>.Instance);
        var characters = new Dictionary<string, Character> { [fromChar.Id] = fromChar, [toChar.Id] = toChar };
        var items = new Dictionary<string, Item> { [item.Id] = item };
        var summary = new List<string>();
        var context = new ChangeContext(
            session, characters, items, new Dictionary<string, Location>(), new Dictionary<string, Faction>(), new Dictionary<string, Quest>(),
            NullLogger.Instance, () => Task.FromResult(new CampaignTime()), () => Task.FromResult(new Dictionary<string, string>()),
            _ => Task.CompletedTask, summary, dispatcher, campaignName: campaign);

        var change = new ItemTransfer { ItemId = item.Id, ToHolderId = toChar.Id };
        var result = await handler.ApplyAsync(change, context);

        Assert.True(result.Success);
        Assert.Contains(summary, m => m == $"Item {item.Id} moved to {toChar.Id}");
    }

    // ---- Item 6: Armor/Held Properties key nudge ----

    [Fact]
    public void Item6_ArmorItem_WrongPropertyKey_Nudges()
    {
        var request = new ItemUpsertRequest
        {
            Id = "items/fixverify_wrongkey",
            Name = "Odd Breastplate",
            Description = "d",
            HolderId = "chars/hero",
            CoreCategory = ItemCategory.Armor,
            EquipZones = [EquipZone.Torso],
            EquipLayer = EquipLayer.Armor,
            Properties = new Dictionary<string, object> { ["armorBonus"] = 2 },
        };

        var nudges = ItemUpsertSanityChecker.GetNudges(request);

        Assert.Contains(nudges, n => n.Contains("NARRATIVE PROMPT") && n.Contains("recognized defense keys"));
    }

    [Fact]
    public void Item6_HeldItem_WrongPropertyKey_Nudges()
    {
        var request = new ItemUpsertRequest
        {
            Id = "items/fixverify_wrongkey_held",
            Name = "Odd Buckler",
            Description = "d",
            HolderId = "chars/hero",
            CoreCategory = ItemCategory.Weapon,
            EquipZones = [EquipZone.OffHand],
            EquipLayer = EquipLayer.Held,
            Properties = new Dictionary<string, object> { ["shieldBonus"] = 1 },
        };

        var nudges = ItemUpsertSanityChecker.GetNudges(request);

        Assert.Contains(nudges, n => n.Contains("NARRATIVE PROMPT") && n.Contains("recognized defense keys"));
    }

    [Fact]
    public void Item6_ArmorItem_CorrectAcBonusKey_NoNudge()
    {
        var request = new ItemUpsertRequest
        {
            Id = "items/fixverify_rightkey",
            Name = "Proper Breastplate",
            Description = "d",
            HolderId = "chars/hero",
            CoreCategory = ItemCategory.Armor,
            EquipZones = [EquipZone.Torso],
            EquipLayer = EquipLayer.Armor,
            Properties = new Dictionary<string, object> { ["acBonus"] = 3 },
        };

        var nudges = ItemUpsertSanityChecker.GetNudges(request);

        Assert.DoesNotContain(nudges, n => n.Contains("recognized defense keys"));
    }

    [Fact]
    public void Item6_NonArmorNonHeldItem_ArbitraryProperties_NoNudge()
    {
        var request = new ItemUpsertRequest
        {
            Id = "items/fixverify_trinket",
            Name = "Trinket",
            Description = "d",
            HolderId = "chars/hero",
            CoreCategory = ItemCategory.Other,
            Properties = new Dictionary<string, object> { ["glowColor"] = "blue" },
        };

        var nudges = ItemUpsertSanityChecker.GetNudges(request);

        Assert.DoesNotContain(nudges, n => n.Contains("recognized defense keys"));
    }
}
