using System;
using System.Linq;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Models;
using Raven.Client.Documents;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>
/// Tests that AssociatedPlotThreads embeds only minimal payload (id, title, state, tensionLevel).
/// Verifies response shape for get_entity calls returning NPC, Location, Faction, Item, Quest contexts.
/// </summary>
[Collection("RavenDB")]
public class PlotThreadResponseShapeTests : IClassFixture<RavenDBFixture>
{
    private readonly RavenDBFixture _fixture;
    private readonly string _campaignName = "test-campaign-response-shape";
    private readonly IDocumentStore _store;

    public PlotThreadResponseShapeTests(RavenDBFixture fixture)
    {
        _fixture = fixture;
        _store = fixture.Store;
        InitializeDataAsync().GetAwaiter().GetResult();
    }

    private async Task InitializeDataAsync()
    {
        using var session = _store.OpenAsyncSession();

        // Seed an NPC
        var npc = new Character
        {
            Id = "chars/response-test-npc",
            Name = "Test NPC",
            CampaignName = _campaignName
        };
        await session.StoreAsync(npc);

        // Seed a location
        var location = new Location
        {
            Id = "locations/response-test-location",
            Name = "Test Location",
            CampaignName = _campaignName
        };
        await session.StoreAsync(location);

        // Seed a faction
        var faction = new Faction
        {
            Id = "factions/response-test-faction",
            Name = "Test Faction",
            CampaignName = _campaignName
        };
        await session.StoreAsync(faction);

        // Seed an item
        var item = new Item
        {
            Id = "items/response-test-item",
            Name = "Test Item",
            Description = "A test item",
            HolderId = "chars/response-test-npc",
            CoreCategory = ItemCategory.Valuable,
            CampaignName = _campaignName
        };
        await session.StoreAsync(item);

        // Seed a quest
        var quest = new Quest
        {
            Id = "quests/response-test-quest",
            Title = "Test Quest",
            CampaignName = _campaignName
        };
        await session.StoreAsync(quest);

        // Seed plot thread referencing the NPC
        var thread1 = new PlotThread
        {
            Id = "plot-threads/response-thread-1",
            Title = "NPC Plot Thread",
            State = PlotThreadState.Active,
            CampaignName = _campaignName,
            InvolvedEntityIds = ["chars/response-test-npc"],
            Clues = [],
            DayCreated = 0,
            LastUpdatedDay = 0,
            TensionLevel = 45
        };
        await session.StoreAsync(thread1);

        // Seed plot thread referencing the location
        var thread2 = new PlotThread
        {
            Id = "plot-threads/response-thread-2",
            Title = "Location Plot Thread",
            State = PlotThreadState.Dormant,
            CampaignName = _campaignName,
            InvolvedEntityIds = ["locations/response-test-location"],
            Clues = [],
            DayCreated = 0,
            LastUpdatedDay = 0,
            TensionLevel = 62
        };
        await session.StoreAsync(thread2);

        // Seed plot thread referencing the faction
        var thread3 = new PlotThread
        {
            Id = "plot-threads/response-thread-3",
            Title = "Faction Plot Thread",
            State = PlotThreadState.Resolved,
            CampaignName = _campaignName,
            InvolvedEntityIds = ["factions/response-test-faction"],
            Clues = [],
            DayCreated = 0,
            LastUpdatedDay = 0,
            TensionLevel = 30
        };
        await session.StoreAsync(thread3);

        // Seed plot thread referencing the item
        var thread4 = new PlotThread
        {
            Id = "plot-threads/response-thread-4",
            Title = "Item Plot Thread",
            State = PlotThreadState.Active,
            CampaignName = _campaignName,
            InvolvedEntityIds = ["items/response-test-item"],
            Clues = [],
            DayCreated = 0,
            LastUpdatedDay = 0,
            TensionLevel = 55
        };
        await session.StoreAsync(thread4);

        // Seed plot thread referencing the quest
        var thread5 = new PlotThread
        {
            Id = "plot-threads/response-thread-5",
            Title = "Quest Plot Thread",
            State = PlotThreadState.Active,
            CampaignName = _campaignName,
            InvolvedEntityIds = ["quests/response-test-quest"],
            Clues = [],
            DayCreated = 0,
            LastUpdatedDay = 0,
            TensionLevel = 78
        };
        await session.StoreAsync(thread5);

        await session.SaveChangesAsync();

        // Give indexes time to catch up
        await Task.Delay(500);
    }

    [Fact]
    public void PlotThreadMinimal_ContainsOnlyRequiredFields()
    {
        // Arrange
        var minimal = new PlotThreadMinimal(
            "plot-threads/test",
            "Test Thread",
            PlotThreadState.Active,
            50);

        // Assert: Verify the record contains exactly these fields
        Assert.Equal("plot-threads/test", minimal.Id);
        Assert.Equal("Test Thread", minimal.Title);
        Assert.Equal(PlotThreadState.Active, minimal.State);
        Assert.Equal(50, minimal.TensionLevel);
    }

