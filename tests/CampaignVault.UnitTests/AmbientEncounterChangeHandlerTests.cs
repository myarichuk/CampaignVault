using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Raven.Client.Documents.Session;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>
/// AmbientEncounterCheck/AmbientEncounterChangeHandler give advance_world and take_turn's ambient
/// day-boundary crossing the same encounter-roll mechanism RestChange/TravelChange already get for their
/// own spans, so "safe downtime" isn't safe purely because of which tool the DM happened to use.
/// </summary>
[Collection("RavenDB")]
public class AmbientEncounterChangeHandlerTests : IClassFixture<RavenDBFixture>
{
    private readonly RavenDBFixture _fixture;

    public AmbientEncounterChangeHandlerTests(RavenDBFixture fixture)
    {
        _fixture = fixture;
    }

    private static ChangeContext CreateContext(
        IAsyncDocumentSession session, Location location, string campaignName, WorldChangeDispatcher? dispatcher = null)
    {
        dispatcher ??= new WorldChangeDispatcher([], new CampaignDocumentKeys(), NullLogger<WorldChangeDispatcher>.Instance);

        return new ChangeContext(
            session,
            new Dictionary<string, Character>(),
            new Dictionary<string, Item>(),
            new Dictionary<string, Location> { [location.Id] = location },
            new Dictionary<string, Faction>(),
            new Dictionary<string, Quest>(),
            NullLogger.Instance,
            [],
            dispatcher,
            campaignName: campaignName);
    }

    private async Task StorePcAtLocationAsync(IAsyncDocumentSession session, string campaignName, string locationId)
    {
        await session.StoreAsync(new Character
        {
            Id = "chars/ambient-encounter-pc",
            Name = "Lyra",
            IsPc = true,
            CampaignName = campaignName,
            CurrentLocationId = locationId
        });
        session.Advanced.WaitForIndexesAfterSaveChanges(timeout: TimeSpan.FromSeconds(10), throwOnTimeout: true,
            indexes: ["Character/Search"]);
        await session.SaveChangesAsync();
    }

    [Fact]
    public async Task ApplyAsync_WhenRollSucceeds_SpawnsEncounter()
    {
        var campaignName = "ambient-encounter-hit-" + Guid.NewGuid().ToString("N")[..8];
        using var session = _fixture.Store.OpenAsyncSession();

        var location = new Location { Id = "locations/ambient-hit", Name = "Back Room", CampaignName = campaignName };
        await StorePcAtLocationAsync(session, campaignName, location.Id);

        var dispatched = new List<WorldChange>();
        var dispatcher = new WorldChangeDispatcher(
            [new CapturingHandler(dispatched)], new CampaignDocumentKeys(), NullLogger<WorldChangeDispatcher>.Instance);

        var handler = new AmbientEncounterChangeHandler(new EncounterResolver(() => 0.0));
        var result = await handler.ApplyAsync(
            new AmbientEncounterCheck { LocationId = location.Id, Hours = 24 },
            CreateContext(session, location, campaignName, dispatcher),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains(dispatched, d => d is CharacterCreate);
    }

    [Fact]
    public async Task ApplyAsync_WhenRollFails_NoEncounter()
    {
        var campaignName = "ambient-encounter-miss-" + Guid.NewGuid().ToString("N")[..8];
        using var session = _fixture.Store.OpenAsyncSession();

        var location = new Location { Id = "locations/ambient-miss", Name = "Back Room", CampaignName = campaignName };
        await StorePcAtLocationAsync(session, campaignName, location.Id);

        var handler = new AmbientEncounterChangeHandler(new EncounterResolver(() => 0.99));
        var result = await handler.ApplyAsync(
            new AmbientEncounterCheck { LocationId = location.Id, Hours = 24 },
            CreateContext(session, location, campaignName),
            CancellationToken.None);

        Assert.True(result.Success);
    }

    [Fact]
    public async Task ApplyAsync_NoPartyAtLocation_NoOp()
    {
        var campaignName = "ambient-encounter-empty-" + Guid.NewGuid().ToString("N")[..8];
        using var session = _fixture.Store.OpenAsyncSession();

        var location = new Location { Id = "locations/ambient-empty", Name = "Empty Room", CampaignName = campaignName };
        // No PC stored at this location.

        var handler = new AmbientEncounterChangeHandler(new EncounterResolver(() => 0.0));
        var result = await handler.ApplyAsync(
            new AmbientEncounterCheck { LocationId = location.Id, Hours = 24 },
            CreateContext(session, location, campaignName),
            CancellationToken.None);

        Assert.True(result.Success);
    }

    private sealed class CapturingHandler : IWorldChangeHandler
    {
        private readonly List<WorldChange> _captured;

        public CapturingHandler(List<WorldChange> captured) => _captured = captured;

        public bool ShouldHandle(WorldChange change) => true;

        public Task<ChangeHandlerResult> ApplyAsync(WorldChange change, IChangeContext context, CancellationToken ct = default)
        {
            _captured.Add(change);
            return Task.FromResult(ChangeHandlerResult.Ok);
        }
    }
}
