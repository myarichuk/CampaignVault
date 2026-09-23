using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Data.Pressure;
using CampaignVault.Data.Pressure.Contributors;
using CampaignVault.Models;
using Raven.Client.Documents.Session;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>
/// P0-3 acceptance: seed malformed docs (null-Name NPC, null-Traits NPC) → contributor
/// returns ENGINE warnings with fix JSON and does not throw; null collections auto-normalize.
/// Shaped like the IncompleteSystemStats/CharacterDistress contributor tests.
/// </summary>
[Collection("RavenDB")]
public class EntityIntegrityPressureTests : IClassFixture<RavenDBFixture>
{
    private readonly RavenDBFixture _fixture;

    public EntityIntegrityPressureTests(RavenDBFixture fixture) => _fixture = fixture;

    private static string NewSlug(string prefix) => prefix + "-" + Guid.NewGuid().ToString("N")[..8];

    [Fact]
    public async Task Evaluate_SurfacesEngineWarnings_WithFixJson_AndDoesNotThrow()
    {
        var slug = NewSlug("integrity");
        var keys = new CampaignDocumentKeys();
        using var session = _fixture.Store.OpenAsyncSession();

        var config = new CampaignConfig
        {
            Id = keys.Config(slug),
            ActiveSystem = RulesetSystem.Dnd5e,
        };
        await session.StoreAsync(config);

        var nullNameId = $"chars/{slug}-noname";
        var nullTraitsId = $"chars/{slug}-notraits";
        await session.StoreAsync(new Character
        {
            Id = nullNameId,
            Name = null!,
            CampaignName = slug,
            KeepAlive = true,
            MaxHp = 10,
            CurrentHp = 10,
        });
        await session.StoreAsync(new Character
        {
            Id = nullTraitsId,
            Name = "Ragged Scout",
            CampaignName = slug,
            KeepAlive = true,
            MaxHp = 10,
            CurrentHp = 10,
            Psychology = new PsychologyProfile
            {
                Traits = null!,
                Memories = new Dictionary<string, MemoryNode>
                {
                    ["cellar"] = new MemoryNode { Topic = null!, Details = "Dark and damp." },
                },
            },
            VisualTags = new List<string> { null! },
            CurrentLocationId = $"locations/{slug}-missing",
        });
        await session.SaveChangesAsync();

        await WaitForIndexAsync(session, slug, [nullNameId, nullTraitsId]);

        var time = new CampaignTime { Id = keys.StateTime(slug), TotalDaysElapsed = 1 };
        var loadedConfig = await session.LoadAsync<CampaignConfig>(keys.Config(slug));
        Assert.NotNull(loadedConfig);

        var contributor = new EntityIntegrityPressureContributor();
        var pressures = (await contributor.EvaluateAsync(new PressureContext(slug, time, loadedConfig, session))).ToList();

        var nameWarning = pressures.FirstOrDefault(p =>
            p.EntityId == nullNameId && p.GroupingKey == EntityIntegrityPressureContributor.CharacterNameGroupingKey);
        Assert.NotNull(nameWarning);
        Assert.Equal(PressureSeverity.EngineWarning, nameWarning.Severity);
        // Nudge-only: the engine must not invent identity, so no commit-shaped fix for a missing name.
        Assert.True(string.IsNullOrWhiteSpace(nameWarning.SuggestedCommitJson));

        var topicWarning = pressures.FirstOrDefault(p =>
            p.EntityId == nullTraitsId && p.GroupingKey == EntityIntegrityPressureContributor.CharacterMemoryTopicGroupingKey);
        Assert.NotNull(topicWarning);
        Assert.Equal(PressureSeverity.EngineWarning, topicWarning.Severity);
        Assert.Contains("knowledge_update", topicWarning.SuggestedCommitJson);

        var locationWarning = pressures.FirstOrDefault(p =>
            p.EntityId == nullTraitsId && p.GroupingKey == EntityIntegrityPressureContributor.CharacterLocationGroupingKey);
        Assert.NotNull(locationWarning);
        Assert.Equal(PressureSeverity.EngineWarning, locationWarning.Severity);
        Assert.Contains($"locations/{slug}-missing", locationWarning.Text);
    }

