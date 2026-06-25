using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CampaignVault.Tests;

[Collection("RavenDB")]
public class TransientEvictionRuleTests : IClassFixture<RavenDBFixture>
{
    private readonly RavenDBFixture _fixture;

    public TransientEvictionRuleTests(RavenDBFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ApplyAsync_EvictsTransient_WhenLocationUnvisited()
    {
        using var session = _fixture.Store.OpenAsyncSession();

        var loc = new Location
            { Id = "locations/abandoned_tavern", Name = "Tavern", LastVisitedDay = 1, CampaignName = "evict-test" };
        var c = new Character
        {
            Id = "chars/transient_guy", Name = "Guy", CurrentLocationId = loc.Id, KeepAlive = false, Schedule = null,
            CampaignName = "evict-test"
        };

        await session.StoreAsync(loc);
        await session.StoreAsync(c);
        session.Advanced.WaitForIndexesAfterSaveChanges(timeout: TimeSpan.FromSeconds(10), throwOnTimeout: true,
            indexes: ["Character/Search"]);
        await session.SaveChangesAsync();

        var rule = new TransientEvictionRule(NullLogger<TransientEvictionRule>.Instance);
        var time = new CampaignTime { TotalDaysElapsed = 3 }; // 3 - 1 > 1 (should evict)

        var ctx = new SimulationContext(time, new List<Rumor>(), new List<Character>(), session, 2, "evict-test");

        var result = await rule.ApplyAsync(ctx);

        Assert.Single(result.Deltas);
        var delta = Assert.IsType<ActivityChange>(result.Deltas[0]);
        Assert.Equal(c.Id, delta.CharacterId);
        Assert.Null(delta.NewLocationId);

        Assert.Single(result.NarrativeEvents);
        Assert.Contains("is no longer present", result.NarrativeEvents[0]);
    }

    [Fact]
    public async Task ApplyAsync_KeepsTransient_WhenLocationRecentlyVisited()
    {
        using var session = _fixture.Store.OpenAsyncSession();

        var loc = new Location
            { Id = "locations/warm_tavern", Name = "Tavern", LastVisitedDay = 2, CampaignName = "keep-test" };
        var c = new Character
        {
            Id = "chars/transient_guy2", Name = "Guy", CurrentLocationId = loc.Id, KeepAlive = false, Schedule = null,
            CampaignName = "keep-test"
        };

        await session.StoreAsync(loc);
        await session.StoreAsync(c);
        session.Advanced.WaitForIndexesAfterSaveChanges(timeout: TimeSpan.FromSeconds(10), throwOnTimeout: true,
            indexes: ["Character/Search"]);
        await session.SaveChangesAsync();

        var rule = new TransientEvictionRule(NullLogger<TransientEvictionRule>.Instance);
        var time = new CampaignTime { TotalDaysElapsed = 3 }; // 3 - 2 = 1 (should not evict yet)

        var ctx = new SimulationContext(time, new List<Rumor>(), new List<Character>(), session, 1, "keep-test");

        var result = await rule.ApplyAsync(ctx);

        Assert.DoesNotContain(result.Deltas, d => d is ActivityChange ac && ac.CharacterId == "chars/transient_guy2");
        Assert.DoesNotContain(result.NarrativeEvents, n => n.Contains("transient_guy2"));
    }

    [Fact]
    public async Task ApplyAsync_ActiveQuestGiver_IsProtectedFromEviction()
    {
        using var session = _fixture.Store.OpenAsyncSession();

        var loc = new Location
            { Id = "locations/quest_tavern", Name = "Tavern", LastVisitedDay = 1, CampaignName = "quest-test" };
        var c = new Character
        {
            Id = "chars/quest_giver", Name = "Quest Guy", CurrentLocationId = loc.Id, KeepAlive = false,
            Schedule = null, CampaignName = "quest-test"
        };

        await session.StoreAsync(loc);
        await session.StoreAsync(c);
        session.Advanced.WaitForIndexesAfterSaveChanges(timeout: TimeSpan.FromSeconds(10), throwOnTimeout: true,
            indexes: ["Character/Search"]);
        await session.SaveChangesAsync();

        var rule = new TransientEvictionRule(NullLogger<TransientEvictionRule>.Instance);
        var time = new CampaignTime { TotalDaysElapsed = 3 }; // Should evict based on time

        var activeQuests = new List<Quest>
        {
            new Quest { Id = "quests/q1", Title = "Test Quest", GiverId = c.Id, OverallState = QuestState.InProgress }
        };

        var ctx = new SimulationContext(time, new List<Rumor>(), new List<Character>(), session, 2, "quest-test", null,
            activeQuests);

        var result = await rule.ApplyAsync(ctx);

        // Verify eviction didn't happen
        Assert.Empty(result.Deltas);
        Assert.Single(result.NarrativeEvents);
        Assert.Contains("Quest giver 'Quest Guy' is a transient NPC but has an active quest",
            result.NarrativeEvents[0]);
    }

    [Fact]
    public async Task ApplyAsync_DeletesOrphanItems()
    {
        using var session = _fixture.Store.OpenAsyncSession();

        var loc = new Location
            { Id = "locations/item_tavern", Name = "Tavern", LastVisitedDay = 1, CampaignName = "item-evict-test" };
        var c = new Character
        {
            Id = "chars/item_guy", Name = "Guy", CurrentLocationId = loc.Id, KeepAlive = false, Schedule = null,
            CampaignName = "item-evict-test"
        };
        var i = new Item
            { Id = "items/cool_sword", Name = "Cool Sword", HolderId = c.Id, CampaignName = "item-evict-test" };

        await session.StoreAsync(loc);
        await session.StoreAsync(c);
        await session.StoreAsync(i);
        session.Advanced.WaitForIndexesAfterSaveChanges(timeout: TimeSpan.FromSeconds(10), throwOnTimeout: true,
            indexes: ["Character/Search", "Item/Search"]);
        await session.SaveChangesAsync();

        var rule = new TransientEvictionRule(NullLogger<TransientEvictionRule>.Instance);
        var time = new CampaignTime { TotalDaysElapsed = 3 }; // Should evict

        var ctx = new SimulationContext(time, new List<Rumor>(), new List<Character>(), session, 2, "item-evict-test");

        var result = await rule.ApplyAsync(ctx);
        await session.SaveChangesAsync(); // Actually apply the deletes to DB

        // The character was evicted
        Assert.Single(result.Deltas);
        var delta = Assert.IsType<ActivityChange>(result.Deltas[0]);
        Assert.Equal(c.Id, delta.CharacterId);
        Assert.Null(delta.NewLocationId);

        Assert.Contains(result.NarrativeEvents, n => n.Contains("orphaned items held by transient characters"));

        using var verifySession = _fixture.Store.OpenAsyncSession();
        var deletedItem = await verifySession.LoadAsync<Item>(i.Id);
        Assert.Null(deletedItem); // The item should be gone!
    }

    [Fact]
    public async Task ApplyAsync_RespectsTransientEvictionGraceDays_FromConfig()
    {
        using var session = _fixture.Store.OpenAsyncSession();

        var loc = new Location
            { Id = "locations/grace_tavern", Name = "Tavern", LastVisitedDay = 1, CampaignName = "grace-test" };
        var c = new Character
        {
            Id = "chars/grace_guy", Name = "Guy", CurrentLocationId = loc.Id, KeepAlive = false, Schedule = null,
            CampaignName = "grace-test"
        };

        await session.StoreAsync(loc);
        await session.StoreAsync(c);
        session.Advanced.WaitForIndexesAfterSaveChanges(timeout: TimeSpan.FromSeconds(10), throwOnTimeout: true,
            indexes: ["Character/Search"]);
        await session.SaveChangesAsync();

        var rule = new TransientEvictionRule(NullLogger<TransientEvictionRule>.Instance);
        var time = new CampaignTime { TotalDaysElapsed = 3 }; // 3 - 1 = 2, should NOT evict with grace=3
        var config = new CampaignConfig { TransientEvictionGraceDays = 3 };
        var ctx = new SimulationContext(time, new List<Rumor>(), new List<Character>(), session, 2, "grace-test",
            Config: config);

        var result = await rule.ApplyAsync(ctx);
        Assert.DoesNotContain(result.Deltas, d => d is ActivityChange ac && ac.CharacterId == c.Id);

        // Now advance past grace: day 5 -> 5-1=4 > 3
        time = new CampaignTime { TotalDaysElapsed = 5 };
        ctx = new SimulationContext(time, new List<Rumor>(), new List<Character>(), session, 4, "grace-test",
            Config: config);
        result = await rule.ApplyAsync(ctx);
        Assert.Contains(result.Deltas, d => d is ActivityChange ac && ac.CharacterId == c.Id);
    }

    [Fact]
    public async Task TransientEvictionRule_CompletedQuestGiver_IsEvictedNormally()
    {
        using var session = _fixture.Store.OpenAsyncSession();

        var loc = new Location
            { Id = "locations/quest_tavern2", Name = "Tavern", LastVisitedDay = 1, CampaignName = "quest-evict-test" };
        var c = new Character
        {
            Id = "chars/quest_giver2", Name = "Quest Guy 2", CurrentLocationId = loc.Id, KeepAlive = false,
            Schedule = null, CampaignName = "quest-evict-test"
        };

        await session.StoreAsync(loc);
        await session.StoreAsync(c);
        session.Advanced.WaitForIndexesAfterSaveChanges(timeout: TimeSpan.FromSeconds(10), throwOnTimeout: true,
            indexes: ["Character/Search"]);
        await session.SaveChangesAsync();

        var rule = new TransientEvictionRule(NullLogger<TransientEvictionRule>.Instance);
        var time = new CampaignTime { TotalDaysElapsed = 3 }; // Should evict based on time

        var activeQuests = new List<Quest>
        {
            new Quest
            {
                Id = "quests/q2", Title = "Completed Test Quest", GiverId = c.Id, OverallState = QuestState.Complete
            }
        };

        var ctx = new SimulationContext(time, new List<Rumor>(), new List<Character>(), session, 2, "quest-evict-test",
            null, activeQuests);

        var result = await rule.ApplyAsync(ctx);

        // Verify eviction DID happen because quest is Complete
        Assert.Contains(result.Deltas, d => d is ActivityChange);
        Assert.DoesNotContain(result.NarrativeEvents, n => n.Contains("has an active quest"));
    }
}
