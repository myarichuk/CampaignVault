using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Data.Pressure;
using CampaignVault.Data.Pressure.Contributors;
using CampaignVault.Models;
using CampaignVault.Rulesets;
using Microsoft.Extensions.Logging.Abstractions;
using Raven.Client.Documents.Session;
using Xunit;

namespace CampaignVault.Tests;

[Collection("RavenDB")]
public class SystemStatsBootstrapTests : IClassFixture<RavenDBFixture>
{
    private readonly RavenDBFixture _fixture;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public SystemStatsBootstrapTests(RavenDBFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void CharacterCreate_DeserializesEmbeddedSystemStats()
    {
        var json = """
        [
          {
            "$type": "character_create",
            "characterId": "chars/goblin-scout",
            "name": "Goblin Scout",
            "maxHp": 7,
            "currentHp": 7,
            "classLevel": "Goblin 1",
            "systemStats": {
              "$system": "dnd5e",
              "armorClass": 15,
              "dexterity": 14,
              "skillModifiers": { "Stealth": 6 }
            }
          }
        ]
        """;

        var changes = JsonSerializer.Deserialize<WorldChange[]>(json, JsonOptions);
        var create = Assert.IsType<CharacterCreate>(Assert.Single(changes!));
        var stats = Assert.IsType<Dnd5eExtension>(create.SystemStats);
        Assert.Equal(15, stats.ArmorClass);
        Assert.Equal(14, stats.Dexterity);
        Assert.Equal(6, stats.SkillModifiers["Stealth"]);
    }

    [Fact]
    public async Task CharacterCreate_AppliesSystemStats_OnCreate()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        await StoreDnd5eConfigAsync(session, "bootstrap-create");

        var characterId = "chars/elara-voss-" + Guid.NewGuid().ToString("N")[..8];
        var handler = RulesetDataTestHelper.CreateCharacterCreateHandler();
        var change = new CharacterCreate
        {
            CharacterId = characterId,
            Name = "Elara Voss",
            IsPc = true,
            KeepAlive = true,
            MaxHp = 18,
            ClassLevel = "Human Fighter 2",
            SystemStats = new Dnd5eExtension
            {
                ArmorClass = 16,
                Strength = 16,
                Dexterity = 14,
                SkillModifiers = new Dictionary<string, int> { { "Athletics", 5 } }
            }
        };

        var ctx = CreateContext(session, "bootstrap-create");
        var result = await handler.ApplyAsync(change, ctx);
        Assert.True(result.Success);

        await session.SaveChangesAsync();

        var character = await session.LoadAsync<Character>(characterId);
        var stats = Assert.IsType<Dnd5eExtension>(character!.SystemStats);
        Assert.Equal(16, stats.ArmorClass);
        Assert.Equal(16, stats.Strength);
        Assert.Equal(5, stats.SkillModifiers["Athletics"]);
    }

    [Fact]
    public async Task SystemStatsChange_MergesPartialUpdates()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        await StoreDnd5eConfigAsync(session, "bootstrap-merge");

        var existing = new Character
        {
            Id = "chars/goblin-1",
            Name = "Goblin",
            MaxHp = 7,
            CurrentHp = 7,
            SystemStats = new Dnd5eExtension
            {
                ArmorClass = 15,
                Dexterity = 14,
                SkillModifiers = new Dictionary<string, int> { { "Stealth", 6 } }
            }
        };
        await session.StoreAsync(existing);
        await session.SaveChangesAsync();

        var handler = new SystemStatsChangeHandler(new CampaignVault.Data.CampaignDocumentKeys(), BootstrapTestHelper.CreateOrchestrator());
        var change = new SystemStatsChange
        {
            CharacterId = "chars/goblin-1",
            SystemStats = new Dnd5eExtension
            {
                Strength = 8,
                SkillModifiers = new Dictionary<string, int> { { "Perception", 2 } }
            }
        };

        var ctx = CreateContext(session, "bootstrap-merge");
        var result = await handler.ApplyAsync(change, ctx);
        Assert.True(result.Success);