    [Fact]
    public async Task Evaluate_AutoNormalizesNullCollections_AndFlagsBlankLocationName()
    {
        var slug = NewSlug("integrity-norm");
        var keys = new CampaignDocumentKeys();
        using var session = _fixture.Store.OpenAsyncSession();

        await session.StoreAsync(new CampaignConfig
        {
            Id = keys.Config(slug),
            ActiveSystem = RulesetSystem.Dnd5e,
        });

        var charId = $"chars/{slug}-ragged";
        var locId = $"locations/{slug}-nameless";
        var nullNameId = $"chars/{slug}-noname";
        await session.StoreAsync(new Location
        {
            Id = locId,
            Name = null!,
            Description = "A place.",
            CampaignName = slug,
        });
        await session.StoreAsync(new Character
        {
            Id = charId,
            Name = "Ragged",
            CampaignName = slug,
            KeepAlive = true,
            MaxHp = 10,
            CurrentHp = 10,
            Psychology = null!,
            Social = null!,
            Needs = null!,
            SystemStats = null!,
            VisualTags = new List<string> { null!, "muddy" },
            CurrentLocationId = locId,
        });
        await session.StoreAsync(new Character
        {
            Id = nullNameId,
            Name = "   ",
            CampaignName = slug,
            KeepAlive = true,
        });
        await session.SaveChangesAsync();

        await WaitForIndexAsync(session, slug, [charId, nullNameId]);

        var time = new CampaignTime { Id = keys.StateTime(slug), TotalDaysElapsed = 1 };
        var loadedConfig = await session.LoadAsync<CampaignConfig>(keys.Config(slug));
        Assert.NotNull(loadedConfig);

        var pressures = (await new EntityIntegrityPressureContributor().EvaluateAsync(
            new PressureContext(slug, time, loadedConfig, session, RequestedLocationId: locId))).ToList();

        // Anchored location exists → no dangling-location warning for the character …
        Assert.DoesNotContain(pressures, p =>
            p.GroupingKey == EntityIntegrityPressureContributor.CharacterLocationGroupingKey);
        // … but its blank name is flagged with a location_update fix.
        var locWarning = pressures.FirstOrDefault(p =>
            p.EntityId == locId && p.GroupingKey == EntityIntegrityPressureContributor.LocationNameGroupingKey);
        Assert.NotNull(locWarning);
        Assert.Equal(PressureSeverity.EngineWarning, locWarning.Severity);
        Assert.Contains("location_update", locWarning.SuggestedCommitJson);

        await session.SaveChangesAsync();

        using (var verify = _fixture.Store.OpenAsyncSession())
        {
            var reloaded = await verify.LoadAsync<Character>(charId);
            Assert.NotNull(reloaded);
            Assert.NotNull(reloaded.Psychology);
            Assert.NotNull(reloaded.Social);
            Assert.NotNull(reloaded.Needs);
            Assert.NotNull(reloaded.SystemStats);
            Assert.NotNull(reloaded.Psychology.Traits);
            Assert.Empty(reloaded.Psychology.Traits);
            Assert.Equal(new[] { "muddy" }, reloaded.VisualTags.ToArray());

            var reloadedLoc = await verify.LoadAsync<Location>(locId);
            Assert.NotNull(reloadedLoc);
            // Nudge-only: blank location name is never auto-fixed.
            Assert.True(string.IsNullOrWhiteSpace(reloadedLoc.Name));
        }

        var repo = _fixture.CreateRepository();
        using (var coverageSession = _fixture.Store.OpenAsyncSession())
        {
            var coverage = await repo.BuildSeedCoverageAsync(coverageSession, slug, null);
            Assert.Contains(coverage.Gaps, g => g.Contains("characters with integrity warnings"));
        }
    }

    private static async Task WaitForIndexAsync(
        IAsyncDocumentSession session, string slug, string[] ids)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var found = await PressureQueryHelper.QueryCombatantCharactersAsync(session, slug, 100);
            if (ids.All(id => found.Any(c => c.Id == id)))
            {
                return;
            }

            await Task.Delay(100);
        }
    }
}
