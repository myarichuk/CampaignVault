using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CampaignVault.Tests;

[Collection("RavenDB")]
public class EvictionIntegrationTests : IClassFixture<RavenDBFixture>
{
    private readonly RavenDBFixture _fixture;

    public EvictionIntegrationTests(RavenDBFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task AdvanceWorld_PersistsRecentlyDeparted_AndDepartureEvent()
    {
        var engine = new DefaultSimulationEngine([new TransientEvictionRule(NullLogger<TransientEvictionRule>.Instance)], null);
        var repo = _fixture.CreateRepository(engineOverride: engine);

        const string campaign = "evict-integration";
        await TestCampaignDefaults.EnsureExistsAsync(_fixture, campaign);

        using var session = _fixture.Store.OpenAsyncSession();
        const string charId = "chars/transient-bard";
        const string locId = "locations/tavern";

        var location = new Location { Id = locId, Name = "The Rusty Nail", Type = LocationType.Room, LastVisitedDay = 1, CampaignName = campaign };
        var character = new Character
        {
            Id = charId,
            Name = "Mira the Bard",
            KeepAlive = false,
            Schedule = null,
            CurrentLocationId = locId,
            CampaignName = campaign
        };

        await repo.UpsertLocationAsync(session, location, campaign);
        await repo.UpsertCharacterAsync(session, character, campaign);
        await repo.SaveTimeAsync(session, new CampaignTime { TotalDaysElapsed = 1 }, campaign);
        await session.SaveChangesAsync();

        // Advance time enough to trigger eviction (TransientEvictionRule checks if location was visited
        // recently; with LastVisitedDay=1 and advancing to day 3+, the NPC should be evicted).
        await repo.AdvanceWorldAsync(session, 3, TimeOfDay.Noon, campaign);
        await session.SaveChangesAsync();

        // Reload location and assert RecentlyDeparted is populated.
        var reloadedLocation = await session.LoadAsync<Location>(locId);
        Assert.NotNull(reloadedLocation);
        Assert.NotEmpty(reloadedLocation.RecentlyDeparted);
        var departed = Assert.Single(reloadedLocation.RecentlyDeparted);
        Assert.Equal(charId, departed.CharacterId);
        Assert.Equal("Mira the Bard", departed.Name);
        Assert.Equal(4, departed.DepartedAtDay); // After advancing 3 days from day 1

        // Query events and assert Departure event exists.
        var events = await repo.QueryEventsAsync(session, null, EventCategory.Departure, 10, campaign);
        var departureEvent = Assert.Single(events);
        Assert.Equal(EventCategory.Departure, departureEvent.Category);
        Assert.Contains(charId, departureEvent.Involved!);
        Assert.Equal(locId, departureEvent.RelatedEntityId);

        // Verify character doc has departedAtDay and departedFromLocationId set.
        var reloadedCharacter = await session.LoadAsync<Character>(charId);
        Assert.NotNull(reloadedCharacter.DepartedAtDay);
        Assert.Equal(locId, reloadedCharacter.DepartedFromLocationId);
    }
}
