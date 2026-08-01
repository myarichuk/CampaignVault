using System;
using System.Linq;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Data.Pressure;
using CampaignVault.Data.Pressure.Contributors;
using CampaignVault.Models;
using CampaignVault.Tools;
using Xunit;

namespace CampaignVault.Tests;

[Collection("RavenDB")]
public class EventConsequenceTests : IClassFixture<RavenDBFixture>
{
    private readonly RavenDBFixture _fixture;

    public EventConsequenceTests(RavenDBFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void EventConsequenceRegistry_CombatTemplate_BuildsLocationUpdate()
    {
        var evt = new Event
        {
            Category = EventCategory.Combat,
            Summary = "Bandits ambushed the party",
            RelatedEntityId = "locations/forest-clearing"
        };

        Assert.True(EventConsequenceRegistry.TrySuggest(evt, out var templateId, out var json));
        Assert.Equal(EventConsequenceRegistry.CombatLocationDamageTemplateId, templateId);
        Assert.Contains("location_update", json);
        Assert.Contains("locations/forest-clearing", json);
        Assert.Contains("newState", json);
        Assert.Contains("tagsToAdd", json);
    }

    [Fact]
    public void EventConsequenceRegistry_DiscoveryTemplate_BuildsLocationUpdate()
    {
        var evt = new Event
        {
            Category = EventCategory.Discovery,
            Summary = "Found a hidden cache",
            RelatedEntityId = "locations/old-cellar"
        };

        Assert.True(EventConsequenceRegistry.TrySuggest(evt, out var templateId, out var json));
        Assert.Equal(EventConsequenceRegistry.DiscoveryLocationStateTemplateId, templateId);
        Assert.Contains("recently-explored", json);
    }

    [Fact]
    public void EventConsequenceRegistry_CombatTemplate_UsesLocationId_WhenSet()
    {
        var evt = new Event
        {
            Category = EventCategory.Combat,
            Summary = "Bar fight spills into the alley",
            LocationId = "locations/rusty-nail",
            RelatedLocationIds = ["locations/rusty-nail-alley"],
            Involved = ["chars/pc1", "chars/bully"]
        };

        Assert.True(EventConsequenceRegistry.TrySuggest(evt, out var templateId, out var json));
        Assert.Equal(EventConsequenceRegistry.CombatLocationDamageTemplateId, templateId);
        Assert.Contains("locations/rusty-nail", json);
        Assert.DoesNotContain("rusty-nail-alley", json);
    }

    [Fact]
    public void Event_TouchesLocation_MatchesAllSpatialAnchorFields()
    {
        const string locId = "locations/rusty-nail";
        const string spilloverId = "locations/rusty-nail-alley";

        Assert.True(new Event { LocationId = locId }.TouchesLocation(locId));
        Assert.True(new Event { RelatedEntityId = locId }.TouchesLocation(locId));
        Assert.True(new Event { RelatedLocationIds = [spilloverId] }.TouchesLocation(spilloverId));
        Assert.True(new Event { Involved = [locId] }.TouchesLocation(locId));
        Assert.False(new Event { Involved = ["chars/pc1"] }.TouchesLocation(locId));
    }

    [Fact]
    public async Task CombatEvent_SuggestsLocationUpdate_OnNextGetScene()
    {
        var tools = TestCampaignToolsFactory.Create(_fixture);
        var campaign = "event-consequence-" + System.Guid.NewGuid().ToString("N")[..8];
        await TestCampaignDefaults.EnsureExistsAsync(tools, campaign);

        const string locId = "locations/ambush-site";

        var repo = _fixture.CreateRepository();
        using (var session = _fixture.Store.OpenAsyncSession())
        {
            await repo.UpsertLocationAsync(session, new LocationUpsertRequest
            {
                Id = locId,
                Name = "Forest Clearing",
                Type = LocationType.Wilderness,
                CurrentState = "Peaceful meadow"
            }, campaign);
            await repo.SaveTimeAsync(session, new CampaignTime { TotalDaysElapsed = 5 }, campaign);
            await session.SaveChangesAsync();
        }

        var commitResult = await tools.Commit([
            new EventOccurred
            {
                Category = EventCategory.Combat,
                Summary = "Ambush by bandits",
                RelatedEntityId = locId,
                Involved = ["chars/pc-1"]
            }
        ], "Combat at clearing", campaign);
        Assert.True(commitResult.Success);

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            var locBefore = await session.LoadAsync<Location>(locId);
            Assert.Equal("Peaceful meadow", locBefore!.CurrentState);
            session.Advanced.WaitForIndexesAfterSaveChanges();
            await session.SaveChangesAsync();
        }

        var sceneResult = await tools.GetScene(locId, campaignName: campaign, partyPresent: true);
        Assert.True(sceneResult.Success);

        var pressureItems = sceneResult.Data!.WorldPressureItems ?? [];
        var consequencePressure = pressureItems.FirstOrDefault(p =>
            p.GroupingKey.StartsWith(EventConsequenceRegistry.EventConsequenceGroupingKey));
        Assert.NotNull(consequencePressure);
        Assert.NotNull(consequencePressure!.SuggestedCommitJson);
        Assert.Contains("location_update", consequencePressure.SuggestedCommitJson);
        Assert.Contains("battle-scarred", consequencePressure.SuggestedCommitJson);

        using (var verifySession = _fixture.Store.OpenAsyncSession())
        {
            var locAfter = await verifySession.LoadAsync<Location>(locId);
            Assert.Equal("Peaceful meadow", locAfter!.CurrentState);
        }
    }

