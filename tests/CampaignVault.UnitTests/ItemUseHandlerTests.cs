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
public class ItemUseHandlerTests : IClassFixture<RavenDBFixture>
{
    private readonly RavenDBFixture _fixture;

    public ItemUseHandlerTests(RavenDBFixture fixture)
    {
        _fixture = fixture;
    }

    private static Item MakeChargedItem(string id, int maxCharges, int? currentCharges = null) => new()
    {
        Id = id,
        Name = id,
        HolderId = "chars/hero",
        CoreCategory = ItemCategory.Consumable,
        MaxCharges = maxCharges,
        CurrentCharges = currentCharges,
        CampaignName = "item-use-test",
    };

    private ChangeContext BuildContext(
        Raven.Client.Documents.Session.IAsyncDocumentSession? session,
        Dictionary<string, Item> items)
    {
        var dispatcher = new WorldChangeDispatcher([], new CampaignDocumentKeys(), NullLogger<WorldChangeDispatcher>.Instance);
        return new ChangeContext(
            sessionForTests: session,
            characters: new Dictionary<string, Character>(),
            items: items,
            locations: new Dictionary<string, Location>(),
            factions: new Dictionary<string, Faction>(),
            quests: new Dictionary<string, Quest>(),
            logger: NullLogger.Instance,
            summary: [],
            dispatcher: dispatcher,
            campaignName: "item-use-test");
    }

    [Fact]
    public async Task ApplyAsync_LazyInitsCurrentCharges_OnFirstUse()
    {
        var item = MakeChargedItem("items/use_lazy_init", maxCharges: 3);
        var context = BuildContext(null, new Dictionary<string, Item> { [item.Id] = item });

        var handler = new ItemUseHandler();
        var result = await handler.ApplyAsync(new ItemUse { ItemId = item.Id, Delta = -1 }, context);

        Assert.True(result.Success);
        Assert.Equal(2, item.CurrentCharges);
    }

    [Fact]
    public async Task ApplyAsync_Decrements_ExistingCharges()
    {
        var item = MakeChargedItem("items/use_decrement", maxCharges: 5, currentCharges: 3);
        var context = BuildContext(null, new Dictionary<string, Item> { [item.Id] = item });

        var handler = new ItemUseHandler();
        var result = await handler.ApplyAsync(new ItemUse { ItemId = item.Id, Delta = -2 }, context);

        Assert.True(result.Success);
        Assert.Equal(1, item.CurrentCharges);
    }

    [Fact]
    public async Task ApplyAsync_HardFails_WhenInsufficientCharges()
    {
        var item = MakeChargedItem("items/use_insufficient", maxCharges: 3, currentCharges: 1);
        var context = BuildContext(null, new Dictionary<string, Item> { [item.Id] = item });

        var handler = new ItemUseHandler();
        var result = await handler.ApplyAsync(new ItemUse { ItemId = item.Id, Delta = -2 }, context);

        Assert.False(result.Success);
        Assert.Contains("Insufficient charges", result.Message);
        // No silent clamping — the field is left untouched on failure.
        Assert.Equal(1, item.CurrentCharges);
    }

    [Fact]
    public async Task ApplyAsync_RefillClampsToMaxCharges()
    {
        var item = MakeChargedItem("items/use_refill_clamp", maxCharges: 3, currentCharges: 2);
        var context = BuildContext(null, new Dictionary<string, Item> { [item.Id] = item });

        var handler = new ItemUseHandler();
        var result = await handler.ApplyAsync(new ItemUse { ItemId = item.Id, Delta = +10 }, context);

        Assert.True(result.Success);
        Assert.Equal(3, item.CurrentCharges);
    }

    [Fact]
    public async Task ApplyAsync_FailsWhenItemHasNoMaxCharges()
    {
        var item = new Item { Id = "items/use_not_chargeable", Name = "Rock", HolderId = "chars/hero" };
        var context = BuildContext(null, new Dictionary<string, Item> { [item.Id] = item });

        var handler = new ItemUseHandler();
        var result = await handler.ApplyAsync(new ItemUse { ItemId = item.Id, Delta = -1 }, context);

        Assert.False(result.Success);
        Assert.Contains("not a limited-use item", result.Message);
    }

    [Fact]
    public async Task ApplyAsync_ZeroCharges_LogsEvent_WithoutAutoArchiving()
    {
        using var session = _fixture.Store.OpenAsyncSession();

        var item = MakeChargedItem("items/use_zero_charges", maxCharges: 1, currentCharges: 1);
        var loggedEvents = new List<Event>();

        var dispatcher = new WorldChangeDispatcher([], new CampaignDocumentKeys(), NullLogger<WorldChangeDispatcher>.Instance);
        var context = new ChangeContext(
            session,
            new Dictionary<string, Character>(),
            new Dictionary<string, Item> { [item.Id] = item },
            new Dictionary<string, Location>(),
            new Dictionary<string, Faction>(),
            new Dictionary<string, Quest>(),
            NullLogger.Instance,
            () => Task.FromResult(new CampaignTime()),
            () => Task.FromResult(new Dictionary<string, string>()),
            e => { loggedEvents.Add(e); return Task.CompletedTask; },
            [],
            dispatcher,
            campaignName: "item-use-test");

        var handler = new ItemUseHandler();
        var result = await handler.ApplyAsync(new ItemUse { ItemId = item.Id, Delta = -1 }, context);

        Assert.True(result.Success);
        Assert.Equal(0, item.CurrentCharges);
        Assert.False(item.IsArchived);
        Assert.Equal("chars/hero", item.HolderId);

        var loggedEvent = Assert.Single(loggedEvents);
        Assert.Contains("out of charges", loggedEvent.Summary);
    }
}
