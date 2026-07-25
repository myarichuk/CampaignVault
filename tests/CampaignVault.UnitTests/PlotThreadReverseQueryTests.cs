using System;
using System.Linq;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Models;
using Raven.Client.Documents;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>
/// Tests for reverse plot thread queries (finding plots that reference a specific entity).
/// Uses RavenDB fixture and real indexed queries.
/// </summary>
[Collection("RavenDB")]
public class PlotThreadReverseQueryTests : IClassFixture<RavenDBFixture>
{
    private readonly RavenDBFixture _fixture;
    private readonly string _campaignName = "test-campaign-reverse-query";
    private readonly IDocumentStore _store;

    public PlotThreadReverseQueryTests(RavenDBFixture fixture)
    {
        _fixture = fixture;
        _store = fixture.Store;
        InitializeDataAsync().GetAwaiter().GetResult();
    }

    private async Task InitializeDataAsync()
    {
        using var session = _store.OpenAsyncSession();

        // Seed test entities
        var npc = new Character
        {
            Id = "chars/target-npc",
            Name = "Target NPC",
            CampaignName = _campaignName
        };
        await session.StoreAsync(npc);

        var otherNpc = new Character
        {
            Id = "chars/other-npc",
            Name = "Other NPC",
            CampaignName = _campaignName
        };
        await session.StoreAsync(otherNpc);

        var location = new Location
        {
            Id = "locations/test-location",
            Name = "Test Location",
            CampaignName = _campaignName
        };
        await session.StoreAsync(location);

        var item = new Item
        {
            Id = "items/test-item",
            Name = "Test Item",
            CampaignName = _campaignName
        };
        await session.StoreAsync(item);

        // Seed plot threads
        var thread1 = new PlotThread
        {
            Id = "plot-threads/thread-1",
            Title = "Thread 1 - NPC Reference",
            State = PlotThreadState.Active,
            CampaignName = _campaignName,
            InvolvedEntityIds = ["chars/target-npc", "factions/guild"],
            Clues = [],
            DayCreated = 0,
            LastUpdatedDay = 0
        };
        await session.StoreAsync(thread1);

        var thread2 = new PlotThread
        {
            Id = "plot-threads/thread-2",
            Title = "Thread 2 - Clue Reference",
            State = PlotThreadState.Dormant,
            CampaignName = _campaignName,
            InvolvedEntityIds = [],
            Clues =
            [
                new PlotClue("clue-1", "A clue", InvolvedEntityIds: ["chars/target-npc", "items/test-item"])
            ],
            DayCreated = 0,
            LastUpdatedDay = 0
        };
        await session.StoreAsync(thread2);

        var thread3 = new PlotThread
        {
            Id = "plot-threads/thread-3",
            Title = "Thread 3 - No Reference",
            State = PlotThreadState.Active,
            CampaignName = _campaignName,
            InvolvedEntityIds = ["chars/other-npc"],
            Clues = [],
            DayCreated = 0,
            LastUpdatedDay = 0
        };
        await session.StoreAsync(thread3);

        var thread4 = new PlotThread
        {
            Id = "plot-threads/thread-4",
            Title = "Thread 4 - Different Campaign",
            State = PlotThreadState.Active,
            CampaignName = "other-campaign",
            InvolvedEntityIds = ["chars/target-npc"],
            Clues = [],
            DayCreated = 0,
            LastUpdatedDay = 0
        };
        await session.StoreAsync(thread4);

        await session.SaveChangesAsync();

        // Give indexes time to catch up (RavenDB is usually fast)
        await Task.Delay(500);
    }

    [Fact]
    public async Task GetPlotThreadsReferencingEntity_FindsThreadLevelReferences()
    {
        // Arrange
        var repository = _fixture.CreateRepository();
        using var session = _fixture.Store.OpenAsyncSession();

        // Act: Query for plots referencing the target NPC
        var results = await repository.GetPlotThreadsReferencingEntityAsync(session, "chars/target-npc", _campaignName);

        // Assert: Should find thread-1 (direct reference) and thread-2 (clue reference)
        Assert.NotEmpty(results);
        Assert.True(results.Any(t => t.Id == "plot-threads/thread-1"), "Should find thread with direct NPC reference");
    }