        var stats = Assert.IsType<Dnd5eExtension>(existing.SystemStats);
        Assert.Equal(15, stats.ArmorClass);
        Assert.Equal(14, stats.Dexterity);
        Assert.Equal(8, stats.Strength);
        Assert.Equal(6, stats.SkillModifiers["Stealth"]);
        Assert.Equal(2, stats.SkillModifiers["Perception"]);
    }

    [Fact]
    public async Task CharacterCreate_RejectsMismatchedRulesetStats()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        await StoreDnd5eConfigAsync(session, "bootstrap-mismatch");

        var handler = RulesetDataTestHelper.CreateCharacterCreateHandler();
        var change = new CharacterCreate
        {
            CharacterId = "chars/bad-stats",
            Name = "Bad Stats",
            MaxHp = 10,
            SystemStats = new Pf2eExtension { ArmorClass = 18 }
        };

        var ctx = CreateContext(session, "bootstrap-mismatch");
        var result = await handler.ApplyAsync(change, ctx);
        Assert.False(result.Success);
    }

    [Theory]
    [InlineData(RulesetSystem.Dnd5e, false)]
    [InlineData(RulesetSystem.Pathfinder2e, false)]
    public void SystemStatsCompleteness_FactoryDefaults_AreIncomplete(RulesetSystem system, bool expectedComplete)
    {
        var character = new Character
        {
            Id = "chars/test",
            Name = "Test",
            KeepAlive = true,
            MaxHp = 10,
            SystemStats = SystemStatsMerger.CreateDefault(system)
        };

        Assert.Equal(expectedComplete, SystemStatsCompleteness.IsComplete(character, system));
    }

    [Fact]
    public void SystemStatsCompleteness_BootstrappedGoblin_IsComplete()
    {
        var character = new Character
        {
            Id = "chars/goblin",
            Name = "Goblin",
            MaxHp = 7,
            SystemStats = new Dnd5eExtension
            {
                ArmorClass = 15,
                Dexterity = 14,
                SkillModifiers = new Dictionary<string, int> { { "Stealth", 6 } }
            }
        };

        Assert.True(SystemStatsCompleteness.IsComplete(character, RulesetSystem.Dnd5e));
    }

    [Fact]
    public void SystemStatsCompleteness_FlavorTransient_SkipsPressure()
    {
        var character = new Character
        {
            Id = "chars/bard",
            Name = "Background Bard",
            KeepAlive = false,
            MaxHp = 0,
            SystemStats = new Dnd5eExtension()
        };

        Assert.True(SystemStatsCompleteness.IsComplete(character, RulesetSystem.Dnd5e));
    }

    [Fact]
    public async Task IncompleteSystemStatsPressureContributor_SurfacesWarning_ForUnbootstrappedCombatant()
    {
        using var session = _fixture.Store.OpenAsyncSession();

        var keys = new CampaignDocumentKeys();
        var config = new CampaignConfig
        {
            Id = keys.Config("pressure-bootstrap"),
            ActiveSystem = RulesetSystem.Dnd5e
        };
        await session.StoreAsync(config);

        await session.StoreAsync(new Character
        {
            Id = "chars/unbootstrapped",
            Name = "Unbootstrapped Fighter",
            CampaignName = "pressure-bootstrap",
            KeepAlive = true,
            MaxHp = 18,
            CurrentHp = 18,
            SystemStats = new Dnd5eExtension()
        });
        await session.SaveChangesAsync();

        List<Character> indexedCombatants = [];
        for (var attempt = 0; attempt < 50; attempt++)
        {
            indexedCombatants = await PressureQueryHelper.QueryCombatantCharactersAsync(
                session, "pressure-bootstrap", 100);
            if (indexedCombatants.Any(c => c.Id == "chars/unbootstrapped"))
            {
                break;
            }

            await Task.Delay(100);
        }

        Assert.Contains(indexedCombatants, c => c.Id == "chars/unbootstrapped");

        var time = new CampaignTime { Id = keys.StateTime("pressure-bootstrap"), TotalDaysElapsed = 1 };
        var contributor = new IncompleteSystemStatsPressureContributor();
        var ctx = new PressureContext("pressure-bootstrap", time, config, session);
        var pressures = (await contributor.EvaluateAsync(ctx)).ToList();

        Assert.Contains(pressures, p =>
            p.Severity == PressureSeverity.EngineWarning
            && p.EntityId == "chars/unbootstrapped"
            && p.Text.Contains("systemStats")
            && p.Text.Contains("hitDie"));
    }

    private static async Task StoreDnd5eConfigAsync(IAsyncDocumentSession session, string campaign)
    {
        var keys = new CampaignDocumentKeys();
        await session.StoreAsync(new CampaignConfig
        {
            Id = keys.Config(campaign),
            ActiveSystem = RulesetSystem.Dnd5e
        });
        await session.SaveChangesAsync();
    }

    private static ChangeContext CreateContext(IAsyncDocumentSession session, string campaign)
    {
        var dispatcher = new WorldChangeDispatcher([], new CampaignVault.Data.CampaignDocumentKeys(), NullLogger<WorldChangeDispatcher>.Instance);
        return new ChangeContext(
            session,
            new Dictionary<string, Character>(),
            new Dictionary<string, Item>(),
            new Dictionary<string, Location>(),
            new Dictionary<string, Faction>(),
            new Dictionary<string, Quest>(),
            NullLogger.Instance,
            [],
            dispatcher,
            null,
            campaign);
    }
}
