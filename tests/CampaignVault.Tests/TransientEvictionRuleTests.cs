using CampaignVault.Data;
using CampaignVault.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Raven.Client.Documents;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
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
        
        var loc = new Location { Id = "locations/abandoned_tavern", Name = "Tavern", LastVisitedDay = 1, CampaignName = "evict-test" };
        var c = new Character { Id = "chars/transient_guy", Name = "Guy", CurrentLocationId = loc.Id, KeepAlive = false, Schedule = null, CampaignName = "evict-test" };
        
        await session.StoreAsync(loc);
        await session.StoreAsync(c);
        session.Advanced.WaitForIndexesAfterSaveChanges(timeout: TimeSpan.FromSeconds(5), throwOnTimeout: true);
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
        
        var loc = new Location { Id = "locations/warm_tavern", Name = "Tavern", LastVisitedDay = 2, CampaignName = "keep-test" };
        var c = new Character { Id = "chars/transient_guy2", Name = "Guy", CurrentLocationId = loc.Id, KeepAlive = false, Schedule = null, CampaignName = "keep-test" };
        
        await session.StoreAsync(loc);
        await session.StoreAsync(c);
        session.Advanced.WaitForIndexesAfterSaveChanges(timeout: TimeSpan.FromSeconds(5), throwOnTimeout: true);
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
        
        var loc = new Location { Id = "locations/quest_tavern", Name = "Tavern", LastVisitedDay = 1, CampaignName = "quest-test" };
        var c = new Character { Id = "chars/quest_giver", Name = "Quest Guy", CurrentLocationId = loc.Id, KeepAlive = false, Schedule = null, CampaignName = "quest-test" };
        
        await session.StoreAsync(loc);
        await session.StoreAsync(c);
        session.Advanced.WaitForIndexesAfterSaveChanges(timeout: TimeSpan.FromSeconds(5), throwOnTimeout: true);
        await session.SaveChangesAsync();

        var rule = new TransientEvictionRule(NullLogger<TransientEvictionRule>.Instance);
        var time = new CampaignTime { TotalDaysElapsed = 3 }; // Should evict based on time
        
        var activeQuests = new List<Quest>
        {
            new Quest { Id = "quests/q1", Title = "Test Quest", GiverId = c.Id, OverallState = QuestState.InProgress }
        };
        
        var ctx = new SimulationContext(time, new List<Rumor>(), new List<Character>(), session, 2, "quest-test", null, activeQuests);
        
        var result = await rule.ApplyAsync(ctx);

        // Verify eviction didn't happen
        Assert.DoesNotContain(result.Deltas, d => d is ActivityChange);
        
        // Verify warning was emitted
        Assert.Contains(result.NarrativeEvents, n => n.Contains("Quest giver 'Quest Guy' is a transient NPC but has an active quest"));
    }

    [Fact]
    public async Task TransientEvictionRule_CompletedQuestGiver_IsEvictedNormally()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        
        var loc = new Location { Id = "locations/quest_tavern2", Name = "Tavern", LastVisitedDay = 1, CampaignName = "quest-evict-test" };
        var c = new Character { Id = "chars/quest_giver2", Name = "Quest Guy 2", CurrentLocationId = loc.Id, KeepAlive = false, Schedule = null, CampaignName = "quest-evict-test" };
        
        await session.StoreAsync(loc);
        await session.StoreAsync(c);
        session.Advanced.WaitForIndexesAfterSaveChanges(timeout: TimeSpan.FromSeconds(5), throwOnTimeout: true);
        await session.SaveChangesAsync();

        var rule = new TransientEvictionRule(NullLogger<TransientEvictionRule>.Instance);
        var time = new CampaignTime { TotalDaysElapsed = 3 }; // Should evict based on time
        
        var activeQuests = new List<Quest>
        {
            new Quest { Id = "quests/q2", Title = "Completed Test Quest", GiverId = c.Id, OverallState = QuestState.Complete }
        };
        
        var ctx = new SimulationContext(time, new List<Rumor>(), new List<Character>(), session, 2, "quest-evict-test", null, activeQuests);
        
        var result = await rule.ApplyAsync(ctx);

        // Verify eviction DID happen because quest is Complete
        Assert.Contains(result.Deltas, d => d is ActivityChange);
        Assert.DoesNotContain(result.NarrativeEvents, n => n.Contains("has an active quest"));
    }
}
