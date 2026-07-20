using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CampaignVault.Models;
using Xunit;

namespace CampaignVault.Tests;

[Collection("RavenDB")]
public class WorldBuildToolsTests : IClassFixture<RavenDBFixture>
{
    private readonly RavenDBFixture _fixture;

    public WorldBuildToolsTests(RavenDBFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task WorldBuild_SeedsMultipleKinds_AtomicallyInOneCall()
    {
        var worldBuilder = TestCampaignToolsFactory.CreateWorldBuilderTools(_fixture);
        var slug = "world-build-" + Guid.NewGuid().ToString("N")[..8];

        var batch = new WorldBuildBatch
        {
            Locations =
            [
                new LocationUpsertRequest { Id = "locations/wb-tavern", Name = "The Rusty Nail", Description = "A tavern.", Type = LocationType.Building },
            ],
            Factions =
            [
                new FactionUpsertRequest { Id = "factions/wb-guild", Name = "Merchants Guild" },
            ],
            Characters =
            [
                new CharacterUpsertRequest { Id = "chars/wb-valen", Name = "Valen", CurrentLocationId = "locations/wb-tavern", IsPc = true },
            ],
            Items =
            [
                new ItemUpsertRequest { Id = "items/wb-sword", Name = "Sword", Description = "A blade.", HolderId = "chars/wb-valen", CoreCategory = ItemCategory.Weapon },
            ],
            Quests =
            [
                // Forward reference to a faction that isn't in this batch — should warn, not fail.
                new QuestUpsertRequest { Id = "quests/wb-quest", Title = "Find the Relic", GiverId = "chars/wb-valen", RelatedFactionIds = ["factions/does-not-exist-yet"] },
            ],
        };

        var result = await worldBuilder.WorldBuild(batch, slug);

        Assert.True(result.Success, result.Summary);
        Assert.NotNull(result.Data);
        Assert.Equal(1, result.Data.Kinds["locations"].Created);
        Assert.Equal(1, result.Data.Kinds["factions"].Created);
        Assert.Equal(1, result.Data.Kinds["characters"].Created);
        Assert.Equal(1, result.Data.Kinds["items"].Created);
        Assert.Equal(1, result.Data.Kinds["quests"].Created);
        Assert.Contains(result.Data.Warnings, w => w.Contains("factions/does-not-exist-yet"));
    }

    [Fact]
    public async Task WorldBuild_BadEntryInSecondCharacter_RollsBackEntireBatch()
    {
        var worldBuilder = TestCampaignToolsFactory.CreateWorldBuilderTools(_fixture);
        var slug = "world-build-rollback-" + Guid.NewGuid().ToString("N")[..8];

        var batch = new WorldBuildBatch
        {
            Characters =
            [
                new CharacterUpsertRequest { Id = "chars/wb-rollback-good", Name = "Good One" },
                new CharacterUpsertRequest { Id = "", Name = "Bad One" }, // Missing Id -> ArgumentException
            ],
        };

        var result = await worldBuilder.WorldBuild(batch, slug);

        Assert.False(result.Success);
        Assert.Contains("characters[1]", result.Summary);

        using var session = _fixture.Store.OpenAsyncSession();
        var rolledBack = await session.LoadAsync<Character>("chars/wb-rollback-good");
        Assert.Null(rolledBack);
    }

    [Fact]
    public async Task WorldBuild_EmptyBatch_Fails()
    {
        var worldBuilder = TestCampaignToolsFactory.CreateWorldBuilderTools(_fixture);
        var slug = "world-build-empty-" + Guid.NewGuid().ToString("N")[..8];

        var result = await worldBuilder.WorldBuild(new WorldBuildBatch(), slug);

        Assert.False(result.Success);
        Assert.Equal("InvalidArgument", result.Error);
    }

    [Fact]
    public async Task WorldBuild_OverEntryCap_Fails()
    {
        var worldBuilder = TestCampaignToolsFactory.CreateWorldBuilderTools(_fixture);
        var slug = "world-build-cap-" + Guid.NewGuid().ToString("N")[..8];

        var batch = new WorldBuildBatch
        {
            Lore = Enumerable.Range(0, 101)
                .Select(i => new LoreUpsertRequest { Id = $"lore/wb-cap-{i}", Title = $"Entry {i}", Content = "..." })
                .ToList(),
        };

        var result = await worldBuilder.WorldBuild(batch, slug);

        Assert.False(result.Success);
        Assert.Equal("InvalidArgument", result.Error);
        Assert.Contains("100-entry cap", result.Summary);
    }

    [Fact]
    public async Task WorldBuild_NonCanonicalCharacterId_IsNormalizedAndWarned()
    {
        var worldBuilder = TestCampaignToolsFactory.CreateWorldBuilderTools(_fixture);
        var slug = "world-build-normalize-" + Guid.NewGuid().ToString("N")[..8];

        var batch = new WorldBuildBatch
        {
            Characters = [new CharacterUpsertRequest { Id = "characters/wb-alias", Name = "Aliased" }],
        };

        var result = await worldBuilder.WorldBuild(batch, slug);

        Assert.True(result.Success, result.Summary);
        Assert.Contains(result.Data!.Warnings, w => w.Contains("was normalized to 'chars/wb-alias'"));

        using var session = _fixture.Store.OpenAsyncSession();
        var stored = await session.LoadAsync<Character>("chars/wb-alias");
        Assert.NotNull(stored);
    }

    [Fact]
    public async Task WorldBuild_ThenGetWorldState_ReportsSeedCoverage()
    {
        var repo = _fixture.CreateRepository();
        var tools = TestCampaignToolsFactory.Create(_fixture, repository: repo);
        var worldBuilder = TestCampaignToolsFactory.CreateWorldBuilderTools(_fixture, repo);
        var slug = "world-build-coverage-" + Guid.NewGuid().ToString("N")[..8];
        await TestCampaignDefaults.EnsureExistsAsync(tools, slug);

        var before = await tools.GetWorldState(campaignName: slug);
        Assert.True(before.Success, before.Summary);
        Assert.NotNull(before.Data!.SeedCoverage);
        Assert.Equal(0, before.Data.SeedCoverage!.Locations);
        Assert.Contains("no locations yet", before.Data.SeedCoverage.Gaps);
        Assert.Contains("no PC characters yet", before.Data.SeedCoverage.Gaps);

        var batch = new WorldBuildBatch
        {
            Locations = [new LocationUpsertRequest { Id = "locations/wb-cov-start", Name = "Start", Description = "A place.", Type = LocationType.Region, ClimateZone = ClimateZone.Temperate }],
            Characters = [new CharacterUpsertRequest { Id = "chars/wb-cov-pc", Name = "PC", IsPc = true, CurrentLocationId = "locations/wb-cov-start" }],
        };
        var buildResult = await worldBuilder.WorldBuild(batch, slug);
        Assert.True(buildResult.Success, buildResult.Summary);

        var after = await tools.GetWorldState("locations/wb-cov-start", slug);
        Assert.True(after.Success, after.Summary);
        Assert.Equal(1, after.Data!.SeedCoverage!.Locations);
        Assert.Equal(1, after.Data.SeedCoverage.PcCharacters);
        Assert.DoesNotContain("no locations yet", after.Data.SeedCoverage.Gaps);
        Assert.DoesNotContain("no PC characters yet", after.Data.SeedCoverage.Gaps);
        Assert.DoesNotContain(after.Data.SeedCoverage.Gaps, g => g.Contains("climateZone"));
    }

    [Fact]
    public async Task WorldBuild_AliasedCharacterId_ThenEquipAndTransferItem_NormalizesAndClearsPersistence()
    {
        var repo = _fixture.CreateRepository();
        var worldBuilder = TestCampaignToolsFactory.CreateWorldBuilderTools(_fixture, repo);
        var slug = "world-build-transfer-" + Guid.NewGuid().ToString("N")[..8];

        var batch = new WorldBuildBatch
        {
            Locations =
            [
                new LocationUpsertRequest { Id = "locations/wb-xfer-start", Name = "Start", Description = "...", Type = LocationType.Region },
            ],
            Characters =
            [
                // Deliberately non-canonical alias — should be stored as chars/wb-xfer-foo.
                new CharacterUpsertRequest
                {
                    Id = "characters/wb-xfer-foo", Name = "Foo", IsPc = true, CurrentLocationId = "locations/wb-xfer-start",
                    SystemStats = new Dnd5eExtension { Dexterity = 10 },
                },
            ],
            Items =
            [
                new ItemUpsertRequest
                {
                    Id = "items/wb-xfer-armor", Name = "Chainmail", Description = "...",
                    HolderId = "locations/wb-xfer-start", CoreCategory = ItemCategory.Armor,
                    EquipZones = [EquipZone.Torso], EquipLayer = EquipLayer.Armor,
                    Properties = new Dictionary<string, object> { ["acBonus"] = "5" },
                },
            ],
        };

        var buildResult = await worldBuilder.WorldBuild(batch, slug);
        Assert.True(buildResult.Success, buildResult.Summary);

        using (var verifySession = _fixture.Store.OpenAsyncSession())
        {
            var stored = await verifySession.LoadAsync<Character>("chars/wb-xfer-foo");
            Assert.NotNull(stored);
            var aliased = await verifySession.LoadAsync<Character>("characters/wb-xfer-foo");
            Assert.Null(aliased);
        }

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            // Mark it ambient-persistent while still at the location, then move it onto the character.
            var toCharacter = await repo.StageChangesAsync(session,
            [
                new ItemUpdate { ItemId = "items/wb-xfer-armor", AmbientPersistenceNote = "left on the armor rack", AmbientExpiresAtDay = 5 },
                new ItemTransfer { ItemId = "items/wb-xfer-armor", ToHolderId = "chars/wb-xfer-foo" },
            ], slug);
            Assert.True(toCharacter.Success, string.Join("; ", toCharacter.Summary));
            await session.SaveChangesAsync();
        }

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            var item = await session.LoadAsync<Item>("items/wb-xfer-armor");
            Assert.Equal("chars/wb-xfer-foo", item.HolderId);
            Assert.Null(item.Persistence); // Transfer onto a character clears ambient-decay tracking.
        }

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            var equip = await repo.StageChangesAsync(session,
            [
                new ItemEquip { CharacterId = "chars/wb-xfer-foo", ItemId = "items/wb-xfer-armor" },
            ], slug);
            Assert.True(equip.Success, string.Join("; ", equip.Summary));
            await session.SaveChangesAsync();
        }

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            var character = await session.LoadAsync<Character>("chars/wb-xfer-foo");
            var stats = Assert.IsType<Dnd5eExtension>(character.SystemStats);
            Assert.Equal(15, stats.ArmorClass); // 10 base + 5 acBonus from the equipped armor.
        }
    }

    [Fact]
    public async Task WorldBuild_CharacterEntry_RunsBootstrap()
    {
        var worldBuilder = TestCampaignToolsFactory.CreateWorldBuilderTools(_fixture);
        var tools = TestCampaignToolsFactory.Create(_fixture);
        var slug = "world-build-bootstrap-" + Guid.NewGuid().ToString("N")[..8];

        await tools.SetActiveSystem(RulesetSystem.Dnd5e, null, slug);

        var batch = new WorldBuildBatch
        {
            Characters =
            [
                new CharacterUpsertRequest
                {
                    Id = "chars/wb-bootstrap",
                    Name = "Bootstrapped",
                    IsPc = true,
                    SystemStats = new Dnd5eExtension { Level = 3, Constitution = 14, HitDie = "d10" },
                },
            ],
        };

        var result = await worldBuilder.WorldBuild(batch, slug);

        Assert.True(result.Success, result.Summary);
        using var session = _fixture.Store.OpenAsyncSession();
        var stored = await session.LoadAsync<Character>("chars/wb-bootstrap");
        Assert.NotNull(stored);
        Assert.True(stored.MaxHp > 0, "Bootstrap should have derived MaxHp from systemStats.");
    }
}
