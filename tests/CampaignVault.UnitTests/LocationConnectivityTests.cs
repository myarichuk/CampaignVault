using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Data.Pressure;
using CampaignVault.Data.Pressure.Contributors;
using CampaignVault.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CampaignVault.Tests;

[Collection("RavenDB")]
public class LocationConnectivityTests : IClassFixture<RavenDBFixture>
{
    private readonly RavenDBFixture _fixture;

    public LocationConnectivityTests(RavenDBFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task OneWayExit_SurfacesSuggestedCommitJson()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        var sourceId = "locations/room-a-" + System.Guid.NewGuid().ToString("N")[..8];
        var targetId = "locations/room-b-" + System.Guid.NewGuid().ToString("N")[..8];

        var source = new Location
        {
            Id = sourceId,
            Name = "Room A",
            Type = LocationType.Room,
            Exits = [new LocationExit(targetId, "Door to B")]
        };
        var target = new Location
        {
            Id = targetId,
            Name = "Room B",
            Type = LocationType.Room,
            Exits = []
        };
        await session.StoreAsync(source);
        await session.StoreAsync(target);
        await session.SaveChangesAsync();

        var contributor = new LocationConnectivityPressureContributor();
        var scene = new SceneView
        {
            IsLocationAnchored = true,
            Location = LocationDetailView.From(source),
            PresentNPCs = [],
            RecentEvents = []
        };

        var pressures = (await contributor.EvaluateAsync(new PressureContext(
            "test",
            new CampaignTime(),
            new CampaignConfig(),
            session,
            Scene: scene))).ToList();

        var oneWayPressure = Assert.Single(pressures);
        Assert.Equal(PressureSeverity.EngineWarning, oneWayPressure.Severity);
        Assert.NotNull(oneWayPressure.SuggestedCommitJson);
        Assert.Contains("location_update", oneWayPressure.SuggestedCommitJson);
        Assert.Contains(targetId, oneWayPressure.SuggestedCommitJson);
        Assert.Contains(sourceId, oneWayPressure.SuggestedCommitJson);
    }

    [Fact]
    public async Task OneWayExit_SkipsMarkedIntentionalOneWay()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        var sourceId = "locations/chute-a-" + System.Guid.NewGuid().ToString("N")[..8];
        var targetId = "locations/pit-b-" + System.Guid.NewGuid().ToString("N")[..8];

        var source = new Location
        {
            Id = sourceId,
            Name = "Upper Level",
            Type = LocationType.Room,
            Exits = [new LocationExit(targetId, "One-way chute", OneWay: true)]
        };
        await session.StoreAsync(source);
        await session.StoreAsync(new Location { Id = targetId, Name = "Pit", Type = LocationType.Room });
        await session.SaveChangesAsync();

        var contributor = new LocationConnectivityPressureContributor();
        var scene = new SceneView
        {
            IsLocationAnchored = true,
            Location = LocationDetailView.From(source)
        };

        var pressures = await contributor.EvaluateAsync(new PressureContext(
            "test",
            new CampaignTime(),
            new CampaignConfig(),
            session,
            Scene: scene));

        Assert.DoesNotContain(pressures, p => p.GroupingKey == LocationConnectivityPressureContributor.MissingReverseLinkGroupingKey);
    }

    [Fact]
    public async Task AddExit_DoesNotAutoRepair_WhenOneWayOrConfigDisabled()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        var sourceId = "locations/source-" + System.Guid.NewGuid().ToString("N")[..8];
        var targetId = "locations/target-" + System.Guid.NewGuid().ToString("N")[..8];

        var source = new Location { Id = sourceId, Name = "Source", Exits = [] };
        var target = new Location { Id = targetId, Name = "Target", Exits = [] };
        await session.StoreAsync(source);
        await session.StoreAsync(target);
        await session.SaveChangesAsync();

        var handler = new LocationUpdateHandler();
        var locations = new Dictionary<string, Location> { { sourceId, source }, { targetId, target } };
        var ctxDisabled = CreateContext(session, locations, new CampaignConfig { AutoRepairLocationConnectivity = false });
        var oneWayExit = new LocationExit(targetId, "Trap door", OneWay: true);

        var result = await handler.ApplyAsync(
            new LocationUpdate { LocationId = sourceId, AddExit = oneWayExit },
            ctxDisabled);
        Assert.True(result.Success);
        Assert.DoesNotContain(target.Exits, e => e.TargetLocationId == sourceId);
    }

    [Fact]
    public async Task AddExit_AutoRepairsReverse_WhenConfigEnabled()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        var sourceId = "locations/repair-source-" + System.Guid.NewGuid().ToString("N")[..8];
        var targetId = "locations/repair-target-" + System.Guid.NewGuid().ToString("N")[..8];

        var source = new Location { Id = sourceId, Name = "Source", Exits = [] };
        var target = new Location { Id = targetId, Name = "Target", Exits = [] };
        await session.StoreAsync(source);
        await session.StoreAsync(target);
        await session.SaveChangesAsync();

        var handler = new LocationUpdateHandler();
        var locations = new Dictionary<string, Location> { { sourceId, source }, { targetId, target } };
        var ctxEnabled = CreateContext(session, locations, new CampaignConfig { AutoRepairLocationConnectivity = true });

        var resultRepair = await handler.ApplyAsync(
            new LocationUpdate { LocationId = sourceId, AddExit = new LocationExit(targetId, "Hallway") },
            ctxEnabled);
        Assert.True(resultRepair.Success);
        Assert.Contains(target.Exits, e => e.TargetLocationId == sourceId);
    }

    private static ChangeContext CreateContext(
        Raven.Client.Documents.Session.IAsyncDocumentSession session,
        Dictionary<string, Location> locations,
        CampaignConfig config)
    {
        var dispatcher = new WorldChangeDispatcher([], new CampaignVault.Data.CampaignDocumentKeys());
        return new ChangeContext(
            session,
            new Dictionary<string, Character>(),
            new Dictionary<string, Item>(),
            locations,
            new Dictionary<string, Faction>(),
            new Dictionary<string, Quest>(),
            NullLogger.Instance,
            [],
            dispatcher,
            config: config);
    }
}