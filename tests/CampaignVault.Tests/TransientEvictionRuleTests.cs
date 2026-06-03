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
        
        var loc = new Location { Id = "locations/abandoned_tavern", Name = "Tavern", LastVisitedDay = 1 };
        var c = new Character { Id = "chars/transient_guy", Name = "Guy", CurrentLocationId = loc.Id, KeepAlive = false, Schedule = null };
        
        await session.StoreAsync(loc);
        await session.StoreAsync(c);
        await session.SaveChangesAsync();

        var rule = new TransientEvictionRule(NullLogger<TransientEvictionRule>.Instance);
        var time = new CampaignTime { TotalDaysElapsed = 3 }; // 3 - 1 > 1 (should evict)
        
        var ctx = new SimulationContext(time, new List<Rumor>(), new List<Character>(), session, 2);
        
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
        
        var loc = new Location { Id = "locations/warm_tavern", Name = "Tavern", LastVisitedDay = 2 };
        var c = new Character { Id = "chars/transient_guy2", Name = "Guy", CurrentLocationId = loc.Id, KeepAlive = false, Schedule = null };
        
        await session.StoreAsync(loc);
        await session.StoreAsync(c);
        await session.SaveChangesAsync();

        var rule = new TransientEvictionRule(NullLogger<TransientEvictionRule>.Instance);
        var time = new CampaignTime { TotalDaysElapsed = 3 }; // 3 - 2 = 1 (should not evict yet)
        
        var ctx = new SimulationContext(time, new List<Rumor>(), new List<Character>(), session, 1);
        
        var result = await rule.ApplyAsync(ctx);

        Assert.DoesNotContain(result.Deltas, d => d is ActivityChange ac && ac.CharacterId == "chars/transient_guy2");
        Assert.DoesNotContain(result.NarrativeEvents, n => n.Contains("transient_guy2"));
    }
}
