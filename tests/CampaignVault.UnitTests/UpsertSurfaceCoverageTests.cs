using System;
using System.Text.Json;
using System.Threading.Tasks;
using CampaignVault.Models;
using CampaignVault.Tools;
using Xunit;

namespace CampaignVault.Tests;

[Collection("RavenDB")]
public class UpsertSurfaceCoverageTests : IClassFixture<RavenDBFixture>
{
    private readonly RavenDBFixture _fixture;

    public UpsertSurfaceCoverageTests(RavenDBFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task UpsertFaction_RoundTrips()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        var repository = _fixture.CreateRepository();

        var faction = await repository.UpsertFactionAsync(session,
            new FactionUpsertRequest { Id = "factions/thieves", Name = "Thieves Guild" }, "test-camp");
        await session.SaveChangesAsync();

        Assert.Equal("Thieves Guild", faction.Name);

        using var verifySession = _fixture.Store.OpenAsyncSession();
        var reloaded = await verifySession.LoadAsync<Faction>("factions/thieves");
        Assert.NotNull(reloaded);
        Assert.Equal("Thieves Guild", reloaded.Name);
    }

    [Fact]
    public async Task UpsertQuest_RoundTrips()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        var repository = _fixture.CreateRepository();

        var quest = await repository.UpsertQuestAsync(session,
            new QuestUpsertRequest { Id = "quests/find-ring", Title = "Find the Ring" }, "test-camp");
        await session.SaveChangesAsync();

        Assert.Equal("Find the Ring", quest.Title);