    [Fact]
    public async Task CombatEvent_WithLocationId_SuggestsLocationUpdate_OnNextGetScene()
    {
        var tools = TestCampaignToolsFactory.Create(_fixture);
        var campaign = "event-consequence-locid-" + System.Guid.NewGuid().ToString("N")[..8];
        await TestCampaignDefaults.EnsureExistsAsync(tools, campaign);

        const string locId = "locations/tavern-floor";

        var repo = _fixture.CreateRepository();
        using (var session = _fixture.Store.OpenAsyncSession())
        {
            await repo.UpsertLocationAsync(session, new LocationUpsertRequest
            {
                Id = locId,
                Name = "Tavern Floor",
                Type = LocationType.Room,
                CurrentState = "Quiet evening crowd"
            }, campaign);
            await repo.SaveTimeAsync(session, new CampaignTime { TotalDaysElapsed = 5 }, campaign);
            await session.SaveChangesAsync();
        }

        var commitResult = await tools.Commit([
            new EventOccurred
            {
                Category = EventCategory.Combat,
                Summary = "A bar fight breaks out on the tavern floor",
                LocationId = locId,
                Involved = ["chars/pc-1", "chars/bully"]
            }
        ], "Bar fight", campaign);
        Assert.True(commitResult.Success);

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            session.Advanced.WaitForIndexesAfterSaveChanges();
            await session.SaveChangesAsync();
        }
        
        var sceneResult = await tools.GetScene(locId, campaignName: campaign, partyPresent: true);
        Assert.True(sceneResult.Success);

        var consequencePressure = (sceneResult.Data!.WorldPressureItems ?? [])
            .FirstOrDefault(p => p.GroupingKey.StartsWith(EventConsequenceRegistry.EventConsequenceGroupingKey));
        Assert.NotNull(consequencePressure);
        Assert.Contains("location_update", consequencePressure!.SuggestedCommitJson);
        Assert.Contains(locId, consequencePressure.SuggestedCommitJson);
    }

    [Fact]
    public async Task EventConsequencePressureContributor_MatchesEventsByLocationId()
    {
        var suffix = System.Guid.NewGuid().ToString("N")[..8];
        var campaign = $"test-locid-{suffix}";
        var locId = $"locations/hall-{suffix}";

        using var session = _fixture.Store.OpenAsyncSession();
        await session.StoreAsync(new Location { Id = locId, Name = "Hall", Type = LocationType.Room, CampaignName = campaign });
        await session.StoreAsync(new Event
        {
            Id = $"events/combat-locid-{suffix}",
            Category = EventCategory.Combat,
            Summary = "Brawl in the hall",
            LocationId = locId,
            Involved = ["chars/brawler"],
            DayLogged = 3,
            CampaignName = campaign
        });
        session.Advanced.WaitForIndexesAfterSaveChanges(timeout: TimeSpan.FromSeconds(10), throwOnTimeout: true);
        await session.SaveChangesAsync();

        var contributor = new EventConsequencePressureContributor();
        var scene = new SceneView { IsLocationAnchored = true, Location = LocationDetailView.From(new Location { Id = locId, Name = "Hall" }) };
        var pressures = await contributor.EvaluateAsync(new PressureContext(
            campaign,
            new CampaignTime { TotalDaysElapsed = 5 },
            new CampaignConfig(),
            session,
            Scene: scene));

        Assert.Contains(pressures, p => p.GroupingKey.StartsWith(EventConsequenceRegistry.EventConsequenceGroupingKey));
    }

    [Fact]
    public async Task EventConsequencePressureContributor_SkipsNonLocationCombat()
    {
        var suffix = System.Guid.NewGuid().ToString("N")[..8];
        var campaign = $"test-skip-{suffix}";
        var locId = $"locations/hall-{suffix}";

        using var session = _fixture.Store.OpenAsyncSession();
        await session.StoreAsync(new Location { Id = locId, Name = "Hall", Type = LocationType.Room, CampaignName = campaign });
        await session.StoreAsync(new Event
        {
            Id = $"events/combat-1-{suffix}",
            Category = EventCategory.Combat,
            Summary = "Street fight",
            RelatedEntityId = "chars/brawler",
            DayLogged = 3,
            CampaignName = campaign
        });
        session.Advanced.WaitForIndexesAfterSaveChanges(timeout: TimeSpan.FromSeconds(10), throwOnTimeout: true);
        await session.SaveChangesAsync();

        var contributor = new EventConsequencePressureContributor();
        var scene = new SceneView { IsLocationAnchored = true, Location = LocationDetailView.From(new Location { Id = locId, Name = "Hall" }) };
        var pressures = await contributor.EvaluateAsync(new PressureContext(
            campaign,
            new CampaignTime { TotalDaysElapsed = 5 },
            new CampaignConfig(),
            session,
            Scene: scene));

        Assert.DoesNotContain(pressures, p => p.GroupingKey.StartsWith(EventConsequenceRegistry.EventConsequenceGroupingKey));
    }
}