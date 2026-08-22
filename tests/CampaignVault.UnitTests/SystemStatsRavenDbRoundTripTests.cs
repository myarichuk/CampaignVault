using System;
using System.Threading.Tasks;
using CampaignVault.Models;
using Raven.Client.Documents;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>
/// SystemExtension's polymorphism (base SystemExtension + Dnd5eExtension/Pf2eExtension) is declared
/// only via System.Text.Json's [JsonPolymorphic]/[JsonDerivedType] attributes (Character.cs).
/// RavenDB's document store serializes with Newtonsoft.Json, which has no idea what those attributes
/// mean — without SystemExtensionNewtonsoftConverter (wired in via RavenSerializationConventions),
/// every character document reloaded from RavenDB in a NEW session (i.e. every real request, since
/// each take_turn/get_entity call opens a fresh session) collapses SystemStats back to the bare base
/// type, silently discarding every dnd5e/pf2e-specific field. These tests exercise the real
/// RavenDbTestEnvironment store (same Newtonsoft path production uses) — not STJ directly — because
/// that's the only place this class of bug is observable.
/// </summary>
[Collection("RavenDB")]
public class SystemStatsRavenDbRoundTripTests : IClassFixture<RavenDBFixture>
{
    private readonly IDocumentStore _store;

    public SystemStatsRavenDbRoundTripTests(RavenDBFixture fixture)
    {
        _store = fixture.Store;
    }

    [Fact]
    public async Task Dnd5eExtension_SurvivesRoundTrip_ThroughANewSession()
    {
        var id = "chars/roundtrip-dnd5e-" + Guid.NewGuid().ToString("N");

        using (var writeSession = _store.OpenAsyncSession())
        {
            var character = new Character
            {
                Id = id,
                Name = "Roundtrip Test Fighter",
                CampaignName = TestCampaignDefaults.Slug,
                KeepAlive = true,
                SystemStats = new Dnd5eExtension
                {
                    ArmorClass = 16,
                    Strength = 16,
                    Dexterity = 12,
                    Constitution = 14,
                    HitDie = "d10",
                    Level = 5,
                    SkillModifiers = { ["Athletics"] = 6 },
                },
            };
            await writeSession.StoreAsync(character, id);
            await writeSession.SaveChangesAsync();
        }

        // A fresh session forces a real reload from RavenDB storage rather than returning the
        // same in-memory object the write session's identity map already holds — this is exactly
        // what happens between two separate take_turn calls in production.
        using var readSession = _store.OpenAsyncSession();
        var reloaded = await readSession.LoadAsync<Character>(id);

        Assert.NotNull(reloaded);
        var stats = Assert.IsType<Dnd5eExtension>(reloaded!.SystemStats);
        Assert.Equal(16, stats.ArmorClass);
        Assert.Equal(16, stats.Strength);
        Assert.Equal(12, stats.Dexterity);
        Assert.Equal(14, stats.Constitution);
        Assert.Equal("d10", stats.HitDie);
        Assert.Equal(5, stats.Level);
        Assert.Equal(6, stats.SkillModifiers["Athletics"]);
    }

    [Fact]
    public async Task Pf2eExtension_SurvivesRoundTrip_ThroughANewSession()
    {
        var id = "chars/roundtrip-pf2e-" + Guid.NewGuid().ToString("N");

        using (var writeSession = _store.OpenAsyncSession())
        {
            var character = new Character
            {
                Id = id,
                Name = "Roundtrip Test Ranger",
                CampaignName = TestCampaignDefaults.Slug,
                KeepAlive = true,
                SystemStats = new Pf2eExtension
                {
                    ArmorClass = 19,
                    DexterityMod = 4,
                    ConstitutionMod = 2,
                    ClassHpPerLevel = 10,
                    AncestryHp = 8,
                    Level = 4,
                    SkillModifiers = { ["Perception"] = 8 },
                },
            };
            await writeSession.StoreAsync(character, id);
            await writeSession.SaveChangesAsync();
        }

        using var readSession = _store.OpenAsyncSession();
        var reloaded = await readSession.LoadAsync<Character>(id);

        Assert.NotNull(reloaded);
        var stats = Assert.IsType<Pf2eExtension>(reloaded!.SystemStats);
        Assert.Equal(19, stats.ArmorClass);
        Assert.Equal(4, stats.DexterityMod);
        Assert.Equal(2, stats.ConstitutionMod);
        Assert.Equal(10, stats.ClassHpPerLevel);
        Assert.Equal(8, stats.AncestryHp);
        Assert.Equal(4, stats.Level);
        Assert.Equal(8, stats.SkillModifiers["Perception"]);
    }

    [Fact]
    public async Task BaseSystemExtension_SurvivesRoundTrip_WithoutBecomingARulesetType()
    {
        // A character with no ruleset-specific stats yet (e.g. narrative-only or not yet bootstrapped)
        // should stay the plain base type — the discriminator should be absent, not defaulted to
        // one of the ruleset extensions.
        var id = "chars/roundtrip-base-" + Guid.NewGuid().ToString("N");

        using (var writeSession = _store.OpenAsyncSession())
        {
            var character = new Character
            {
                Id = id,
                Name = "Roundtrip Test Bystander",
                CampaignName = TestCampaignDefaults.Slug,
                SystemStats = new SystemExtension { Willpower = 40 },
            };
            await writeSession.StoreAsync(character, id);
            await writeSession.SaveChangesAsync();
        }

        using var readSession = _store.OpenAsyncSession();
        var reloaded = await readSession.LoadAsync<Character>(id);

        Assert.NotNull(reloaded);
        Assert.IsType<SystemExtension>(reloaded!.SystemStats);
        Assert.Equal(40, reloaded.SystemStats.Willpower);
    }
}