        using var verifySession = _fixture.Store.OpenAsyncSession();
        var reloaded = await verifySession.LoadAsync<Quest>("quests/find-ring");
        Assert.NotNull(reloaded);
        Assert.Equal("Find the Ring", reloaded.Title);
    }

    [Fact]
    public async Task UpsertRumor_RoundTrips()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        var repository = _fixture.CreateRepository();

        var rumor = await repository.UpsertRumorAsync(session,
            new RumorUpsertRequest
            {
                Id = "rumors/dragon-sighting",
                Subject = "Dragon sighting",
                CurrentText = "A dragon was seen over the mountains."
            }, "test-camp");
        await session.SaveChangesAsync();

        Assert.Equal("Dragon sighting", rumor.Subject);

        using var verifySession = _fixture.Store.OpenAsyncSession();
        var reloaded = await verifySession.LoadAsync<Rumor>("rumors/dragon-sighting");
        Assert.NotNull(reloaded);
        Assert.Equal("Dragon sighting", reloaded.Subject);
    }

    [Fact]
    public async Task UpsertCustomSpell_RoundTrips_AndIsQueryableForItsSystem()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        var repository = _fixture.CreateRepository();

        await repository.UpsertCustomSpellAsync(session,
            new CustomSpellUpsertRequest
            {
                Id = "spells/homebrew-firebolt",
                Name = "Homebrew Firebolt",
                System = RulesetSystem.Dnd5e,
                Level = 1
            }, "test-camp");
        session.Advanced.WaitForIndexesAfterSaveChanges(timeout: TimeSpan.FromSeconds(5), throwOnTimeout: true);
        await session.SaveChangesAsync();

        using var verifySession = _fixture.Store.OpenAsyncSession();
        var spells = await repository.GetCustomSpellsForSystemAsync(verifySession, RulesetSystem.Dnd5e, "test-camp");

        Assert.Contains(spells, s => s.Id == "spells/homebrew-firebolt" && s.Name == "Homebrew Firebolt");
    }

    [Fact]
    public async Task UpsertCustomFeat_RoundTrips_AndIsQueryableForItsSystem()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        var repository = _fixture.CreateRepository();

        await repository.UpsertCustomFeatAsync(session,
            new CustomFeatUpsertRequest
            {
                Id = "feats/homebrew-toughness",
                Name = "Homebrew Toughness",
                System = RulesetSystem.Dnd5e
            }, "test-camp");
        session.Advanced.WaitForIndexesAfterSaveChanges(timeout: TimeSpan.FromSeconds(5), throwOnTimeout: true);
        await session.SaveChangesAsync();

        using var verifySession = _fixture.Store.OpenAsyncSession();
        var feats = await repository.GetCustomFeatsForSystemAsync(verifySession, RulesetSystem.Dnd5e, "test-camp");

        Assert.Contains(feats, f => f.Id == "feats/homebrew-toughness" && f.Name == "Homebrew Toughness");
    }

    [Fact]
    public async Task UpsertLocation_Archived_IsExcludedFromUnifiedSearch_ButStillLoadableById()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        var repository = _fixture.CreateRepository();

        await repository.UpsertLocationAsync(session,
            new LocationUpsertRequest
            {
                Id = "locations/forgotten-crypt",
                Name = "ZzArchivedCryptUnique",
                Description = "A crypt nobody visits anymore.",
                IsArchived = true
            }, "test-camp");
        await session.SaveChangesAsync();

        using var verifySession = _fixture.Store.OpenAsyncSession();
        var results = await repository.UnifiedSearchAsync(verifySession, "ZzArchivedCryptUnique", "test-camp");
        Assert.DoesNotContain(results, r => r is SearchMatch { EntityType: "location", Match: LocationSearchSummary loc } && loc.Id == "locations/forgotten-crypt");

        var reloaded = await verifySession.LoadAsync<Location>("locations/forgotten-crypt");
        Assert.NotNull(reloaded);
        Assert.True(reloaded.IsArchived);
    }

    [Fact]
    public async Task UpsertItem_Archived_IsExcludedFromScene_ButStillLoadableById()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        var repository = _fixture.CreateRepository();

        await session.StoreAsync(new Location
        {
            Id = "locations/scene-test-room", Name = "Scene Test Room", CampaignName = "test-camp"
        });
        await session.SaveChangesAsync();

        await repository.UpsertItemAsync(session,
            new ItemUpsertRequest
            {
                Id = "items/forgotten-coin",
                Name = "Forgotten Coin",
                Description = "A dusty old coin.",
                HolderId = "locations/scene-test-room",
                IsArchived = true
            }, "test-camp");
        await session.SaveChangesAsync();

        using var verifySession = _fixture.Store.OpenAsyncSession();
        var scene = await repository.GetSceneAsync(verifySession, "locations/scene-test-room", "test-camp");
        Assert.DoesNotContain(scene.VisibleItems, i => i.Id == "items/forgotten-coin");

        var reloaded = await verifySession.LoadAsync<Item>("items/forgotten-coin");
        Assert.NotNull(reloaded);
        Assert.True(reloaded.IsArchived);
    }

    [Fact]
    public void Commit_WithLegacyCharacterCreateDiscriminator_NoLongerParses()
    {
        const string payload = """
                               [
                                 {
                                   "$type": "character_create",
                                   "characterId": "characters/ghost",
                                   "name": "Ghost"
                                 }
                               ]
                               """;

        using var doc = JsonDocument.Parse(payload);
        var ok = CommitChangesParser.TryParse(doc.RootElement, out var parsed, out var error);

        Assert.False(ok);
        Assert.Null(parsed);
    }

    [Fact]
    public void Commit_WithLegacyLocationCreateDiscriminator_NoLongerParses()
    {
        const string payload = """
                               [
                                 {
                                   "$type": "location_create",
                                   "locationId": "locations/ghost-town",
                                   "name": "Ghost Town"
                                 }
                               ]
                               """;

        using var doc = JsonDocument.Parse(payload);
        var ok = CommitChangesParser.TryParse(doc.RootElement, out var parsed, out var error);

        Assert.False(ok);
        Assert.Null(parsed);
    }

    [Fact]
    public void Commit_WithLegacyFactionCreateDiscriminator_NoLongerParses()
    {
        const string payload = """
                               [
                                 {
                                   "$type": "faction_create",
                                   "factionId": "factions/ghost-guild",
                                   "name": "Ghost Guild"
                                 }
                               ]
                               """;

        using var doc = JsonDocument.Parse(payload);
        var ok = CommitChangesParser.TryParse(doc.RootElement, out var parsed, out var error);

        Assert.False(ok);
        Assert.Null(parsed);
    }
}
