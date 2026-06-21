using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Raven.Client.Documents;
using Xunit;

namespace CampaignVault.Tests;

[Collection("RavenDB")]
public class LocationAndRumorHandlersTests : IClassFixture<RavenDBFixture>
{
    private readonly RavenDBFixture _fixture;

    public LocationAndRumorHandlersTests(RavenDBFixture fixture)
    {
        _fixture = fixture;
    }

    private ChangeContext CreateContext(
        Raven.Client.Documents.Session.IAsyncDocumentSession session,
        Dictionary<string, Location>? locations = null,
        string campaignName = "test-campaign",
        List<string>? summaryList = null)
    {
        var dispatcher = new WorldChangeDispatcher(new List<IWorldChangeHandler>(), new CampaignVault.Data.CampaignDocumentKeys());
        return new ChangeContext(
            session,
            new Dictionary<string, Character>(),
            new Dictionary<string, Item>(),
            locations ?? new Dictionary<string, Location>(),
            new Dictionary<string, Faction>(),
            new Dictionary<string, Quest>(),
            NullLogger.Instance,
            summaryList ?? new List<string>(),
            dispatcher,
            null,
            campaignName
        );
    }

    [Fact]
    public async Task LocationCreate_UpdatesExistingLocation_WhenIdCollision()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        var locId = "locations/collision-" + Guid.NewGuid();
        
        var existing = new Location
        {
            Id = locId,
            Name = "Old Name",
            Description = "Old Desc",
            Type = LocationType.Settlement
        };
        await session.StoreAsync(existing);
        await session.SaveChangesAsync();

        var handler = new LocationCreateHandler();
        var change = new LocationCreate
        {
            LocationId = locId,
            Name = "New Name",
            Description = "New Desc",
            Type = LocationType.Building, // Use Building (non-Room) so it overrides Settlement
            PointsOfInterest = ["PoI 1"],
            AmbientCrowd = "Noisy",
            Exits = [new LocationExit("locations/other", "An exit")]
        };

        var ctx = CreateContext(session);
        var result = await handler.ApplyAsync(change, ctx);

        Assert.True(result.Success);
        
        await session.SaveChangesAsync();

