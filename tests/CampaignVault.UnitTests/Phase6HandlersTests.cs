using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CampaignVault.Tests;

[Collection("RavenDB")]
public class Phase6HandlersTests : IClassFixture<RavenDBFixture>
{
    private readonly RavenDBFixture _fixture;

    public Phase6HandlersTests(RavenDBFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task UpsertLocation_AutoLinksToParent_BothWays()
    {
        using var session = _fixture.Store.OpenAsyncSession();

        var parent = new Location { Id = "locations/parent", Name = "Parent", Exits = [] };
        await session.StoreAsync(parent);
        await session.SaveChangesAsync();

        var repository = _fixture.CreateRepository();
        var request = new LocationUpsertRequest
        {
            Id = "locations/child",
            Name = "Child",
            Description = "",
            ConnectedFromLocationId = "locations/parent",
            ConnectionDescription = "A sturdy oak door"
        };

        var child = await repository.UpsertLocationAsync(_fixture.CreateCampaignSession(session, "test-camp"), request);
        await session.SaveChangesAsync();

        // Check if child got the reverse exit to the parent (derived)
        Assert.Single(child.Exits);
        Assert.Equal("locations/parent", child.Exits[0].TargetLocationId);
        Assert.Equal("Leads back toward Parent (A sturdy oak door)", child.Exits[0].Description);

        using var verifySession = _fixture.Store.OpenAsyncSession();
        var reloadedParent = await verifySession.LoadAsync<Location>("locations/parent");
        Assert.NotNull(reloadedParent);
        Assert.Single(reloadedParent.Exits);
        Assert.Equal("locations/child", reloadedParent.Exits[0].TargetLocationId);
        Assert.Equal("A sturdy oak door", reloadedParent.Exits[0].Description);
    }

    [Fact]
    public async Task CharacterCreate_InitializesHpAndSystemStats_BasedOnRuleset()
    {
        using var session = _fixture.Store.OpenAsyncSession();

        var keys = new CampaignDocumentKeys();
        var configId = keys.Config("test-camp-hp");
        var config = new CampaignConfig { Id = configId, ActiveSystem = RulesetSystem.Dnd5e };
        await session.StoreAsync(config);
        await session.SaveChangesAsync();

        var handler = RulesetDataTestHelper.CreateCharacterCreateHandler();
        var change = new CharacterCreate
        {
            CharacterId = "characters/test-char-hp",
            Name = "Grog",
            MaxHp = 25
        };

        var dispatcher = new WorldChangeDispatcher([handler], new CampaignVault.Data.CampaignDocumentKeys(),
            NullLogger<WorldChangeDispatcher>.Instance);
        var ctx = new ChangeContext(session, new Dictionary<string, Character>(), new Dictionary<string, Item>(),
            new Dictionary<string, Location>(), new Dictionary<string, Faction>(), new Dictionary<string, Quest>(),
            NullLogger.Instance,
            [], dispatcher, null, "test-camp-hp");

        var result = await handler.ApplyAsync(change, ctx);
        Assert.True(result.Success);

        await session.SaveChangesAsync();

        var character = await session.LoadAsync<Character>("characters/test-char-hp");
        Assert.NotNull(character);
        Assert.Equal(25, character.MaxHp);
        Assert.Equal(25, character.CurrentHp);
        Assert.IsType<Dnd5eExtension>(character.SystemStats);
    }

    [Fact]
    public async Task CharacterCreate_OnExistingId_SurfacesStructuredEntityCollision()
    {
        using var session = _fixture.Store.OpenAsyncSession();

        var keys = new CampaignDocumentKeys();
        var configId = keys.Config("test-camp-collision");
        var config = new CampaignConfig { Id = configId, ActiveSystem = RulesetSystem.Dnd5e };
        await session.StoreAsync(config);
        await session.StoreAsync(new Character { Id = "chars/already-there", Name = "Original", MaxHp = 10, CurrentHp = 10 });
        await session.SaveChangesAsync();

        var handler = RulesetDataTestHelper.CreateCharacterCreateHandler();
        var dispatcher = new WorldChangeDispatcher([handler], keys, NullLogger<WorldChangeDispatcher>.Instance);

        // Supplying the "characters/" alias here proves it gets normalized to the canonical "chars/"
        // form before collision-checking against the already-stored "chars/already-there" entity.
        var result = await dispatcher.DispatchAsync(
            session,
            [new CharacterCreate { CharacterId = "characters/already-there", Name = "Original", MaxHp = 5 }],
            "test-camp-collision",
            () => Task.FromResult(new CampaignTime()),
            () => Task.FromResult(new Dictionary<string, string>()),
            _ => Task.CompletedTask);

        Assert.True(result.Success);
        Assert.Contains("chars/already-there", result.EntityCollisions);
        Assert.Contains(result.Summary, s => s.Contains("already exists"));
    }

    [Fact]
    public async Task RelationshipChange_ToNonexistentTarget_FailsWithHint()
    {
        using var session = _fixture.Store.OpenAsyncSession();

        var sourceId = "chars/relationship-source-" + Guid.NewGuid();
        await session.StoreAsync(new Character { Id = sourceId, Name = "Source" });
        await session.SaveChangesAsync();

        var handler = new RelationshipChangeHandler();
        var dispatcher = new WorldChangeDispatcher([handler], new CampaignDocumentKeys(), NullLogger<WorldChangeDispatcher>.Instance);

        // Supplying the "characters/" alias for TargetId proves it gets normalized to "chars/ghost"
        // (surfaced in the failure message) before the not-found check.
        var result = await dispatcher.DispatchAsync(
            session,
            [new RelationshipChange { CharacterId = sourceId, TargetId = "characters/ghost", Delta = 10, Reason = "test" }],
            "test-camp-relationship",
            () => Task.FromResult(new CampaignTime()),
            () => Task.FromResult(new Dictionary<string, string>()),
            _ => Task.CompletedTask);

        Assert.False(result.Success);
        Assert.Contains(result.Summary, s => s.Contains("chars/ghost") && s.Contains("not found"));
    }

    [Fact]
    public async Task LocationUpdate_AddExit_ToNonexistentTarget_WarnsButSucceeds()
    {
        using var session = _fixture.Store.OpenAsyncSession();

        var locId = "locations/add-exit-source-" + Guid.NewGuid();
        await session.StoreAsync(new Location { Id = locId, Name = "Source", Exits = [] });
        await session.SaveChangesAsync();

        var handler = new LocationUpdateHandler();
        var dispatcher = new WorldChangeDispatcher([handler], new CampaignDocumentKeys(), NullLogger<WorldChangeDispatcher>.Instance);

        var result = await dispatcher.DispatchAsync(
            session,
            [new LocationUpdate { LocationId = locId, AddExit = new LocationExit("locations/ghost-target", "A door") }],
            "test-camp-exit",
            () => Task.FromResult(new CampaignTime()),
            () => Task.FromResult(new Dictionary<string, string>()),
            _ => Task.CompletedTask);

        Assert.True(result.Success);
        Assert.Contains(result.Summary, s => s.Contains("locations/ghost-target") && s.Contains("does not currently exist"));

        var reloaded = await session.LoadAsync<Location>(locId);
        Assert.Single(reloaded.Exits);
        Assert.Equal("locations/ghost-target", reloaded.Exits[0].TargetLocationId);
    }

    [Fact]
    public async Task ArchiveEntity_ArchivesAndRestoresQuest()
    {
        using var session = _fixture.Store.OpenAsyncSession();

        var questId = "quests/archive-target-" + Guid.NewGuid();
        await session.StoreAsync(new Quest { Id = questId, Title = "Archivable Quest", IsArchived = false });
        await session.SaveChangesAsync();

        var handler = new ArchiveEntityChangeHandler();
        var dispatcher = new WorldChangeDispatcher([handler], new CampaignDocumentKeys(), NullLogger<WorldChangeDispatcher>.Instance);

        var archiveResult = await dispatcher.DispatchAsync(
            session,
            [new ArchiveEntityChange { EntityType = ArchivableEntityType.Quest, EntityId = questId, Archived = true }],
            "test-camp-archive",
            () => Task.FromResult(new CampaignTime()),
            () => Task.FromResult(new Dictionary<string, string>()),
            _ => Task.CompletedTask);

        Assert.True(archiveResult.Success);
        var afterArchive = await session.LoadAsync<Quest>(questId);
        Assert.True(afterArchive.IsArchived);

        var restoreResult = await dispatcher.DispatchAsync(
            session,
            [new ArchiveEntityChange { EntityType = ArchivableEntityType.Quest, EntityId = questId, Archived = false }],
            "test-camp-archive",
            () => Task.FromResult(new CampaignTime()),
            () => Task.FromResult(new Dictionary<string, string>()),
            _ => Task.CompletedTask);

        Assert.True(restoreResult.Success);
        var afterRestore = await session.LoadAsync<Quest>(questId);
        Assert.False(afterRestore.IsArchived);
    }

    [Fact]
    public async Task ArchiveEntity_RejectsCharacterWithExplanatoryMessage()
    {
        using var session = _fixture.Store.OpenAsyncSession();

        var handler = new ArchiveEntityChangeHandler();
        var dispatcher = new WorldChangeDispatcher([handler], new CampaignDocumentKeys(), NullLogger<WorldChangeDispatcher>.Instance);

        var result = await dispatcher.DispatchAsync(
            session,
            [new ArchiveEntityChange { EntityType = ArchivableEntityType.Character, EntityId = "chars/whoever" }],
            "test-camp-archive",
            () => Task.FromResult(new CampaignTime()),
            () => Task.FromResult(new Dictionary<string, string>()),
            _ => Task.CompletedTask);

        Assert.False(result.Success);
        Assert.Contains(result.Summary, s => s.Contains("Characters cannot be archived"));
    }

    [Fact]
    public async Task ArchivedPlotThread_ExcludedFromActivePlotThreadsListing()
    {
        var repo = _fixture.CreateRepository();
        var slug = "archive-readpath-" + Guid.NewGuid().ToString("N")[..8];
        var visibleId = "plot-threads/visible-" + Guid.NewGuid();
        var archivedId = "plot-threads/archived-" + Guid.NewGuid();

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            await session.StoreAsync(new PlotThread
            {
                Id = visibleId, Title = "Visible Thread", State = PlotThreadState.Active,
                CampaignName = slug, IsArchived = false
            });
            await session.StoreAsync(new PlotThread
            {
                Id = archivedId, Title = "Archived Thread", State = PlotThreadState.Active,
                CampaignName = slug, IsArchived = true
            });
            await session.SaveChangesAsync();
        }

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            var active = await repo.GetActivePlotThreadsAsync(session, slug);

            Assert.Contains(active, t => t.Id == visibleId);
            Assert.DoesNotContain(active, t => t.Id == archivedId);
        }
    }
}