    [Fact]
    public async Task GetPlotThreadsReferencingEntity_FindsClueReferences()
    {
        // Arrange
        var repository = _fixture.CreateRepository();
        using var session = _fixture.Store.OpenAsyncSession();

        // Act: Query for plots referencing the target NPC
        var results = await repository.GetPlotThreadsReferencingEntityAsync(session, "chars/target-npc", _campaignName);

        // Assert: Should find thread-2 which references it via a clue
        Assert.True(results.Any(t => t.Id == "plot-threads/thread-2"), "Should find thread with clue NPC reference");
    }

    [Fact]
    public async Task GetPlotThreadsReferencingEntity_ExcludesUnrelatedThreads()
    {
        // Arrange
        var repository = _fixture.CreateRepository();
        using var session = _fixture.Store.OpenAsyncSession();

        // Act: Query for plots referencing the target NPC
        var results = await repository.GetPlotThreadsReferencingEntityAsync(session, "chars/target-npc", _campaignName);

        // Assert: Should NOT find thread-3 which references a different NPC
        Assert.False(results.Any(t => t.Id == "plot-threads/thread-3"), "Should not find unrelated thread");
    }

    [Fact]
    public async Task GetPlotThreadsReferencingEntity_RespectsCampaignScoping()
    {
        // Arrange
        var repository = _fixture.CreateRepository();
        using var session = _fixture.Store.OpenAsyncSession();

        // Act: Query for plots in the test campaign
        var results = await repository.GetPlotThreadsReferencingEntityAsync(session, "chars/target-npc", _campaignName);

        // Assert: Should NOT find thread-4 from other-campaign
        Assert.False(results.Any(t => t.Id == "plot-threads/thread-4"), "Should not return plots from other campaigns");
    }

    [Fact]
    public async Task GetPlotThreadsReferencingEntity_FindsItemReferences()
    {
        // Arrange
        var repository = _fixture.CreateRepository();
        using var session = _fixture.Store.OpenAsyncSession();

        // Act: Query for plots referencing the test item
        var results = await repository.GetPlotThreadsReferencingEntityAsync(session, "items/test-item", _campaignName);

        // Assert: Should find thread-2 which references the item via a clue
        Assert.NotEmpty(results);
        Assert.Single(results);
        Assert.Equal("plot-threads/thread-2", results[0].Id);
    }

    [Fact]
    public async Task GetPlotThreadsReferencingEntity_ReturnsEmptyForNonexistentEntity()
    {
        // Arrange
        var repository = _fixture.CreateRepository();
        using var session = _fixture.Store.OpenAsyncSession();

        // Act: Query for plots referencing a non-existent entity
        var results = await repository.GetPlotThreadsReferencingEntityAsync(session, "chars/nonexistent", _campaignName);

        // Assert: Should return empty list, not error
        Assert.Empty(results);
    }

    [Fact]
    public async Task GetPlotThreadsReferencingEntity_ReturnsEmptyForEmptyEntityId()
    {
        // Arrange
        var repository = _fixture.CreateRepository();
        using var session = _fixture.Store.OpenAsyncSession();

        // Act: Query with empty ID
        var results = await repository.GetPlotThreadsReferencingEntityAsync(session, "", _campaignName);

        // Assert: Should return empty list
        Assert.Empty(results);
    }

    [Fact]
    public async Task GetPlotThreadsReferencingEntity_ReturnsEmptyForWhitespaceEntityId()
    {
        // Arrange
        var repository = _fixture.CreateRepository();
        using var session = _fixture.Store.OpenAsyncSession();

        // Act: Query with whitespace ID
        var results = await repository.GetPlotThreadsReferencingEntityAsync(session, "   ", _campaignName);

        // Assert: Should return empty list
        Assert.Empty(results);
    }
}
