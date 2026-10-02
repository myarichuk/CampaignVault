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
                new ItemUpsertRequest { Id = "items/wb-sword", Name = "Sword", Description = "A blade.", HolderId = "chars/wb-valen", CoreCategory = ItemCategories.Weapon },
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
        var rolledBack = await session.LoadAsync<Character>("chars/wb-rollback-good", TestContext.Current.CancellationToken);
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
            Lore =
            [
                .. Enumerable.Range(0, 101)
                    .Select(i => new LoreUpsertRequest
                        { Id = $"lore/wb-cap-{i}", Title = $"Entry {i}", Content = "..." })
            ],
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
        var stored = await session.LoadAsync<Character>("chars/wb-alias", TestContext.Current.CancellationToken);
        Assert.NotNull(stored);
    }

    [Fact]
    public async Task WorldBuild_ThenSeedCoverage_ReportsSeedCoverage()
    {
        // Seed coverage moved out of GetWorldState (per-turn cost) into the start_session kickoff;
        // this exercises the underlying repository builder both before and after seeding.
        var repo = _fixture.CreateRepository();
        var tools = TestCampaignToolsFactory.Create(_fixture, repository: repo);
        var worldBuilder = TestCampaignToolsFactory.CreateWorldBuilderTools(_fixture, repo);
        var slug = "world-build-coverage-" + Guid.NewGuid().ToString("N")[..8];
        await TestCampaignDefaults.EnsureExistsAsync(tools, slug);

        SeedCoverageSummary before;
        using (var beforeSession = _fixture.Store.OpenAsyncSession())
        {
            before = await repo.BuildSeedCoverageAsync(beforeSession, slug, null);
        }
        // Verify no PC characters yet (strict check)
        Assert.Contains("no PC characters yet", before.Gaps);
        var initialLocationCount = before.Locations;

        var batch = new WorldBuildBatch
        {
            Locations = [new LocationUpsertRequest { Id = "locations/wb-cov-start", Name = "Start", Description = "A place.", Type = LocationType.Region, ClimateZone = ClimateZone.Temperate }],
            Characters = [new CharacterUpsertRequest { Id = "chars/wb-cov-pc", Name = "PC", IsPc = true, CurrentLocationId = "locations/wb-cov-start" }],
        };
        var buildResult = await worldBuilder.WorldBuild(batch, slug);
        Assert.True(buildResult.Success, buildResult.Summary);

        SeedCoverageSummary after;
        using (var afterSession = _fixture.Store.OpenAsyncSession())
        {
            after = await repo.BuildSeedCoverageAsync(afterSession, slug, "locations/wb-cov-start");
        }
        Assert.Equal(initialLocationCount + 1, after.Locations);
        Assert.Equal(1, after.PcCharacters);
        Assert.DoesNotContain("no locations yet", after.Gaps);
        Assert.DoesNotContain("no PC characters yet", after.Gaps);
        Assert.DoesNotContain(after.Gaps, g => g.Contains("climateZone"));
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
                    HolderId = "locations/wb-xfer-start", CoreCategory = ItemCategories.Armor,
                    EquipZones = [EquipZones.Torso], EquipLayer = EquipLayers.Armor,
                    Properties = new Dictionary<string, object> { ["acBonus"] = "5" },
                },
            ],
        };

        var buildResult = await worldBuilder.WorldBuild(batch, slug);
        Assert.True(buildResult.Success, buildResult.Summary);

        using (var verifySession = _fixture.Store.OpenAsyncSession())
        {
            var stored = await verifySession.LoadAsync<Character>("chars/wb-xfer-foo", TestContext.Current.CancellationToken);
            Assert.NotNull(stored);
            var aliased = await verifySession.LoadAsync<Character>("characters/wb-xfer-foo", TestContext.Current.CancellationToken);
            Assert.Null(aliased);
        }

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            // Mark it ambient-persistent while still at the location, then move it onto the character.
            var toCharacter = await repo.StageChangesAsync(_fixture.CreateCampaignSession(session, slug), [
                new ItemUpdate { ItemId = "items/wb-xfer-armor", AmbientPersistenceNote = "left on the armor rack", AmbientExpiresAtDay = 5 },
                new ItemTransfer { ItemId = "items/wb-xfer-armor", ToHolderId = "chars/wb-xfer-foo" },
            ]);
            Assert.True(toCharacter.Success, string.Join("; ", toCharacter.Summary));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            var item = await session.LoadAsync<Item>("items/wb-xfer-armor", TestContext.Current.CancellationToken);
            Assert.Equal("chars/wb-xfer-foo", item.HolderId);
            Assert.Null(item.Persistence); // Transfer onto a character clears ambient-decay tracking.
        }

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            var equip = await repo.StageChangesAsync(_fixture.CreateCampaignSession(session, slug), [
                new ItemEquip { CharacterId = "chars/wb-xfer-foo", ItemId = "items/wb-xfer-armor" },
            ]);
            Assert.True(equip.Success, string.Join("; ", equip.Summary));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            var character = await session.LoadAsync<Character>("chars/wb-xfer-foo", TestContext.Current.CancellationToken);
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
        var stored = await session.LoadAsync<Character>("chars/wb-bootstrap", TestContext.Current.CancellationToken);
        Assert.NotNull(stored);
        Assert.True(stored.MaxHp > 0, "Bootstrap should have derived MaxHp from systemStats.");
    }

    [Theory]
    [InlineData("Dnd5e", "dnd5e")]
    [InlineData(" DND5E ", "dnd5e")]
    [InlineData("Pathfinder2e", "pf2e")]
    [InlineData("PF2E", "pf2e")]
    [InlineData("Narrative", "narrative")]
    [InlineData("shadow-and-steel", "shadow-and-steel")]
    [InlineData(null, "")]
    public void RulesetSystem_Canonicalize_MapsLegacyNamesAndCasing(string? input, string expected)
    {
        Assert.Equal(expected, RulesetSystem.Canonicalize(input));
    }

    [Fact]
    public async Task WorldBuild_SeededArmorSetsAc_AndClassPoolsExistFromTheStart()
    {
        var worldBuilder = TestCampaignToolsFactory.CreateWorldBuilderTools(_fixture);
        var tools = TestCampaignToolsFactory.Create(_fixture);
        var tag = Guid.NewGuid().ToString("N")[..8];
        var slug = "world-build-kit-" + tag;
        var fighter = "chars/wb-kit-fighter-" + tag;
        var cleric = "chars/wb-kit-cleric-" + tag;

        // As clients and the tool's own example spell it ("Dnd5e"), not the canonical "dnd5e".
        await tools.CreateCampaign(slug, "Dnd5e");

        // Characters dispatch before items: the armor below only exists after the fighter was bootstrapped.
        var batch = new WorldBuildBatch
        {
            Characters =
            [
                new CharacterUpsertRequest
                {
                    Id = fighter, Name = "Kit Fighter", IsPc = true,
                    SystemStats = new Dnd5eExtension { Level = 5, ClassLevels = [new ClassLevelEntry { Class = "Fighter", Level = 5 }], HitDie = "d10", Dexterity = 14, Constitution = 15 },
                },
                new CharacterUpsertRequest
                {
                    Id = cleric, Name = "Kit Cleric", IsPartyCompanion = true,
                    SystemStats = new Dnd5eExtension { Level = 3, ClassLevels = [new ClassLevelEntry { Class = "Cleric", Level = 3 }], HitDie = "d8", Wisdom = 16, SpellcastingAbility = "wisdom" },
                },
            ],
            Items =
            [
                new ItemUpsertRequest { Id = "items/wb-kit-mail-" + tag, Name = "Chain Mail", DefinitionName = "chain_mail", HolderId = fighter, IsEquipped = true },
                new ItemUpsertRequest { Id = "items/wb-kit-shield-" + tag, Name = "Shield", DefinitionName = "shield", HolderId = fighter, IsEquipped = true },
                new ItemUpsertRequest { Id = "items/wb-kit-rope-" + tag, Name = "Rope", HolderId = cleric },
            ],
        };

        var result = await worldBuilder.WorldBuild(batch, slug);

        Assert.True(result.Success, result.Summary);
        using var session = _fixture.Store.OpenAsyncSession();
        var ct = TestContext.Current.CancellationToken;
        var storedFighter = await session.LoadAsync<Character>(fighter, ct);
        var storedCleric = await session.LoadAsync<Character>(cleric, ct);
        // Chain mail 16 (no Dex) + shield 2.
        Assert.Equal(18, ((Dnd5eExtension)storedFighter.SystemStats).ArmorClass);
        Assert.Contains("action_surge", storedFighter.SystemStats.ResourcePools.Keys);
        Assert.Contains("second_wind", storedFighter.SystemStats.ResourcePools.Keys);
        Assert.Equal(4, storedCleric.SystemStats.ResourcePools["spell_slots_1"].Max);
        Assert.Equal(2, storedCleric.SystemStats.ResourcePools["spell_slots_2"].Max);
        // Unarmored and untouched by the armor pass: 10 + Dex (10).
        Assert.Equal(10, ((Dnd5eExtension)storedCleric.SystemStats).ArmorClass);
    }

    [Fact]
    public async Task WorldBuild_Race_AppliesOnCreate_AndReupsertDoesNotStackBonuses()
    {
        var worldBuilder = TestCampaignToolsFactory.CreateWorldBuilderTools(_fixture);
        var tools = TestCampaignToolsFactory.Create(_fixture);
        var tag = Guid.NewGuid().ToString("N")[..8];
        var slug = "world-build-race-" + tag;
        var dwarf = "chars/wb-race-dwarf-" + tag;
        await tools.CreateCampaign(slug, "dnd5e");

        var first = await worldBuilder.WorldBuild(new WorldBuildBatch
        {
            Characters =
            [
                new CharacterUpsertRequest
                {
                    Id = dwarf, Name = "Brom", IsPc = true,
                    SystemStats = new Dnd5eExtension { Race = "Dwarf", Level = 2, ClassLevels = [new ClassLevelEntry { Class = "Fighter", Level = 2 }], HitDie = "d10", Constitution = 14 },
                },
            ],
        }, slug);
        Assert.True(first.Success, first.Summary);

        // A later, unrelated upsert of the same character (as the DM does to move someone).
        var second = await worldBuilder.WorldBuild(new WorldBuildBatch
        {
            Characters = [new CharacterUpsertRequest { Id = dwarf, Name = "Brom", CurrentActivity = "Sharpening an axe" }],
        }, slug);
        Assert.True(second.Success, second.Summary);

        using var session = _fixture.Store.OpenAsyncSession();
        var stored = await session.LoadAsync<Character>(dwarf, TestContext.Current.CancellationToken);
        var stats = (Dnd5eExtension)stored.SystemStats;
        Assert.Equal(16, stats.Constitution);
        Assert.Equal(25, stats.Movement);
        Assert.Contains("Darkvision 60 ft.", stored.DistinctiveFeatures);
        Assert.Single(stored.DistinctiveFeatures, f => f.StartsWith("Darkvision"));
    }

    [Fact]
    public async Task WorldBuild_Pf2e_SlotsFollowLevel_AndNonCastersHaveNoSpellDc()
    {
        var worldBuilder = TestCampaignToolsFactory.CreateWorldBuilderTools(_fixture);
        var tools = TestCampaignToolsFactory.Create(_fixture);
        var tag = Guid.NewGuid().ToString("N")[..8];
        var slug = "world-build-pf2e-" + tag;
        var wizard = "chars/wb-pf2e-wizard-" + tag;
        var fighter = "chars/wb-pf2e-fighter-" + tag;
        await tools.CreateCampaign(slug, "pf2e");

        var result = await worldBuilder.WorldBuild(new WorldBuildBatch
        {
            Characters =
            [
                new CharacterUpsertRequest
                {
                    Id = wizard, Name = "Liesl", ClassLevel = "Wizard 4", IsPc = true,
                    SystemStats = new Pf2eExtension { Level = 4, Ancestry = "Elf", IntelligenceMod = 4 },
                },
                new CharacterUpsertRequest
                {
                    Id = fighter, Name = "Brakk", ClassLevel = "Fighter 3", IsPartyCompanion = true,
                    SystemStats = new Pf2eExtension { Level = 3, StrengthMod = 4 },
                },
            ],
        }, slug);
        Assert.True(result.Success, result.Summary);

        using var session = _fixture.Store.OpenAsyncSession();
        var ct = TestContext.Current.CancellationToken;
        var storedWizard = await session.LoadAsync<Character>(wizard, ct);
        var storedFighter = await session.LoadAsync<Character>(fighter, ct);
        var wizardStats = (Pf2eExtension)storedWizard.SystemStats;
        var fighterStats = (Pf2eExtension)storedFighter.SystemStats;

        // Level 4 full caster: rank 1 and rank 2 at 3 slots each, nothing higher yet.
        Assert.Equal(3, wizardStats.ResourcePools["spell_slots_1"].Max);
        Assert.Equal(3, wizardStats.ResourcePools["spell_slots_2"].Max);
        Assert.DoesNotContain("spell_slots_3", wizardStats.ResourcePools.Keys);
        Assert.DoesNotContain("spell_slots_4", wizardStats.ResourcePools.Keys);
        Assert.Equal("Intelligence", wizardStats.SpellcastingAbility);
        Assert.NotNull(wizardStats.SpellDc);
        Assert.Equal(30, wizardStats.Movement);

        Assert.Null(fighterStats.SpellDc);
        Assert.Null(fighterStats.SpellcastingAbility);
        Assert.DoesNotContain(fighterStats.ResourcePools.Keys, k => k.StartsWith("spell_slots_", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WorldBuild_NoCampaignConfig_DoesNotPersistDefaultConfig_AndSurfacesNote()
    {
        var worldBuilder = TestCampaignToolsFactory.CreateWorldBuilderTools(_fixture);
        var slug = "world-build-noconfig-" + Guid.NewGuid().ToString("N")[..8];

        var batch = new WorldBuildBatch
        {
            Characters =
            [
                new CharacterUpsertRequest { Id = "chars/wb-noconfig", Name = "Unconfigured", IsPc = true },
            ],
        };

        var result = await worldBuilder.WorldBuild(batch, slug);

        Assert.True(result.Success, result.Summary);
        Assert.Contains(result.Data!.Warnings, w => w.Contains("NOTE:"));

        using var session = _fixture.Store.OpenAsyncSession();
        var keys = new CampaignVault.Data.CampaignDocumentKeys();
        var config = await session.LoadAsync<CampaignConfig>(keys.Config(slug), TestContext.Current.CancellationToken);
        Assert.Null(config);
    }
}