        var reloaded = await session.LoadAsync<Location>(locId);
        Assert.Equal("New Name", reloaded.Name);
        Assert.Equal("New Desc", reloaded.Description);
        Assert.Equal(LocationType.Building, reloaded.Type);
        Assert.Contains("PoI 1", reloaded.PointsOfInterest);
        Assert.Equal("Noisy", reloaded.AmbientCrowd);
        Assert.Single(reloaded.Exits);
    }

    [Fact]
    public async Task LocationCreate_ClampsDangerModifier_AndOrphanWarning()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        var locId = "locations/clamp-" + Guid.NewGuid();
        var handler = new LocationCreateHandler();

        var change = new LocationCreate
        {
            LocationId = locId,
            Name = "Clamp Location",
            DangerModifier = 120, // Should be clamped to 50
            ConnectedFromLocationId = "locations/non-existent"
        };

        var summaryList = new List<string>();
        var ctx = CreateContext(session, summaryList: summaryList);
        var result = await handler.ApplyAsync(change, ctx);
        Assert.True(result.Success);
        
        Assert.Contains(summaryList, m => m.Contains("created as orphan"));

        await session.SaveChangesAsync();

        var reloaded = await session.LoadAsync<Location>(locId);
        Assert.Equal(50, reloaded.DangerModifier);
        Assert.Equal("test-campaign", reloaded.CampaignName);
    }

    [Fact]
    public async Task LocationUpdate_Fails_WhenLocationNotFound_AndSuggestsFuzzyMatches()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        
        var targetId = "locations/target-" + Guid.NewGuid().ToString("N");
        await session.StoreAsync(new Location { Id = targetId, Name = "Target Tavern" });
        await session.SaveChangesAsync();

        // Wait for Indexing so suggestion lookup finds it
        var indexWaitStart = DateTime.UtcNow;
        while ((DateTime.UtcNow - indexWaitStart).TotalSeconds < 10)
        {
            var stats = _fixture.Store.Maintenance.Send(new Raven.Client.Documents.Operations.GetStatisticsOperation());
            if (stats.Indexes.Any(x => x.Name == "Location/Search" && x.IsStale == false))
            {
                break;
            }
            await Task.Delay(100);
        }

        var handler = new LocationUpdateHandler();
        var change = new LocationUpdate
        {
            LocationId = targetId.Substring(0, targetId.Length - 4) // Query substring of ID so targetId starts with it
        };

        var ctx = CreateContext(session);
        var result = await handler.ApplyAsync(change, ctx);

        Assert.False(result.Success);
        Assert.Contains("Did you mean", result.Message);
        Assert.Contains(targetId, result.Message);
    }

    [Fact]
    public async Task LocationUpdate_PatchesFieldsCorrectly()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        var locId = "locations/update-" + Guid.NewGuid();

        var loc = new Location
        {
            Id = locId,
            Name = "Before",
            Description = "Before Desc",
            DangerModifier = 0,
            Exits = [new LocationExit("locations/exit-to-remove", "Old Exit")],
            PointsOfInterest = ["Old PoI"],
            VisualTags = ["tag-remove"],
            DistinctiveFeatures = ["feat-remove"]
        };
        await session.StoreAsync(loc);
        await session.SaveChangesAsync();

        var handler = new LocationUpdateHandler();
        var change = new LocationUpdate
        {
            LocationId = locId,
            Name = "After",
            Description = "After Desc",
            AmbientCrowd = "", // Empty string -> sets to null
            DangerModifier = -99, // Clamped to -50
            AddExit = new LocationExit("locations/new-exit", "New Exit"),
            RemoveExitTarget = "locations/exit-to-remove",
            AddPointOfInterest = "New PoI",
            NewState = "Flooded",
            TagsToAdd = ["tag-add"],
            TagsToRemove = ["tag-remove"],
            FeaturesToAdd = ["feat-add"],
            FeaturesToRemove = ["feat-remove"]
        };

        var ctx = CreateContext(session, new Dictionary<string, Location> { { locId, loc } });
        var result = await handler.ApplyAsync(change, ctx);
        Assert.True(result.Success);

        await session.SaveChangesAsync();

        var reloaded = await session.LoadAsync<Location>(locId);
        Assert.Equal("After", reloaded.Name);
        Assert.Equal("After Desc", reloaded.Description);
        Assert.Null(reloaded.AmbientCrowd);
        Assert.Equal(-50, reloaded.DangerModifier);
        Assert.Single(reloaded.Exits);
        Assert.Equal("locations/new-exit", reloaded.Exits[0].TargetLocationId);
        Assert.Contains("Old PoI", reloaded.PointsOfInterest);
        Assert.Contains("New PoI", reloaded.PointsOfInterest);
        Assert.Equal("Flooded", reloaded.CurrentState);
        Assert.Contains("tag-add", reloaded.VisualTags);
        Assert.DoesNotContain("tag-remove", reloaded.VisualTags);
        Assert.Contains("feat-add", reloaded.DistinctiveFeatures);
        Assert.DoesNotContain("feat-remove", reloaded.DistinctiveFeatures);
    }

    [Fact]
    public async Task RumorCreateAndEvolves_Workflow()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        var rumorId = "rumors/test-" + Guid.NewGuid();
        var locId = "locations/rumor-loc-" + Guid.NewGuid();

        var createHandler = new RumorCreateHandler();
        var evolvesHandler = new RumorEvolvesHandler();

        var createChange = new RumorCreate
        {
            RumorId = rumorId,
            Subject = "The Dragon",
            Text = "A dragon was spotted.",
            RelatedLocationIds = [locId]
        };

        var ctx = CreateContext(session);
        var resultCreate = await createHandler.ApplyAsync(createChange, ctx);
        Assert.True(resultCreate.Success);

        await session.SaveChangesAsync();

        var created = await session.LoadAsync<Rumor>(rumorId);
        Assert.NotNull(created);
        Assert.Equal("The Dragon", created.Subject);
        Assert.Equal("A dragon was spotted.", created.CurrentText);
        Assert.Equal(RumorState.Nascent, created.State);
        Assert.Equal(locId, created.RegionLocationId);

        // Evolve rumor
        var evolveChange = new RumorEvolves
        {
            RumorId = rumorId,
            NewState = RumorState.Spreading,
            NewText = "The dragon burned a barn."
        };

        var resultEvolve = await evolvesHandler.ApplyAsync(evolveChange, ctx);
        Assert.True(resultEvolve.Success);

        await session.SaveChangesAsync();

        // Refresh session
        using var session2 = _fixture.Store.OpenAsyncSession();
        var evolved = await session2.LoadAsync<Rumor>(rumorId);
        Assert.NotNull(evolved);
        Assert.Equal(RumorState.Spreading, evolved.State);
        Assert.Equal("The dragon burned a barn.", evolved.CurrentText);
    }
}
