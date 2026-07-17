using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Models;
using Xunit;

namespace CampaignVault.Tests;

[Collection("RavenDB")]
public class AmbientItemDecayRuleTests : IClassFixture<RavenDBFixture>
{
    private readonly RavenDBFixture _fixture;

    public AmbientItemDecayRuleTests(RavenDBFixture fixture)
    {
        _fixture = fixture;
    }

    // Each test uses its own CampaignName: AmbientItemDecayRule/SimulationQueryHelper scope purely by
    // CampaignName with no per-test data isolation, and the embedded-RavenDB fallback shares one DB
    // across the whole test run, so reusing a campaign name would leak items between tests.
    private static Item MakeItem(string id, AmbientPersistence? persistence, string campaignName) => new()
    {
        Id = id,
        Name = id,
        Description = "An item.",
        HolderId = "locations/tavern",
        CampaignName = campaignName,
        Persistence = persistence,
    };

    [Fact]
    public async Task ApplyAsync_FutureExpiry_LeftUntouched()
    {
        const string campaign = "ambient-decay-test-future";
        using var session = _fixture.Store.OpenAsyncSession();
        var item = MakeItem("items/ambient_future", new AmbientPersistence { Note = "still warm", ExpiresAtDay = 100 }, campaign);
        await session.StoreAsync(item);
        session.Advanced.WaitForIndexesAfterSaveChanges(timeout: TimeSpan.FromSeconds(10), throwOnTimeout: true);
        await session.SaveChangesAsync();

        var rule = new AmbientItemDecayRule();
        var time = new CampaignTime { TotalDaysElapsed = 5 };
        var ctx = new SimulationContext(time, [], [], session, 1, campaign);

        var result = await rule.ApplyAsync(ctx);

        Assert.Empty(result.Deltas.OfType<ItemPersistenceSurfaced>());
    }

    [Fact]
    public async Task ApplyAsync_PastExpiry_FlipsPressureSurfacedOnce()
    {
        const string campaign = "ambient-decay-test-past";
        using var session = _fixture.Store.OpenAsyncSession();
        var item = MakeItem("items/ambient_past", new AmbientPersistence { Note = "porridge going cold", ExpiresAtDay = 3 }, campaign);
        await session.StoreAsync(item);
        session.Advanced.WaitForIndexesAfterSaveChanges(timeout: TimeSpan.FromSeconds(10), throwOnTimeout: true);
        await session.SaveChangesAsync();

        var rule = new AmbientItemDecayRule();
        var time = new CampaignTime { TotalDaysElapsed = 5 };
        var ctx = new SimulationContext(time, [], [], session, 1, campaign);

        var result = await rule.ApplyAsync(ctx);

        var delta = Assert.Single(result.Deltas.OfType<ItemPersistenceSurfaced>());
        Assert.Equal(item.Id, delta.ItemId);
        Assert.Contains(result.NarrativeEvents, n => n.Contains("porridge going cold"));

        // Applying the delta flips the flag.
        Assert.False(item.Persistence!.PressureSurfaced);
        var handler = new CampaignVault.Data.ChangeHandlers.ItemPersistenceSurfacedHandler();
        var dispatcher = new CampaignVault.Data.ChangeHandlers.WorldChangeDispatcher(
            [handler], new CampaignDocumentKeys(), Microsoft.Extensions.Logging.Abstractions.NullLogger<CampaignVault.Data.ChangeHandlers.WorldChangeDispatcher>.Instance);
        var changeContext = new CampaignVault.Data.ChangeHandlers.ChangeContext(
            sessionForTests: session,
            characters: new Dictionary<string, Character>(),
            items: new Dictionary<string, Item> { [item.Id] = item },
            locations: new Dictionary<string, Location>(),
            factions: new Dictionary<string, Faction>(),
            quests: new Dictionary<string, Quest>(),
            logger: Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            summary: [],
            dispatcher: dispatcher,
            campaignName: campaign);

        await handler.ApplyAsync(delta, changeContext);

        Assert.True(item.Persistence!.PressureSurfaced);

        // Idempotent: a second pass with the same current day should not re-surface it.
        var secondPass = await rule.ApplyAsync(ctx);
        Assert.Empty(secondPass.Deltas.OfType<ItemPersistenceSurfaced>());
    }

    [Fact]
    public async Task ApplyAsync_NullPersistence_LeftUntouched()
    {
        const string campaign = "ambient-decay-test-null";
        using var session = _fixture.Store.OpenAsyncSession();
        var item = MakeItem("items/ambient_no_persistence", null, campaign);
        await session.StoreAsync(item);
        session.Advanced.WaitForIndexesAfterSaveChanges(timeout: TimeSpan.FromSeconds(10), throwOnTimeout: true);
        await session.SaveChangesAsync();

        var rule = new AmbientItemDecayRule();
        var time = new CampaignTime { TotalDaysElapsed = 999 };
        var ctx = new SimulationContext(time, [], [], session, 1, campaign);

        var result = await rule.ApplyAsync(ctx);

        Assert.Empty(result.Deltas.OfType<ItemPersistenceSurfaced>());
    }
}