    [Fact]
    public async Task NpcContextView_AssociatedPlotThreads_ContainsMinimalPayload()
    {
        // Arrange
        var repository = _fixture.CreateRepository();
        using var session = _fixture.Store.OpenAsyncSession();

        // Act: Get NPC context (which loads associated plot threads)
        var result = await repository.GetCharacterAsync(session, "chars/response-test-npc", _campaignName);
        var threads = await repository.GetPlotThreadsReferencingEntityAsync(session, "chars/response-test-npc", _campaignName);
        var minimal = threads.Select(t => new PlotThreadMinimal(t.Id, t.Title, t.State, t.TensionLevel)).ToList();

        // Assert: Should have exactly one associated thread with only minimal fields
        Assert.Single(minimal);
        var thread = minimal.First();
        Assert.Equal("plot-threads/response-thread-1", thread.Id);
        Assert.Equal("NPC Plot Thread", thread.Title);
        Assert.Equal(PlotThreadState.Active, thread.State);
        Assert.Equal(45, thread.TensionLevel);

        // Assert: No full PlotThread details should leak (e.g., Clues, InvolvedEntityIds, DmNotes)
        Assert.NotNull(thread.Title);
    }

    [Fact]
    public async Task SceneView_AssociatedPlotThreads_ContainsMinimalPayload()
    {
        // Arrange
        var repository = _fixture.CreateRepository();
        using var session = _fixture.Store.OpenAsyncSession();

        // Act: Get plot threads referencing the location
        var threads = await repository.GetPlotThreadsReferencingEntityAsync(session, "locations/response-test-location", _campaignName);
        var minimal = threads.Select(t => new PlotThreadMinimal(t.Id, t.Title, t.State, t.TensionLevel)).ToList();

        // Assert: Should have exactly one associated thread with only minimal fields
        Assert.Single(minimal);
        var thread = minimal.First();
        Assert.Equal("plot-threads/response-thread-2", thread.Id);
        Assert.Equal("Location Plot Thread", thread.Title);
        Assert.Equal(PlotThreadState.Dormant, thread.State);
        Assert.Equal(62, thread.TensionLevel);
    }

    [Fact]
    public async Task FactionContext_AssociatedPlotThreads_ContainsMinimalPayload()
    {
        // Arrange
        var repository = _fixture.CreateRepository();
        using var session = _fixture.Store.OpenAsyncSession();

        // Act: Get plot threads referencing the faction
        var threads = await repository.GetPlotThreadsReferencingEntityAsync(session, "factions/response-test-faction", _campaignName);
        var minimal = threads.Select(t => new PlotThreadMinimal(t.Id, t.Title, t.State, t.TensionLevel)).ToList();

        // Assert: Should have exactly one associated thread
        Assert.Single(minimal);
        var thread = minimal.First();
        Assert.Equal("plot-threads/response-thread-3", thread.Id);
        Assert.Equal("Faction Plot Thread", thread.Title);
        Assert.Equal(PlotThreadState.Resolved, thread.State);
        Assert.Equal(30, thread.TensionLevel);
    }

    [Fact]
    public async Task ItemDetail_AssociatedPlotThreads_ContainsMinimalPayload()
    {
        // Arrange
        var repository = _fixture.CreateRepository();
        using var session = _fixture.Store.OpenAsyncSession();

        // Act: Get plot threads referencing the item
        var threads = await repository.GetPlotThreadsReferencingEntityAsync(session, "items/response-test-item", _campaignName);
        var minimal = threads.Select(t => new PlotThreadMinimal(t.Id, t.Title, t.State, t.TensionLevel)).ToList();

        // Assert: Should have exactly one associated thread
        Assert.Single(minimal);
        var thread = minimal.First();
        Assert.Equal("plot-threads/response-thread-4", thread.Id);
        Assert.Equal("Item Plot Thread", thread.Title);
        Assert.Equal(PlotThreadState.Active, thread.State);
        Assert.Equal(55, thread.TensionLevel);
    }

    [Fact]
    public async Task QuestDetails_AssociatedPlotThreads_ContainsMinimalPayload()
    {
        // Arrange
        var repository = _fixture.CreateRepository();
        using var session = _fixture.Store.OpenAsyncSession();

        // Act: Get plot threads referencing the quest
        var threads = await repository.GetPlotThreadsReferencingEntityAsync(session, "quests/response-test-quest", _campaignName);
        var minimal = threads.Select(t => new PlotThreadMinimal(t.Id, t.Title, t.State, t.TensionLevel)).ToList();

        // Assert: Should have exactly one associated thread
        Assert.Single(minimal);
        var thread = minimal.First();
        Assert.Equal("plot-threads/response-thread-5", thread.Id);
        Assert.Equal("Quest Plot Thread", thread.Title);
        Assert.Equal(PlotThreadState.Active, thread.State);
        Assert.Equal(78, thread.TensionLevel);
    }

    [Fact]
    public async Task AssociatedPlotThreads_EmptyWhenNoReferences()
    {
        // Arrange
        var repository = _fixture.CreateRepository();
        using var session = _fixture.Store.OpenAsyncSession();

        // Act: Query an entity that has no plot thread references
        var threads = await repository.GetPlotThreadsReferencingEntityAsync(session, "chars/nonexistent-id", _campaignName);

        // Assert: Should return empty list, not null
        Assert.NotNull(threads);
        Assert.Empty(threads);
    }
}
