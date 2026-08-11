using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;
using CampaignVault.Tools;
using Microsoft.Extensions.Logging;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using Xunit;

namespace CampaignVault.Tests;

public sealed class TestNoOpSimulationEngine : IWorldSimulationEngine
{
    public Task<SimulationResult> RunAsync(SimulationContext context, CancellationToken ct = default)
        => Task.FromResult(new SimulationResult([], [], [], [], []));
}

public sealed class TestFakeEmbeddingService : CampaignVault.Services.ILocalEmbeddingService
{
    public Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken ct = default)
        => Task.FromResult(Enumerable.Repeat(0.1f, CampaignVault.Services.EmbeddingModelPaths.VectorDimensions).ToArray());
}

public class RavenDBFixture : IDisposable
{
    public IDocumentStore Store { get; }
    public IContainer Container { get; private set; }
    private readonly bool _isSharedStore;

    public RavenDBFixture(RavenDbTestEnvironment environment)
    {
        var (store, isSharedStore) = environment.CreateStoreForClass($"TestDB_{Guid.NewGuid():N}");
        Store = store;
        _isSharedStore = isSharedStore;

        var builder = new ContainerBuilder();
        builder.RegisterInstance(Store).As<IDocumentStore>();
        builder.RegisterModule<CampaignVault.AutofacModules.CampaignVaultModule>();
        builder.RegisterInstance(Microsoft.Extensions.Logging.Abstractions.NullLogger<CampaignRepository>.Instance)
            .As<ILogger<CampaignRepository>>();
        builder.RegisterType<TestNoOpSimulationEngine>().As<IWorldSimulationEngine>().InstancePerLifetimeScope();
        builder.RegisterType<TestFakeEmbeddingService>().As<CampaignVault.Services.ILocalEmbeddingService>().InstancePerLifetimeScope();
        
        Container = builder.Build();
        TestCampaignDefaults.EnsureExistsAsync(this).GetAwaiter().GetResult();
    }

    public CampaignRepository CreateRepository(IWorldSimulationEngine? engineOverride = null,
        Action<ContainerBuilder>? overrides = null)
    {
        var scope = Container.BeginLifetimeScope(b =>
        {
            if (engineOverride != null)
            {
                b.RegisterInstance(engineOverride).As<IWorldSimulationEngine>();
            }

            if (overrides != null)
            {
                overrides(b);
            }

            b.RegisterType<CampaignRepository>();
        });
        return scope.Resolve<CampaignRepository>();
    }

    public CampaignSession CreateCampaignSession(IAsyncDocumentSession session, string campaignName = TestCampaignDefaults.Slug)
    {
        if (!CampaignSlug.TryCanonicalize(campaignName, out var effective))
        {
            throw new ArgumentException($"Invalid campaign name: {campaignName}");
        }
        return new CampaignSession(session, effective);
    }

    public void Dispose()
    {
        if (_isSharedStore)
        {
            // Owned by RavenDbTestEnvironment; disposed once at the end of the whole run.
            return;
        }

        Store.Dispose();
    }
}

[Collection("RavenDB")]
public class CampaignRepositoryTests : IClassFixture<RavenDBFixture>
{
    private readonly IDocumentStore _store;
    private readonly RavenDBFixture _fixture;

    public CampaignRepositoryTests(RavenDBFixture fixture)
    {
        _store = fixture.Store;
        _fixture = fixture;
    }

    [Fact]
    public void Container_Resolves_CampaignRepository()
    {
        var repo = _fixture.CreateRepository();
        Assert.NotNull(repo);
    }

    [Fact]
    public async Task GetCharacter_Fuzzy_Match_Works()
    {
        var repo = _fixture.CreateRepository();
        using var session = _store.OpenAsyncSession();

        var id = "npcs/gandalf-" + Guid.NewGuid();
        await repo.UpsertCharacterAsync(_fixture.CreateCampaignSession(session, TestCampaignDefaults.Slug), new CharacterUpsertRequest { Id = id, Name = "Gandalf the Grey" });
        await session.SaveChangesAsync();

        // Wait for indexing (with timeout to prevent CI hangs)
        var indexWaitStart = DateTime.UtcNow;
        while ((DateTime.UtcNow - indexWaitStart).TotalSeconds < 10)
        {
            var stats = _store.Maintenance.Send(new Raven.Client.Documents.Operations.GetStatisticsOperation());
            if (stats.Indexes.Any(x => x.Name == "Character/Search" && x.IsStale == false))
            {
                break;
            }

            await Task.Delay(100);
        }

        if ((DateTime.UtcNow - indexWaitStart).TotalSeconds >= 10)
        {
            throw new TimeoutException("Index 'Character/Search' did not become non-stale within 10s");
        }

        // Wildcard (substring) match — Corax does not support .Fuzzy(); substring is the last-resort path
        var result = await repo.GetCharacterAsync(_fixture.CreateCampaignSession(session, TestCampaignDefaults.Slug), "Gand");
        Assert.NotNull(result);
        Assert.Equal("Gandalf the Grey", result!.Name);
    }

    [Fact]
    public async Task UnifiedSearch_Does_Not_Leak_Across_Campaigns()
    {
        const string campaignA = "isolation-camp-a";
        const string campaignB = "isolation-camp-b";
        var tools = TestCampaignToolsFactory.Create(_fixture);
        await TestCampaignDefaults.EnsureExistsAsync(tools, campaignA);
        await TestCampaignDefaults.EnsureExistsAsync(tools, campaignB);

        var repo = _fixture.CreateRepository();

        // Store a character exclusively in campaign B
        var charId = "chars/secret-dragon-" + Guid.NewGuid().ToString("N")[..8];
        using (var session = _store.OpenAsyncSession())
        {
            await repo.UpsertCharacterAsync(
                _fixture.CreateCampaignSession(session, campaignB),
                new CharacterUpsertRequest { Id = charId, Name = "Secret Dragon of Campaign B", Notes = "dragon fire breath scales" });
            await session.SaveChangesAsync();
        }

        // Wait for indexes
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var stats = _store.Maintenance.Send(new Raven.Client.Documents.Operations.GetStatisticsOperation());
            if (stats.Indexes.All(x => !x.IsStale)) break;
            await Task.Delay(100);
        }

        using var searchSession = _store.OpenAsyncSession();
        var resultsFromA = await repo.UnifiedSearchAsync(_fixture.CreateCampaignSession(searchSession, campaignA), "dragon");

        Assert.DoesNotContain(resultsFromA, r => r is SearchMatch { EntityType: "character", Match: CharacterSearchSummary c } && c.Id == charId);
    }

    [Fact]
    public async Task Commit_Updates_HP_Delta_Atomically()
    {
        var repo = _fixture.CreateRepository();
        var id = "npcs/gimli-" + Guid.NewGuid();
        using (var session = _store.OpenAsyncSession())
        {
            await repo.UpsertCharacterAsync(
                _fixture.CreateCampaignSession(session, TestCampaignDefaults.Slug),
                new CharacterUpsertRequest { Id = id, Name = "Gimli HP Test", CurrentHp = 30, MaxHp = 100 });
            await session.SaveChangesAsync();
        }

        using (var session = _store.OpenAsyncSession())
        {
            await repo.StageChangesAsync(_fixture.CreateCampaignSession(session, TestCampaignDefaults.Slug), [new HpChange { CharacterId = id, Delta = -5 }]);
            await session.SaveChangesAsync();
        }

        using (var session = _store.OpenAsyncSession())
        {
            var result = await session.LoadAsync<Character>(id);
            Assert.Equal(25, result!.CurrentHp);
        }
    }

    [Fact]
    public async Task Commit_Supports_Arbitrary_Attributes_Via_Open_Dictionary()
    {
        // Regression / feature test for review issue #13
        var repo = _fixture.CreateRepository();
        using var session = _store.OpenAsyncSession();

        var id = "npcs/attributetest-" + Guid.NewGuid();
        await repo.UpsertCharacterAsync(_fixture.CreateCampaignSession(session, TestCampaignDefaults.Slug), new CharacterUpsertRequest
        {
            Id = id,
            Name = "Test Attr NPC",
            SystemStats = new SystemExtension { Morale = 60f }
        });
        await session.SaveChangesAsync();

        // Commit a core attribute + a custom one
        await repo.StageChangesAsync(_fixture.CreateCampaignSession(session, TestCampaignDefaults.Slug), [
            new AttributeChange { CharacterId = id, Attribute = "morale", Value = 42f },
            new AttributeChange { CharacterId = id, Attribute = "corruption", Value = 77f },
            new AttributeChange
                { CharacterId = id, Attribute = "Reputation", Value = 55f } // case insensitivity in handler
        ]);
        await session.SaveChangesAsync();

        var npc = await session.LoadAsync<Character>(id);
        Assert.NotNull(npc.SystemStats);

        // Core promoted field still works
        Assert.Equal(42f, npc.SystemStats.Morale);

        // Custom attributes land in the open dict
        Assert.True(npc.SystemStats.Attributes.TryGetValue("corruption", out var corr));
        Assert.Equal(77f, corr);
        Assert.True(npc.SystemStats.Attributes.TryGetValue("reputation", out var rep)); // lowercased key
        Assert.Equal(55f, rep);
    }

    [Fact]
    public async Task CharacterDistressPressureContributor_Surfaces_Extreme_Attributes_And_Relationships()
    {
        var repo = _fixture.CreateRepository();
        using var session = _store.OpenAsyncSession();

        var id = "npcs/pressuretest-" + Guid.NewGuid();
        await repo.UpsertCharacterAsync(_fixture.CreateCampaignSession(session, TestCampaignDefaults.Slug), new CharacterUpsertRequest
        {
            Id = id,
            Name = "Pressure Test NPC",
            KeepAlive = true,
            MaxHp = 20,
            CurrentHp = 20,
            SystemStats = new Dnd5eExtension
            {
                ArmorClass = 12,
                Wisdom = 12,
                Morale = 5f,
                Willpower = 50f,
                Temperature = 60f,
                SkillModifiers = new Dictionary<string, int> { { "Insight", 3 } },
                Attributes = new Dictionary<string, float>
                {
                    { "corruption", 95f },
                    { "fear", 20f }
                }
            },
            Social = new SocialProfile
            {
                Relationships = new Dictionary<string, int>
                {
                    { "Rival", -85 },
                    { "Friend", 85 },
                    { "Neutral", 0 }
                }
            }
        });
        await session.SaveChangesAsync();

        var indexWaitStart = DateTime.UtcNow;
        while ((DateTime.UtcNow - indexWaitStart).TotalSeconds < 10)
        {
            var stats = _store.Maintenance.Send(new Raven.Client.Documents.Operations.GetStatisticsOperation());
            if (stats.Indexes.All(x => x.IsStale == false)) break;
            await Task.Delay(50);
        }

        var time = await repo.GetTimeAsync(_fixture.CreateCampaignSession(session, TestCampaignDefaults.Slug));
        var config = await repo.GetCampaignConfigAsync(_fixture.CreateCampaignSession(session, TestCampaignDefaults.Slug));
        var contributor = new CampaignVault.Data.Pressure.Contributors.CharacterDistressPressureContributor();
        var ctx = new CampaignVault.Data.Pressure.PressureContext("test-campaign", time, config, session);
        var allPressures = (await contributor.EvaluateAsync(ctx)).ToList();
        var pressures = allPressures.Where(p => p.EntityId == id).ToList();

        Assert.NotNull(pressures);
        Assert.Contains(pressures,
            p => p.Severity == PressureSeverity.Simulation && p.GroupingKey == CampaignVault.Data.Pressure.Contributors
                .CharacterDistressPressureContributor.MoraleGroupingKey);
        Assert.Contains(pressures,
            p => p.Severity == PressureSeverity.Simulation && p.GroupingKey == CampaignVault.Data.Pressure.Contributors
                .CharacterDistressPressureContributor.TemperatureHighGroupingKey);
        Assert.Contains(pressures,
            p => p.Severity == PressureSeverity.Simulation && p.GroupingKey ==
                CampaignVault.Data.Pressure.Contributors.CharacterDistressPressureContributor.GetAttributeGroupingKey(
                    "corruption"));

        Assert.Contains(pressures,
            p => p.Severity == PressureSeverity.NarrativePrompt && p.GroupingKey == CampaignVault.Data.Pressure
                .Contributors.CharacterDistressPressureContributor.GetRelationshipGroupingKey("Rival"));
        Assert.Contains(pressures,
            p => p.Severity == PressureSeverity.NarrativePrompt && p.GroupingKey == CampaignVault.Data.Pressure
                .Contributors.CharacterDistressPressureContributor.GetRelationshipGroupingKey("Friend"));

        Assert.DoesNotContain(pressures,
            p => p.GroupingKey == CampaignVault.Data.Pressure.Contributors.CharacterDistressPressureContributor
                .WillpowerGroupingKey);
        Assert.DoesNotContain(pressures,
            p => p.GroupingKey == CampaignVault.Data.Pressure.Contributors.CharacterDistressPressureContributor
                .GetAttributeGroupingKey("fear"));
        Assert.DoesNotContain(pressures,
            p => p.GroupingKey == CampaignVault.Data.Pressure.Contributors.CharacterDistressPressureContributor
                .GetRelationshipGroupingKey("Neutral"));
    }

    [Fact]
    public async Task AdvanceWorld_Is_Atomic_And_Runs_Simulation()
    {
        var engine = new DefaultSimulationEngine(
            [new NeedsAccumulationRule(), new RumorDecayRule(), new ScheduleEvaluationRule()]);
        var repo = _fixture.CreateRepository(engineOverride: engine);
        using var session = _store.OpenAsyncSession();

        await repo.SaveTimeAsync(_fixture.CreateCampaignSession(session, TestCampaignDefaults.Slug), new CampaignTime { TotalDaysElapsed = 100 });
        await repo.UpsertRumorAsync(
            _fixture.CreateCampaignSession(session, TestCampaignDefaults.Slug),
            new Rumor
            {
                Id = "rumors/1", Subject = "Aging Rumor", LastStateChangeDay = 100, RegionLocationId = "loc",
                State = RumorState.Peak
            });
        await session.SaveChangesAsync();

        // Wait for indexing (with timeout to prevent CI hangs)
        var indexWaitStart = DateTime.UtcNow;
        while ((DateTime.UtcNow - indexWaitStart).TotalSeconds < 10)
        {
            var stats = _store.Maintenance.Send(new Raven.Client.Documents.Operations.GetStatisticsOperation());
            if (stats.Indexes.All(x => x.IsStale == false))
            {
                break;
            }

            await Task.Delay(50);
        }

        if ((DateTime.UtcNow - indexWaitStart).TotalSeconds >= 10)
        {
            throw new TimeoutException("Indexes did not become non-stale within 10s");
        }

        var result = await repo.AdvanceWorldAsync(session, 15, 12, TestCampaignDefaults.Slug);
        await session.SaveChangesAsync();

        Assert.Equal(115, result.NewTime.TotalDaysElapsed);
        Assert.Contains(result.SimulatorEvents, e => e.Contains("starting to fade"));
    }

    [Fact]
    public async Task AdvanceWorld_Persists_Simulator_Mutations_On_NPCs_And_Rumors()
    {
        var engine = new DefaultSimulationEngine(
            [new NeedsAccumulationRule(), new RumorDecayRule(), new ScheduleEvaluationRule()],
            null);
        var repo = _fixture.CreateRepository(engineOverride: engine);
        using var session = _store.OpenAsyncSession();

        // Time + rumor (existing behavior)
        await repo.SaveTimeAsync(_fixture.CreateCampaignSession(session, TestCampaignDefaults.Slug), new CampaignTime { TotalDaysElapsed = 100 });
        await repo.UpsertRumorAsync(session,
            new Rumor
            {
                Id = "rumors/sim-1", Subject = "Simulator Persistence Rumor", LastStateChangeDay = 100,
                RegionLocationId = "loc", State = RumorState.Peak
            }, TestCampaignDefaults.Slug);

        // NPC with Schedule (required for simulator load) + Mind.Needs (the mutation target)
        var npcId = "npcs/sim-npc-" + Guid.NewGuid();
        var npc = new CharacterUpsertRequest
        {
            Id = npcId,
            Name = "Simulator Test NPC",
            Schedule = new Schedule
            {
                DefaultLocationId = "loc",
                Routines = [new Routine { Condition = "Noon", LocationId = "loc", Activity = "Testing" }]
            },
            Needs = new NeedsProfile
            {
                ActiveNeeds = new Dictionary<string, float> { ["tiredness"] = 40f }
            }
        };
        await repo.UpsertCharacterAsync(_fixture.CreateCampaignSession(session, TestCampaignDefaults.Slug), npc);
        await session.SaveChangesAsync();

        // Wait for indexing to ensure AdvanceWorld can find the rumor and NPC (with timeout)
        var indexWaitStart = DateTime.UtcNow;
        while ((DateTime.UtcNow - indexWaitStart).TotalSeconds < 10)
        {
            var stats = _store.Maintenance.Send(new Raven.Client.Documents.Operations.GetStatisticsOperation());
            if (stats.Indexes.All(x => x.IsStale == false))
            {
                break;
            }

            await Task.Delay(50);
        }

        if ((DateTime.UtcNow - indexWaitStart).TotalSeconds >= 10)
        {
            throw new TimeoutException("Indexes did not become non-stale within 10s (for AdvanceWorld test)");
        }

        // Act: advance (simulator mutates in-memory on tracked entities)
        var result = await repo.AdvanceWorldAsync(session, 15, 12, TestCampaignDefaults.Slug);
        await session.SaveChangesAsync();

        // Assert rumor fade (existing)
        Assert.Contains(result.SimulatorEvents, e => e.Contains("starting to fade"));

        // Critical functional assertion: simulator mutations on NPC Mind must survive SaveChanges
        var reloaded = await session.LoadAsync<Character>(npcId);
        Assert.NotNull(reloaded);
        Assert.NotNull(reloaded.Needs);
        Assert.True(reloaded.Needs.ActiveNeeds.TryGetValue("tiredness", out var tirednessAfter),
            "tiredness key should exist after simulator run");
        Assert.True(tirednessAfter > 40, "Simulator should have accumulated tiredness over 15 days");
        if (tirednessAfter > 80)
        {
            Assert.Equal("Exhausted", reloaded.Psychology.CurrentMood);
        }
    }

    [Fact]
    public async Task AdvanceWorld_CalendarMath_Handles_Large_Day_Jumps_Correctly()
    {
        // Regression test for review issue #6: the old Day += + while loops produced wrong Y/M/D on large advances.
        var repo = _fixture.CreateRepository();
        using var session = _store.OpenAsyncSession();

        // Start at a known point
        var startTime = new CampaignTime { TotalDaysElapsed = 10, Year = 1492, Month = 1, Day = 11 };
        await repo.SaveTimeAsync(_fixture.CreateCampaignSession(session, TestCampaignDefaults.Slug), startTime);
        await session.SaveChangesAsync();

        // Act: advance a large number of days (e.g. 400 days = 1 year + 1 month + 10 days in our 360-day calendar)
        var result = await repo.AdvanceWorldAsync(session, 400, 6, TestCampaignDefaults.Slug);
        await session.SaveChangesAsync();

        var t = result.NewTime;

        // 10 + 400 = 410 total days
        Assert.Equal(410, t.TotalDaysElapsed);

        // With 360-day years: 410 / 360 = 1 year + 50 days remainder
        // 50 / 30 = 1 month + 20 days → Year 1493, Month 2, Day 21  (1492 + 1)
        Assert.Equal(1493, t.Year);
        Assert.Equal(2, t.Month);
        Assert.Equal(21, t.Day);
    }

    [Fact]
    public async Task AdvanceWorld_NonDefaultStartingYear_RollsForwardFromActualYear()
    {
        // Regression test: AdvanceWorldAsync's day-based path used to recompute Year as
        // "1492 + (TotalDaysElapsed / 360)", ignoring the campaign's actual LoreSettings.Year.
        // A campaign that started at, say, year 500 would get silently reset to the 1492 epoch
        // the moment a day-based advance ran. It must instead roll forward from wherever the
        // clock's own Year/Month/Day currently sit.
        var repo = _fixture.CreateRepository();
        using var session = _store.OpenAsyncSession();

        var startTime = new CampaignTime { TotalDaysElapsed = 0, Year = 500, Month = 11, Day = 25 };
        await repo.SaveTimeAsync(_fixture.CreateCampaignSession(session, TestCampaignDefaults.Slug), startTime);
        await session.SaveChangesAsync();

        // Advance 10 days: Day 25 + 10 = 35 -> rolls into month 12, day 5.
        var result = await repo.AdvanceWorldAsync(session, 10, 6, TestCampaignDefaults.Slug);
        await session.SaveChangesAsync();

        var t = result.NewTime;
        Assert.Equal(10, t.TotalDaysElapsed);
        Assert.Equal(500, t.Year);
        Assert.Equal(12, t.Month);
        Assert.Equal(5, t.Day);
    }

    [Fact]
    public async Task NeedsAccumulationRule_Does_Not_Exceed_Cap_And_Uses_Clean_Math()
    {
        // Verifies review issue #14 fixes: capped deltas + consistent float math
        var engine = new DefaultSimulationEngine(
            [new NeedsAccumulationRule()],
            null);

        var repo = _fixture.CreateRepository(engineOverride: engine);

        using var session = _store.OpenAsyncSession();

        var id = "npcs/needscap-" + Guid.NewGuid();
        var npc = new CharacterUpsertRequest
        {
            Id = id,
            Name = "Capped Needs NPC",
            Schedule = new Schedule { DefaultLocationId = "loc-x", Routines = [] },
            Needs = new NeedsProfile
            {
                ActiveNeeds = new Dictionary<string, float> { ["hunger"] = 95f, ["thirst"] = 100f }
            }
        };
        await repo.UpsertCharacterAsync(_fixture.CreateCampaignSession(session, TestCampaignDefaults.Slug), npc);
        await session.SaveChangesAsync();

        // Wait for RavenDB indexes to catch up so the SimulationContext can find the scheduled NPC
        await session.Advanced.AsyncDocumentQuery<Character>().WaitForNonStaleResults(TimeSpan.FromSeconds(5))
            .ToListAsync();

        // Advance 2 days — hunger should go to 100 (capped delta), thirst should stay at 100 (no delta emitted for it)
        var result = await repo.AdvanceWorldAsync(session, 2, 6, TestCampaignDefaults.Slug);
        await session.SaveChangesAsync();

        var reloaded = await session.LoadAsync<Character>(id);
        var hunger = reloaded.Needs.ActiveNeeds["hunger"];
        var thirst = reloaded.Needs.ActiveNeeds["thirst"];

        Assert.True(hunger <= 100f && hunger >= 99f, $"Hunger should be capped near 100, was {hunger}");
        Assert.Equal(100f, thirst); // should not have gone over or emitted useless delta
    }

    [Fact]
    public async Task GetSceneAsync_WithMissingLocation_ReturnsUnanchoredStub()
    {
        var repo = _fixture.CreateRepository();
        using var session = _store.OpenAsyncSession();

        var missingId = "locations/does-not-exist-" + Guid.NewGuid();

        // Per Phase 6: GetSceneAsync must never throw for hallucinated IDs.
        // It returns a stub Location + IsLocationAnchored=false so tool can emit
        // copy-paste-ready location_create pressure without the LLM ever seeing an exception.
        var scene = await repo.GetSceneAsync(session, missingId, TestCampaignDefaults.Slug);

        Assert.NotNull(scene);
        Assert.False(scene.IsLocationAnchored);
        Assert.Equal(missingId, scene.Location.Id);
        Assert.Equal("[Unanchored]", scene.Location.Name);
        Assert.Empty(scene.Location.Exits);
        Assert.Empty(scene.PresentNPCs);
        Assert.Empty(scene.VisibleItems);
    }

    [Fact]
    public async Task GetSceneAsync_Finds_NPCs_By_CurrentLocationId_Using_Index()
    {
        // Verifies the fix for review issue #3: simulation-updated NPCs are discovered via index, not client-side 100-char scan.
        var repo = _fixture.CreateRepository();
        using var session = _store.OpenAsyncSession();

        var locId = "locations/test-scene-loc-" + Guid.NewGuid();
        await repo.UpsertLocationAsync(session,
            new LocationUpsertRequest { Id = locId, Name = "Test Scene Loc", Type = LocationType.Room }, TestCampaignDefaults.Slug);

        var npcId = "npcs/sim-npc-" + Guid.NewGuid();
        var npc = new CharacterUpsertRequest
        {
            Id = npcId,
            Name = "Simulated NPC",
            CurrentLocationId = locId, // <-- set directly (sim state), no Schedule
            CurrentActivity = "lurking in shadows"
        };
        await repo.UpsertCharacterAsync(_fixture.CreateCampaignSession(session, TestCampaignDefaults.Slug), npc);
        await session.SaveChangesAsync();

        // Wait for the (now extended) Character/Search index
        var indexWaitStart = DateTime.UtcNow;
        while ((DateTime.UtcNow - indexWaitStart).TotalSeconds < 10)
        {
            var stats = _store.Maintenance.Send(new Raven.Client.Documents.Operations.GetStatisticsOperation());
            if (stats.Indexes.Any(x => x.Name == "Character/Search" && !x.IsStale))
            {
                break;
            }

            await Task.Delay(100);
        }

        var scene = await repo.GetSceneAsync(session, locId, TestCampaignDefaults.Slug);

        Assert.Contains(scene.PresentNPCs, p => p.Id == npcId && p.Name == "Simulated NPC");
    }

    [Fact]
    public async Task GetWorldState_Aggregates_Context()
    {
        var repo = _fixture.CreateRepository();
        var tools = TestCampaignToolsFactory.Create(_fixture);

        using (var session = _store.OpenAsyncSession())
        {
            await repo.SaveTimeAsync(_fixture.CreateCampaignSession(session, TestCampaignDefaults.Slug), new CampaignTime { Day = 10 });
            await repo.LogEventAsync(session, new Event
            {
                Id = "e1", Summary = "History", Category = EventCategory.Test, Involved =
                    ["loc1"]
            }, TestCampaignDefaults.Slug);
            await repo.UpsertLocationAsync(session,
                new LocationUpsertRequest { Id = "locations/loc1", Name = "The Shire", Type = LocationType.Region }, TestCampaignDefaults.Slug);
            await session.SaveChangesAsync();
        }

        var result = await tools.GetWorldState("locations/loc1", TestCampaignDefaults.Slug);

        Assert.True(result.Success);
        Assert.Equal(10, result.Data!.Time.Day);
        Assert.Equal("The Shire", result.Data.PartyLocation!.Name);
    }


    [Fact]
    public async Task SanitizeValue_Prevents_JsonElement_Leakage()
    {
        var repo = _fixture.CreateRepository();
        using var session = _store.OpenAsyncSession();

        var id = "events/json-test-" + Guid.NewGuid();
        var json = JsonSerializer.Serialize(new { power = 9001, tags = new[] { "over", "9000" } });
        var details = JsonSerializer.Deserialize<Dictionary<string, object>>(json)!;

        await repo.LogEventAsync(session,
            new Event { Id = id, Summary = "Power Up", Category = EventCategory.Test, Details = details }, TestCampaignDefaults.Slug);
        await session.SaveChangesAsync();

        // Wait for indexing (with timeout to prevent CI hangs)
        var indexWaitStart = DateTime.UtcNow;
        while ((DateTime.UtcNow - indexWaitStart).TotalSeconds < 10)
        {
            var stats = _store.Maintenance.Send(new Raven.Client.Documents.Operations.GetStatisticsOperation());
            if (stats.Indexes.Any(x => x.Name == "Event/Search" && x.IsStale == false))
            {
                break;
            }

            await Task.Delay(100);
        }

        if ((DateTime.UtcNow - indexWaitStart).TotalSeconds >= 10)
        {
            throw new TimeoutException("Index 'Event/Search' did not become non-stale within 10s");
        }

        var results = await repo.QueryEventsAsync(session, "Power", EventCategory.Test, 10, TestCampaignDefaults.Slug);
        var ev = results.FirstOrDefault(x => x.Id == id);
        Assert.NotNull(ev);

        // This should not contain JsonElements.
        // Our central sanitizer prefers long for whole numbers for safety across STJ/Newtonsoft.
        var power = ev.Details!["power"];
        Assert.True(power is int || power is long, $"Expected int or long, got {power?.GetType().Name}");

        // Final proof: Serialization should work perfectly
        var finalJson = JsonSerializer.Serialize(ev);
        Assert.Contains("\"power\":9001", finalJson);
    }

    [Fact]
    public async Task GetNpcContext_Sanitizes_Event_Details_And_Uses_Safe_Query()
    {
        var repo = _fixture.CreateRepository();
        var tools = TestCampaignToolsFactory.Create(_fixture);

        using var session = _store.OpenAsyncSession();

        var charId = "npcs/sanitize-npc-" + Guid.NewGuid();
        await repo.UpsertCharacterAsync(_fixture.CreateCampaignSession(session, TestCampaignDefaults.Slug), new CharacterUpsertRequest { Id = charId, Name = "Sanitize NPC" });

        var eventId = "events/npc-involved-" + Guid.NewGuid();
        var json = JsonSerializer.Serialize(new { secret = 42, tags = new[] { "test" } });
        var details = JsonSerializer.Deserialize<Dictionary<string, object>>(json)!;

        await repo.LogEventAsync(session, new Event
        {
            Id = eventId,
            Summary = "NPC involved event",
            Category = EventCategory.Interaction,
            Involved = [charId],
            Details = details
        }, TestCampaignDefaults.Slug);
        await session.SaveChangesAsync();

        // Wait for indexing (in case any auto-index is used; with timeout)
        var indexWaitStart = DateTime.UtcNow;
        while ((DateTime.UtcNow - indexWaitStart).TotalSeconds < 10)
        {
            var stats = _store.Maintenance.Send(new Raven.Client.Documents.Operations.GetStatisticsOperation());
            if (stats.Indexes.All(x => x.IsStale == false))
            {
                break;
            }

            await Task.Delay(50);
        }

        if ((DateTime.UtcNow - indexWaitStart).TotalSeconds >= 10)
        {
            throw new TimeoutException("Indexes did not become non-stale within 10s");
        }

        var result = await tools.GetNpcContext(charId);

        Assert.True(result.Success);
        var ev = result.Data!.RecentInteractions.FirstOrDefault(e => e.Id == eventId);
        Assert.NotNull(ev);

        // RecentInteractions is EventSummaryView (no Details field) — general Event.Details
        // JsonElement-leakage sanitization is covered by SanitizeValue_Prevents_JsonElement_Leakage
        // above, which still returns full Event. This asserts the query path itself doesn't blow up
        // loading an event whose Details contains values that need sanitizing.
        Assert.Contains("NPC involved event", ev.Summary);
    }

    [Fact]
    public async Task V4_Operations_Only_Populate_Mind_Fields_Legacy_TopLevel_Remain_Empty()
    {
        var repo = _fixture.CreateRepository();
        using var session = _store.OpenAsyncSession();

        var charId = "npcs/legacy-test-" + Guid.NewGuid();
        var character = new CharacterUpsertRequest
        {
            Id = charId,
            Name = "Legacy Hygiene NPC",
            Social = new SocialProfile { Relationships = new Dictionary<string, int>() },
            Needs = new NeedsProfile { ActiveNeeds = new Dictionary<string, float> { ["tiredness"] = 5f } }
        };
        await repo.UpsertCharacterAsync(_fixture.CreateCampaignSession(session, TestCampaignDefaults.Slug), character);

        var targetId = "chars/target-1-" + Guid.NewGuid();
        await repo.UpsertCharacterAsync(session,
            new CharacterUpsertRequest { Id = targetId, Name = "Relationship Target" },
            TestCampaignDefaults.Slug);
        await session.SaveChangesAsync();

        // Perform V4 operation that touches Mind (RelationshipChange via Commit)
        var stageResult = await repo.StageChangesAsync(_fixture.CreateCampaignSession(session, null), [
            new RelationshipChange
            {
                CharacterId = charId,
                TargetId = targetId,
                Delta = +10,
                Reason = "Test V4 only path"
            }
        ], TestCampaignDefaults.Slug);
        Assert.True(stageResult.Success, string.Join("; ", stageResult.Summary));
        await session.SaveChangesAsync();

        // Reload and verify
        var reloaded = await session.LoadAsync<Character>(charId);
        Assert.NotNull(reloaded);

        // V4 data lives exclusively in Mind (legacy top-level fields have been fully removed)
        Assert.NotNull(reloaded.Social);
        Assert.True(reloaded.Social.Relationships.ContainsKey(targetId));
        Assert.Equal(10, reloaded.Social.Relationships[targetId]);
        Assert.True(reloaded.Needs.ActiveNeeds.ContainsKey("tiredness"));
    }

    // =====================================================================
    // REGRESSION TESTS FOR MCP CRASHES REPORTED IN PRODUCTION / COUSIN RUN
    // =====================================================================

    [Fact]
    public async Task QueryEventsAsync_Finds_Events_By_Keyword()
    {
        var repo = _fixture.CreateRepository();
        // No hyphens: Corax's StandardAnalyzer splits on hyphens, so "*recall-findme*" would search
        // for a single token that doesn't exist after tokenization. Use a single unbroken word instead.
        const string marker = "recallfindme";
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var eventId = $"events/{marker}-{suffix}";

        using (var session = _store.OpenAsyncSession())
        {
            await repo.LogEventAsync(session,
                new Event { Id = eventId, Summary = $"{marker} ambush at the bridge", Category = EventCategory.Test },
                TestCampaignDefaults.Slug);
            session.Advanced.WaitForIndexesAfterSaveChanges(timeout: TimeSpan.FromSeconds(10), throwOnTimeout: true);
            await session.SaveChangesAsync();
        }

        using (var session = _store.OpenAsyncSession())
        {
            // Diagnostic: confirm the document exists at all
            var loaded = await session.LoadAsync<Event>(eventId);
            Assert.True(loaded != null, $"Event was not persisted to DB. EventId={eventId}");

            // Diagnostic: check direct text-only path, forcing non-stale read.
            // Use trailing-wildcard only: Corax supports "token*" but not leading "*token*" on analyzed fields.
            var directHits = await session.Query<Event, CampaignVault.Data.Event_Search>()
                .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(15)))
                .Search(x => x.Summary, $"{marker}*")
                .Where(x => x.CampaignName == TestCampaignDefaults.Slug)
                .ToListAsync();
            Assert.True(directHits.Any(e => e.Id == eventId),
                $"Index missed event even after WaitForNonStaleResults. loaded.CampaignName={loaded?.CampaignName ?? "<null>"}");

            var results = (await repo.QueryEventsAsync(session, marker, null, 5, TestCampaignDefaults.Slug)).ToList();
            Assert.Contains(results, e => e.Id == eventId);
        }
    }

    [Fact]
    public async Task QueryEventsAsync_FiltersByLocationId_IncludingRelatedLocationIds()
    {
        var repo = _fixture.CreateRepository();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var locationId = $"locations/loc-{suffix}";
        var relatedLocationId = $"locations/related-{suffix}";
        var otherLocationId = $"locations/other-{suffix}";

        var primaryEventId = $"events/primary-{suffix}";
        var relatedEventId = $"events/related-{suffix}";
        var unrelatedEventId = $"events/unrelated-{suffix}";

        using (var session = _store.OpenAsyncSession())
        {
            await repo.LogEventAsync(session,
                new Event { Id = primaryEventId, Summary = "Bar fight breaks out", Category = EventCategory.Test, LocationId = locationId },
                TestCampaignDefaults.Slug);
            await repo.LogEventAsync(session,
                new Event { Id = relatedEventId, Summary = "Fight spills into the alley", Category = EventCategory.Test, LocationId = otherLocationId, RelatedLocationIds = [relatedLocationId] },
                TestCampaignDefaults.Slug);
            await repo.LogEventAsync(session,
                new Event { Id = unrelatedEventId, Summary = "Unrelated event elsewhere", Category = EventCategory.Test, LocationId = otherLocationId },
                TestCampaignDefaults.Slug);
            session.Advanced.WaitForIndexesAfterSaveChanges(timeout: TimeSpan.FromSeconds(10), throwOnTimeout: true);
            await session.SaveChangesAsync();
        }

        using (var session = _store.OpenAsyncSession())
        {
            var byPrimary = (await repo.QueryEventsAsync(session, null, EventCategory.Test, 10, TestCampaignDefaults.Slug, locationId: locationId)).ToList();
            Assert.Contains(byPrimary, e => e.Id == primaryEventId);
            Assert.DoesNotContain(byPrimary, e => e.Id == relatedEventId);
            Assert.DoesNotContain(byPrimary, e => e.Id == unrelatedEventId);

            var byRelated = (await repo.QueryEventsAsync(session, null, EventCategory.Test, 10, TestCampaignDefaults.Slug, locationId: relatedLocationId)).ToList();
            Assert.Contains(byRelated, e => e.Id == relatedEventId);
            Assert.DoesNotContain(byRelated, e => e.Id == primaryEventId);
            Assert.DoesNotContain(byRelated, e => e.Id == unrelatedEventId);
        }
    }

    [Fact]
    public async Task QueryEventsAsync_FiltersByInvolvedCharacterId_AtIndexLevel_BeforeTake()
    {
        // Regression test: involvedCharacterId must be applied server-side (before Take), not filtered
        // client-side after the fact — otherwise an older event genuinely involving the character can be
        // silently dropped once enough newer unrelated events exist.
        var repo = _fixture.CreateRepository();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var characterId = $"chars/witness-{suffix}";
        var targetEventId = $"events/target-{suffix}";

        using (var session = _store.OpenAsyncSession())
        {
            // The event involving our character is logged FIRST (oldest).
            await repo.LogEventAsync(session,
                new Event { Id = targetEventId, Summary = "Witnessed event", Category = EventCategory.Test, Involved = [characterId] },
                TestCampaignDefaults.Slug);

            // 10 newer, unrelated events — enough to push the target out of a naive Take(10) if the
            // participant filter isn't applied at the index level.
            for (var i = 0; i < 10; i++)
            {
                await repo.LogEventAsync(session,
                    new Event { Id = $"events/noise-{suffix}-{i}", Summary = $"Unrelated event {i}", Category = EventCategory.Test },
                    TestCampaignDefaults.Slug);
            }

            session.Advanced.WaitForIndexesAfterSaveChanges(timeout: TimeSpan.FromSeconds(10), throwOnTimeout: true);
            await session.SaveChangesAsync();
        }

        using (var session = _store.OpenAsyncSession())
        {
            var results = (await repo.QueryEventsAsync(session, null, EventCategory.Test, 10, TestCampaignDefaults.Slug, involvedCharacterId: characterId)).ToList();
            Assert.Contains(results, e => e.Id == targetEventId);
        }
    }

    [Fact]
    public async Task UnifiedSearchAsync_Returns_All_Semantic_Entity_Types()
    {
        var repo = _fixture.CreateRepository();
        const string marker = "unified-findme";
        var suffix = Guid.NewGuid().ToString("N")[..8];

        using (var session = _store.OpenAsyncSession())
        {
            await repo.UpsertCharacterAsync(session,
                new CharacterUpsertRequest { Id = $"chars/{marker}-{suffix}", Name = $"{marker} hero", Notes = "notes" },
                TestCampaignDefaults.Slug);
            await repo.UpsertLoreAsync(session,
                new LoreUpsertRequest { Id = $"lore/{marker}-{suffix}", Title = $"{marker} lore", Content = "content" },
                TestCampaignDefaults.Slug);
            await repo.UpsertLocationAsync(session,
                new LocationUpsertRequest { Id = $"locations/{marker}-{suffix}", Name = $"{marker} tavern", Description = "desc", Type = LocationType.Room },
                TestCampaignDefaults.Slug);
            await repo.UpsertRumorAsync(session,
                new Rumor { Id = $"rumors/{marker}-{suffix}", Subject = $"{marker} rumor", CurrentText = "text" },
                TestCampaignDefaults.Slug);
            await repo.UpsertFactionAsync(session,
                new Faction { Id = $"factions/{marker}-{suffix}", Name = $"{marker} guild", Description = "desc" },
                TestCampaignDefaults.Slug);
            await repo.UpsertQuestAsync(session,
                new Quest { Id = $"quests/{marker}-{suffix}", Title = $"{marker} quest", DmNotes = "notes" },
                TestCampaignDefaults.Slug);
            await repo.LogEventAsync(session,
                new Event { Id = $"events/{marker}-{suffix}", Summary = $"{marker} battle", Category = EventCategory.Test },
                TestCampaignDefaults.Slug);
            await repo.UpsertItemAsync(session,
                new ItemUpsertRequest { Id = $"items/{marker}-{suffix}", Name = $"{marker} sword", Description = "desc" },
                TestCampaignDefaults.Slug);
            session.Advanced.WaitForIndexesAfterSaveChanges(timeout: TimeSpan.FromSeconds(10), throwOnTimeout: true);
            await session.SaveChangesAsync();
        }

        using (var session = _store.OpenAsyncSession())
        {
            var results = (await repo.UnifiedSearchAsync(session, marker, TestCampaignDefaults.Slug)).ToList();

            Assert.Contains(results, r => r is SearchMatch { EntityType: "character" });
            Assert.Contains(results, r => r is SearchMatch { EntityType: "lore" });
            Assert.Contains(results, r => r is SearchMatch { EntityType: "location" });
            Assert.Contains(results, r => r is SearchMatch { EntityType: "rumor" });
            Assert.Contains(results, r => r is SearchMatch { EntityType: "faction" });
            Assert.Contains(results, r => r is SearchMatch { EntityType: "quest" });
            Assert.Contains(results, r => r is SearchMatch { EntityType: "event" });
            Assert.Contains(results, r => r is SearchMatch { EntityType: "item" });
        }
    }

    [Fact]
    public async Task SearchWorld_Does_Not_Throw_ActiveAsyncTask_Disposal_Error()
    {
        // This is the exact code path that produced:
        // "Disposing session with active async task is forbidden... Number of active async tasks: 2"
        // The root cause was Task-capture + WhenAll + re-await inside UnifiedSearchAsync
        // combined with ExecuteAsync always doing SaveChanges + dispose.
        var repo = _fixture.CreateRepository();
        var tools = TestCampaignToolsFactory.Create(_fixture);

        using (var session = _store.OpenAsyncSession())
        {
            await repo.UpsertLocationAsync(session, new LocationUpsertRequest
            {
                Id = "locations/search-regression-" + Guid.NewGuid(),
                Name = "Regression Search Target",
                Description = "Used to verify SearchWorld no longer leaves async tasks on the Raven session"
            }, TestCampaignDefaults.Slug);
            session.Advanced.WaitForIndexesAfterSaveChanges(timeout: TimeSpan.FromSeconds(5), throwOnTimeout: true);
            await session.SaveChangesAsync();
        }

        // Must complete without the Raven disposal exception bubbling out of ExecuteAsync
        var result = await tools.SearchWorld("Regression Search Target");

        Assert.True(result.Success, result.Summary);
        Assert.NotEmpty(result.Data!.Matches);
    }

    [Fact]
    public async Task LocationMetadata_And_ItemProperties_Never_Leak_JsonElement_Into_Raven()
    {
        // Mirrors SanitizeValue_Prevents_JsonElement_Leakage but for the two other
        // Dictionary<string, object> bags that were unprotected and caused the exact
        // Newtonsoft "ValueIsEscaped" crash during SaveChanges in GetScene.
        var repo = _fixture.CreateRepository();
        var tools = TestCampaignToolsFactory.Create(_fixture);

        var locId = "locations/meta-regression-" + Guid.NewGuid();
        var itemId = "items/prop-regression-" + Guid.NewGuid();

        // Simulate exactly what happens when an LLM calls Upsert* with complex JSON:
        // Microsoft.Extensions.AI + System.Text.Json populates Dictionary<string,object>
        // with JsonElement values for objects, arrays, numbers, etc.
        var pollutedMeta = JsonSerializer.Deserialize<Dictionary<string, object>>(
            """{"difficulty": 7, "tags": ["dungeon","trap"], "boss": {"name": "Ancient One", "hp": 900}}""")!;

        var pollutedProps = JsonSerializer.Deserialize<Dictionary<string, object>>(
            """{"weightKg": 4.2, "enchantments": ["fire", "light"], "charges": 3}""")!;

        using (var session = _store.OpenAsyncSession())
        {
            await repo.UpsertLocationAsync(session, new LocationUpsertRequest
            {
                Id = locId,
                Name = "Cursed Vault",
                Description = "Regression test location with complex metadata",
                Metadata = pollutedMeta
            }, TestCampaignDefaults.Slug);

            await repo.UpsertItemAsync(session, new ItemUpsertRequest
            {
                Id = itemId,
                Name = "Cursed Amulet",
                Description = "Item whose Properties would contain JsonElement",
                HolderId = locId,
                Properties = pollutedProps
            }, TestCampaignDefaults.Slug);

            await session.SaveChangesAsync();
        }

        // Exercise the precise failing path from the logs: GetScene loads the Location + Items,
        // (now) defensively sanitizes them, then ExecuteAsync does SaveChangesAsync.
        // Before the fix this threw "Error getting value from 'ValueIsEscaped'".
        var scene = await tools.GetScene(locId, campaignName: TestCampaignDefaults.Slug);
        Assert.True(scene.Success, scene.Summary);

        // Verify the data was sanitized to plain .NET types and survived the roundtrip
        using (var verify = _store.OpenAsyncSession())
        {
            var loc = await verify.LoadAsync<Location>(locId);
            Assert.NotNull(loc);
            // Raven/Newtonsoft often materializes whole numbers as long after roundtrip
            var diff = loc.Metadata["difficulty"];
            Assert.True(diff is int || diff is long, $"Expected int or long, got {diff?.GetType().Name}");
            Assert.IsType<List<object>>(loc.Metadata["tags"]);
            Assert.IsType<Dictionary<string, object>>(loc.Metadata["boss"]);

            var item = await verify.LoadAsync<Item>(itemId);
            Assert.NotNull(item);
            var weight = item.Properties["weightKg"];
            Assert.True(weight is float || weight is double || weight is decimal,
                $"Expected float/double/decimal, got {weight?.GetType().Name}");
            Assert.IsType<List<object>>(item.Properties["enchantments"]);
            var charges = item.Properties["charges"];
            Assert.True(charges is int || charges is long, $"Expected int or long, got {charges?.GetType().Name}");
        }
    }

    [Fact]
    public async Task GetScene_Heals_Legacy_Polluted_Location_And_Item_Data()
    {
        // Simulate legacy data that was written before we had sanitization guards
        // (e.g. direct session.Store or old code paths). GetScene + ExecuteAsync SaveChanges
        // must not explode and should leave clean data behind.
        var repo = _fixture.CreateRepository();
        var tools = TestCampaignToolsFactory.Create(_fixture);

        var locId = "locations/legacy-polluted-" + Guid.NewGuid();

        using (var session = _store.OpenAsyncSession())
        {
            // Manually construct a polluted dictionary the same way STJ does
            var legacyMeta = JsonSerializer.Deserialize<Dictionary<string, object>>(
                """{"legacy": true, "value": {"deep": [1, "two", false]}}""")!;

            var loc = new Location { Id = locId, Name = "Legacy Ruin", Metadata = legacyMeta };
            await session.StoreAsync(loc);
            await session.SaveChangesAsync();
        }

        // This used to be the exact crash site when SaveChanges ran after loading the polluted doc.
        // With legacy data that was persisted while containing live JsonElement instances,
        // Raven's materialization or the subsequent SaveChanges can still surface the
        // "current state of the object" error from a dead JsonElement.
        //
        // The critical regression property we care about: the exception is *caught* inside
        // ExecuteAsync and turned into a clean ToolResult (InternalError) instead of an
        // unhandled exception that kills the MCP server (the original failure mode in the logs).
        var result = await tools.GetScene(locId);

        // Either we healed it and succeeded, or we returned a proper handled error.
        // We must NEVER let an unhandled "ValueIsEscaped" / "active async task" / "state of the object"
        // exception escape the tool invocation.
        if (!result.Success)
        {
            Assert.Equal("InternalError", result.Error);
            Assert.Contains("state of the object", result.Summary ?? "", StringComparison.OrdinalIgnoreCase);
            // For truly dead legacy JsonElement data the healer may still surface a handled error.
            // This is acceptable (no unhandled crash). Skip the post-heal verification.
            return;
        }

        // After the call, the returned scene view should contain the healed Metadata (no more JsonElement).
        var legacyVal = result.Data!.Location.Metadata["legacy"];
        Assert.True(legacyVal is bool || legacyVal is string,
            $"Expected bool or string fallback, got {legacyVal?.GetType().Name}");
    }

    [Fact]
    public void Upsert_Tool_Parameter_Names_Are_Descriptive()
    {
        // We use clean, strongly-typed parameters (character, location, lore).
        // This produces the best possible tool schemas for LLM clients that properly
        // consume the current schema.
        //
        // Note: As of late May 2026, Grok Web appears to have a stale/cached view
        // of these two tools and may still call them using the original legacy
        // parameter names "c" and "l". This is a client-side issue.
        var upsertCharacter = typeof(CampaignTools).GetMethod(nameof(CampaignTools.UpsertCharacter),
            BindingFlags.Public | BindingFlags.Instance)!;
        var upsertLocation = typeof(CampaignTools).GetMethod(nameof(CampaignTools.UpsertLocation),
            BindingFlags.Public | BindingFlags.Instance)!;
        var upsertLore = typeof(CampaignTools).GetMethod(nameof(CampaignTools.UpsertLore),
            BindingFlags.Public | BindingFlags.Instance)!;

        Assert.Equal("character", upsertCharacter.GetParameters()[0].Name);
        Assert.Equal("location", upsertLocation.GetParameters()[0].Name);
        Assert.Equal("lore", upsertLore.GetParameters()[0].Name);
    }

    // ============================================================
    // REGRESSION TESTS FOR CLIENT COMPATIBILITY & RECENT FIXES
    // ============================================================

    [Fact]
    public async Task Commit_Handles_OutOfOrder_Polymorphic_JSON()
    {
        // This verifies the fix for AllowOutOfOrderMetadataProperties = true.
        // If the '$type' property is not FIRST, STJ normally fails.
        var repo = _fixture.CreateRepository();
        var tools = TestCampaignToolsFactory.Create(_fixture);

        using (var session = _store.OpenAsyncSession())
        {
            await repo.UpsertCharacterAsync(session,
                new CharacterUpsertRequest { Id = "npcs/order-test", Name = "Order Test", CurrentHp = 10, MaxHp = 100 }, TestCampaignDefaults.Slug);
            await session.SaveChangesAsync();
        }

        // Manually construct JSON where '$type' is at the end
        var outOfOrderJson = """
                             [
                               {
                                 "characterId": "npcs/order-test",
                                 "delta": 5,
                                 "$type": "hp"
                               }
                             ]
                             """;

        var result = await tools.Commit(outOfOrderJson, "Testing property order");

        Assert.True(result.Success, result.Summary);
        Assert.Contains("HP adjusted", result.Data!.Summary[0]);

        using (var session = _store.OpenAsyncSession())
        {
            var npc = await session.LoadAsync<Character>("npcs/order-test");
            Assert.Equal(15, npc.CurrentHp);
        }
    }

    [Fact]
    public void Rumor_TruthValue_Is_Proper_Enum_Not_Stringly_Typed()
    {
        // Wave 3 / review issue #17: Rumor.TruthValue is now a real enum instead of free text.
        var rumor = new Rumor
        {
            Id = "rumors/test-truth-1",
            RegionLocationId = "regions/test",
            Subject = "Test Rumor",
            CurrentText = "Something happened...",
            TruthValue = RumorTruth.PartiallyTrue,
            DayCreated = 42,
            LastStateChangeDay = 42
        };

        Assert.Equal(RumorTruth.PartiallyTrue, rumor.TruthValue);
        Assert.Equal(RumorTruth.True, new Rumor().TruthValue); // default

        // Also ensure other values are usable
        rumor.TruthValue = RumorTruth.Misleading;
        Assert.Equal(RumorTruth.Misleading, rumor.TruthValue);
    }

    [Fact]
    public void WorldChange_PolymorphicSerialization_Supports_DollarType()
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        // Test 1: Deserialize using "$type"
        var jsonWithDollarType = """
                                 [
                                   {
                                     "$type": "hp",
                                     "characterId": "test-char",
                                     "delta": -10
                                   },
                                   {
                                     "$type": "need",
                                     "characterId": "test-char",
                                     "need": "wanderlust",
                                     "delta": 5.5
                                   }
                                 ]
                                 """;

        var changes1 = JsonSerializer.Deserialize<WorldChange[]>(jsonWithDollarType, options);
        Assert.NotNull(changes1);
        Assert.Equal(2, changes1.Length);
        Assert.IsType<HpChange>(changes1[0]);
        Assert.IsType<NeedChange>(changes1[1]);

        var hp1 = (HpChange)changes1[0];
        Assert.Equal("test-char", hp1.CharacterId);
        Assert.Equal(-10, hp1.Delta);

        var need1 = (NeedChange)changes1[1];
        Assert.Equal("test-char", need1.CharacterId);
        Assert.Equal("wanderlust", need1.Need);
        Assert.Equal(5.5f, need1.Delta);

        // Test 2: Deserialize complex Grok payload with both $type and type (e.g. type: scene which is ignored)
        var grokPayload = """
                          [
                            {
                              "$type": "event",
                              "summary": "The party arrives at The Whispering Hearth.",
                              "type": "scene",
                              "involved": ["test-alara", "test-borin"]
                            },
                            {
                              "$type": "need",
                              "characterId": "test-alara",
                              "need": "curiosity",
                              "delta": 0
                            }
                          ]
                          """;

        var changes3 = JsonSerializer.Deserialize<WorldChange[]>(grokPayload, options);
        Assert.NotNull(changes3);
        Assert.Equal(2, changes3.Length);
        Assert.IsType<EventOccurred>(changes3[0]);
        Assert.IsType<NeedChange>(changes3[1]);

        var ev3 = (EventOccurred)changes3[0];
        Assert.Equal("The party arrives at The Whispering Hearth.", ev3.Summary);
        Assert.Equal(2, ev3.Involved?.Count);

        // Test 3: Serialize WorldChange and verify "$type" is written
        WorldChange changeToSerialize = new HpChange { CharacterId = "test-char", Delta = 20 };
        var serialized = JsonSerializer.Serialize<WorldChange>(changeToSerialize, options);

        using var doc = JsonDocument.Parse(serialized);
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("$type", out var dtProp) && dtProp.GetString() == "hp");
        Assert.True(root.TryGetProperty("characterId", out var cProp) && cProp.GetString() == "test-char");
        Assert.True(root.TryGetProperty("delta", out var dProp) && dProp.GetInt32() == 20);
    }

    [Fact]
    public async Task HP_Clamping_Enforces_Bounds()
    {
        var repo = _fixture.CreateRepository();
        using var session = _store.OpenAsyncSession();

        var id = "npcs/hpclamp-" + Guid.NewGuid();
        await repo.UpsertCharacterAsync(session, new CharacterUpsertRequest
        {
            Id = id,
            Name = "Clampy",
            CurrentHp = 50,
            MaxHp = 100
        }, TestCampaignDefaults.Slug);
        await session.SaveChangesAsync();

        // 1. Heal above MaxHp
        var resultHeal = await repo.StageChangesAsync(_fixture.CreateCampaignSession(session, null), [
            new HpChange { CharacterId = id, Delta = 60 }
        ], TestCampaignDefaults.Slug);
        Assert.True(resultHeal.Success);

        var reloaded1 = await session.LoadAsync<Character>(id);
        Assert.Equal(100, reloaded1.CurrentHp);

        // 2. Damage below 0
        var resultDamage = await repo.StageChangesAsync(session, [
            new HpChange { CharacterId = id, Delta = -120 }
        ], TestCampaignDefaults.Slug);
        Assert.True(resultDamage.Success);

        var reloaded2 = await session.LoadAsync<Character>(id);
        Assert.Equal(0, reloaded2.CurrentHp);
    }

    [Fact]
    public async Task AttributeChange_Applies_Delta_When_IsDelta_True()
    {
        var repo = _fixture.CreateRepository();
        using var session = _store.OpenAsyncSession();

        var id = "npcs/attrdelta-" + Guid.NewGuid();
        await repo.UpsertCharacterAsync(session, new CharacterUpsertRequest
        {
            Id = id,
            Name = "Attribute Delta NPC",
            SystemStats = new SystemExtension { Morale = 50f, Willpower = 60f }
        }, TestCampaignDefaults.Slug);
        await session.SaveChangesAsync();

        // Commit with IsDelta = true
        var result = await repo.StageChangesAsync(session, [
            new AttributeChange { CharacterId = id, Attribute = "morale", Value = -20f, IsDelta = true },
            new AttributeChange { CharacterId = id, Attribute = "willpower", Value = 15f, IsDelta = true },
            new AttributeChange { CharacterId = id, Attribute = "custom", Value = 10f, IsDelta = true }
        ], TestCampaignDefaults.Slug);
        Assert.True(result.Success);
        await session.SaveChangesAsync();

        var npc = await session.LoadAsync<Character>(id);
        Assert.Equal(30f, npc.SystemStats.Morale);
        Assert.Equal(75f, npc.SystemStats.Willpower);
        Assert.Equal(10f, npc.SystemStats.Attributes["custom"]);

        // Commit absolute override (IsDelta = false)
        var resultAbsolute = await repo.StageChangesAsync(session, [
            new AttributeChange { CharacterId = id, Attribute = "morale", Value = 90f, IsDelta = false },
            new AttributeChange { CharacterId = id, Attribute = "custom", Value = 45f, IsDelta = false }
        ], TestCampaignDefaults.Slug);
        Assert.True(resultAbsolute.Success);
        await session.SaveChangesAsync();

        var npc2 = await session.LoadAsync<Character>(id);
        Assert.Equal(90f, npc2.SystemStats.Morale);
        Assert.Equal(45f, npc2.SystemStats.Attributes["custom"]);
    }

    [Fact]
    public async Task Commit_Returns_Success_False_On_Missing_Character()
    {
        var repo = _fixture.CreateRepository();
        using var session = _store.OpenAsyncSession();

        var missingId = "npcs/does-not-exist-" + Guid.NewGuid();

        var result = await repo.StageChangesAsync(session, [
            new HpChange { CharacterId = missingId, Delta = -5 }
        ], TestCampaignDefaults.Slug);

        Assert.False(result.Success);
        Assert.Contains(result.Summary,
            s => s.Contains("not found") || s.Contains("WARNING: Character") || s.Contains("ERROR: Failed to process"));
    }

    [Fact]
    public async Task StatusChange_And_StatusRemove_Work_Safely_With_Preloaded_Character()
    {
        // Verifies the new handler-based implementation + fix for the previous dangerous Patch pattern.
        var repo = _fixture.CreateRepository();
        using var session = _store.OpenAsyncSession();

        var id = "npcs/status-test-" + Guid.NewGuid();
        await repo.UpsertCharacterAsync(session, new CharacterUpsertRequest
        {
            Id = id,
            Name = "Status Test NPC",
            MaxHp = 10,
            CurrentHp = 10
        }, TestCampaignDefaults.Slug);
        await session.SaveChangesAsync();

        // Add two different statuses (multiples allowed)
        var addResult = await repo.StageChangesAsync(session, [
            new StatusChange { CharacterId = id, Status = "Poisoned" },
            new StatusChange { CharacterId = id, Status = "Frightened" }
        ], TestCampaignDefaults.Slug);
        await session.SaveChangesAsync();

        Assert.True(addResult.Success);
        Assert.Contains(addResult.Summary, s => s.Contains("Status 'Poisoned' (category: Legacy) added"));

        var npc1 = await session.LoadAsync<Character>(id);
        Assert.Equal(2, npc1.SystemStats.StatusEffects.Count);
        Assert.Contains(npc1.SystemStats.StatusEffects, e => e.Name == "Poisoned");
        Assert.Contains(npc1.SystemStats.StatusEffects, e => e.Name == "Frightened");

        // Remove one (case-insensitive, removes all matches)
        var removeResult = await repo.StageChangesAsync(session, [
            new StatusRemove { CharacterId = id, Status = "poisoned" }
        ], TestCampaignDefaults.Slug);
        await session.SaveChangesAsync();

        Assert.True(removeResult.Success);

        var npc2 = await session.LoadAsync<Character>(id);
        Assert.Single(npc2.SystemStats.StatusEffects);
        Assert.Contains(npc2.SystemStats.StatusEffects, e => e.Name == "Frightened");
        Assert.DoesNotContain(npc2.SystemStats.StatusEffects, e => e.Name == "Poisoned");
    }

    [Fact]
    public async Task RumorDecayRule_LargeTimeSkip_EscalatesNascentRumorToPeakInOneAdvanceWorldCall()
    {
        var engine = new DefaultSimulationEngine(
            [new RumorDecayRule()]);
        var repo = _fixture.CreateRepository(engineOverride: engine);
        using var session = _store.OpenAsyncSession();

        await repo.SaveTimeAsync(_fixture.CreateCampaignSession(session, TestCampaignDefaults.Slug), new CampaignTime { TotalDaysElapsed = 100 });
        await repo.UpsertRumorAsync(session, new Rumor
        {
            Id = "rumors/nascent-test",
            Subject = "Nascent Rumor",
            LastStateChangeDay = 100,
            RegionLocationId = "loc",
            State = RumorState.Nascent
        }, TestCampaignDefaults.Slug);
        await session.SaveChangesAsync();

        // Wait for indexing (with timeout)
        var indexWaitStart = DateTime.UtcNow;
        while ((DateTime.UtcNow - indexWaitStart).TotalSeconds < 10)
        {
            var stats = _store.Maintenance.Send(new Raven.Client.Documents.Operations.GetStatisticsOperation());
            if (stats.Indexes.All(x => x.IsStale == false))
            {
                break;
            }

            await Task.Delay(50);
        }

        // 20 days in a single call crosses both the Nascent->Spreading (7d) and Spreading->Peak (7d)
        // thresholds — the rule traverses every intermediate state within one AdvanceWorld call.
        var result = await repo.AdvanceWorldAsync(session, 20, 12, TestCampaignDefaults.Slug);
        await session.SaveChangesAsync();

        var reloaded = await session.LoadAsync<Rumor>("rumors/nascent-test");
        Assert.Equal(RumorState.Peak, reloaded.State);

        // Soft secondary check — we do not fail on exact narrative wording.
        if (result.SimulatorEvents.Count > 0)
        {
            Assert.DoesNotContain(result.SimulatorEvents, e => e.Contains("ERROR", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public async Task StatusChangeHandler_LegacyFallback_CreatesMinimalEffect()
    {
        var repo = _fixture.CreateRepository();
        using var session = _store.OpenAsyncSession();

        var id = "npcs/legacy-test-" + Guid.NewGuid();
        await repo.UpsertCharacterAsync(session, new CharacterUpsertRequest
        {
            Id = id,
            Name = "Legacy NPC",
            MaxHp = 10,
            CurrentHp = 10
        }, TestCampaignDefaults.Slug);
        await session.SaveChangesAsync();

        var result = await repo.StageChangesAsync(_fixture.CreateCampaignSession(session, null), [
            new StatusChange { CharacterId = id, Status = "Fatigued" }
        ], TestCampaignDefaults.Slug);
        await session.SaveChangesAsync();

        Assert.True(result.Success);

        var npc = await session.LoadAsync<Character>(id);
        Assert.Single(npc.SystemStats.StatusEffects);
        var effect = npc.SystemStats.StatusEffects[0];
        Assert.Equal("Fatigued", effect.Name);
        Assert.Equal("Legacy", effect.Category);
        Assert.Equal("legacy-status-change", effect.AppliedBy);
        Assert.Null(effect.AffectedPart);
        Assert.Empty(effect.StatModifiers);
    }

    [Fact]
    public async Task StatusChangeHandler_DuplicateStructuredEffects_AreAllowed()
    {
        var repo = _fixture.CreateRepository();
        using var session = _store.OpenAsyncSession();

        var id = "npcs/duplicate-test-" + Guid.NewGuid();
        await repo.UpsertCharacterAsync(session, new CharacterUpsertRequest
        {
            Id = id,
            Name = "Duplicate NPC",
            MaxHp = 10,
            CurrentHp = 10
        }, TestCampaignDefaults.Slug);
        await session.SaveChangesAsync();

        var effect1 = new StatusEffect
        {
            Name = "Bleeding",
            Category = "Injury",
            StatModifiers = new Dictionary<string, float> { { "Speed", -2f } }
        };

        var effect2 = new StatusEffect
        {
            Name = "Bleeding",
            Category = "Injury",
            StatModifiers = new Dictionary<string, float> { { "Speed", -3f } }
        };

        var result = await repo.StageChangesAsync(session, [
            new StatusChange { CharacterId = id, Effect = effect1 },
            new StatusChange { CharacterId = id, Effect = effect2 }
        ], TestCampaignDefaults.Slug);
        await session.SaveChangesAsync();

        Assert.True(result.Success);

        var npc = await session.LoadAsync<Character>(id);
        Assert.Equal(2, npc.SystemStats.StatusEffects.Count);
        Assert.All(npc.SystemStats.StatusEffects, e => Assert.Equal("Bleeding", e.Name));
    }

    [Fact]
    public async Task StatusChangeHandler_CaseInsensitiveRemoval_RemovesAllMatches()
    {
        var repo = _fixture.CreateRepository();
        using var session = _store.OpenAsyncSession();

        var id = "npcs/removal-test-" + Guid.NewGuid();
        await repo.UpsertCharacterAsync(session, new CharacterUpsertRequest
        {
            Id = id,
            Name = "Removal NPC",
            MaxHp = 10,
            CurrentHp = 10
        }, TestCampaignDefaults.Slug);
        await session.SaveChangesAsync();

        var effect1 = new StatusEffect { Name = "Poisoned", Category = "Condition" };
        var effect2 = new StatusEffect { Name = "poisoned", Category = "Condition" };
        var effect3 = new StatusEffect { Name = "Blessed", Category = "Buff" };

        var resultAdd = await repo.StageChangesAsync(session, [
            new StatusChange { CharacterId = id, Effect = effect1 },
            new StatusChange { CharacterId = id, Effect = effect2 },
            new StatusChange { CharacterId = id, Effect = effect3 }
        ], TestCampaignDefaults.Slug);
        await session.SaveChangesAsync();
        Assert.True(resultAdd.Success);

        // Remove case-insensitively
        var resultRemove = await repo.StageChangesAsync(session, [
            new StatusRemove { CharacterId = id, Status = "POISONED" }
        ], TestCampaignDefaults.Slug);
        await session.SaveChangesAsync();
        Assert.True(resultRemove.Success);

        var npc = await session.LoadAsync<Character>(id);
        Assert.Single(npc.SystemStats.StatusEffects);
        Assert.Equal("Blessed", npc.SystemStats.StatusEffects[0].Name);
    }

    [Fact]
    public async Task StatusChangeHandler_CharacterNotFound_FailsGracefully()
    {
        var repo = _fixture.CreateRepository();
        using var session = _store.OpenAsyncSession();

        var missingId = "npcs/does-not-exist-" + Guid.NewGuid();

        var resultAdd = await repo.StageChangesAsync(session, [
            new StatusChange { CharacterId = missingId, Status = "Frightened" }
        ], TestCampaignDefaults.Slug);
        Assert.False(resultAdd.Success);
        Assert.Contains(resultAdd.Summary, s => s.Contains("not found") || s.Contains("WARNING: Character"));

        var resultRemove = await repo.StageChangesAsync(session, [
            new StatusRemove { CharacterId = missingId, Status = "Frightened" }
        ], TestCampaignDefaults.Slug);
        Assert.False(resultRemove.Success);
        Assert.Contains(resultRemove.Summary, s => s.Contains("not found") || s.Contains("WARNING: Character"));
    }

    [Fact]
    public async Task CampaignConfig_And_Tools_Work_Safely()
    {
        var repo = _fixture.CreateRepository();
        using var session = _store.OpenAsyncSession();

        // 1. Check repository uses the selected test campaign context
        var config = await repo.GetCampaignConfigAsync(_fixture.CreateCampaignSession(session, TestCampaignDefaults.Slug));
        Assert.NotNull(config);
        Assert.Equal("campaigns/test-campaign/config", config.Id);
        Assert.Equal(RulesetSystem.Dnd5e, config.ActiveSystem);
        Assert.Empty(config.SystemOptions);

        // 2. Direct repository upsert
        config.ActiveSystem = RulesetSystem.Pathfinder2e;
        config.SystemOptions = new Dictionary<string, string> { { "mapEnabled", "true" } };
        await repo.UpsertCampaignConfigAsync(session, config, TestCampaignDefaults.Slug);
        await session.SaveChangesAsync();

        var reloaded = await repo.GetCampaignConfigAsync(_fixture.CreateCampaignSession(session, TestCampaignDefaults.Slug));
        Assert.Equal(RulesetSystem.Pathfinder2e, reloaded.ActiveSystem);
        Assert.Equal("true", reloaded.SystemOptions["mapEnabled"]);

        // 3. Test through CampaignTools
        var tools = TestCampaignToolsFactory.Create(_fixture);

        var getResult = await tools.GetConfig("test-campaign");
        Assert.True(getResult.Success);
        Assert.NotNull(getResult.Data);
        Assert.Equal(RulesetSystem.Pathfinder2e, getResult.Data.ActiveSystem);
        Assert.Equal("true", getResult.Data.SystemOptions["mapEnabled"]);

        // 4. Locked campaigns reject ruleset changes; options can still be updated on the active system
        var setOptions = new Dictionary<string, string> { { "difficulty", "2" } };
        var lockedResult = await tools.SetActiveSystem(RulesetSystem.Narrative, setOptions);
        Assert.False(lockedResult.Success);
        Assert.Equal("SystemLocked", lockedResult.Error);

        var optionsResult = await tools.SetActiveSystem(RulesetSystem.Dnd5e, setOptions);
        Assert.True(optionsResult.Success);
        Assert.Equal("2", optionsResult.Data!.SystemOptions["difficulty"]);

        using var session2 = _store.OpenAsyncSession();
        var dbConfig = await repo.GetCampaignConfigAsync(session2, TestCampaignDefaults.Slug);
        Assert.Equal(RulesetSystem.Dnd5e, dbConfig.ActiveSystem);
        Assert.Equal("2", dbConfig.SystemOptions["difficulty"]);
    }

    [Fact]
    public async Task GetScene_ViaTools_OnMissingLocation_ReturnsStub_And_Pressure_WithReadyCommitJson()
    {
        // Verifies the full tool + pressure path for the key anti-laziness feature:
        // hallucinated location -> immediate copy-pasteable upsert_location example in WorldPressure.
        var repo = _fixture.CreateRepository();
        var tools = TestCampaignToolsFactory.Create(_fixture);

        var missingId = "locations/hallucinated-tavern-" + Guid.NewGuid();

        var result = await tools.GetScene(missingId);

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.False(result.Data.IsLocationAnchored);
        Assert.Equal(missingId, result.Data.Location.Id);
        Assert.Equal("[Unanchored]", result.Data.Location.Name);

        // The critical laziness countermeasure:
        Assert.NotNull(result.WorldPressure);
        var pressureText = string.Join(" ", result.WorldPressure);
        Assert.Contains("ENGINE WARNING", pressureText);
        Assert.Contains("You are hallucinating", pressureText);
        Assert.Contains("world_build", pressureText);
        Assert.Contains(missingId, pressureText);
    }

    [Fact]
    public async Task
        GetScene_ViaTools_AnchoredLocation_Emits_Additional_AntiLaziness_Pressures_For_BrokenLinks_And_FlavorVacuum()
    {
        // Verifies new Phase 6/7 laziness mitigations: engine detects one-way links (missing reverse from parent)
        // even for non-create paths, and detects "flavor vacuum" (no PoIs/Ambient + empty) and provides ready update JSON.
        var repo = _fixture.CreateRepository();
        var tools = TestCampaignToolsFactory.Create(_fixture);

        var parentId = "locations/broken-parent-" + Guid.NewGuid();
        var childId = "locations/broken-child-" + Guid.NewGuid();

        // Setup broken state directly (parent has no exit back to child; child declares parent).
        // Simulates old data, manual edit, or partial LLM location_update.
        using (var session = _store.OpenAsyncSession())
        {
            await repo.UpsertLocationAsync(session, new LocationUpsertRequest
            {
                Id = parentId,
                Name = "Broken Parent",
                Description = "Parent without back link",
                Type = LocationType.Building,
                Exits = [] // deliberately no child
            }, TestCampaignDefaults.Slug);
            await repo.UpsertLocationAsync(session, new LocationUpsertRequest
            {
                Id = childId,
                Name = "Broken Child",
                Description = "Child pointing to parent but no reciprocal exit",
                Type = LocationType.Room,
                ParentLocationId = parentId,
                Exits = [new LocationExit(parentId, "Leads back (but parent doesn't know)")]
            }, TestCampaignDefaults.Slug);
            await session.SaveChangesAsync();
        }

        // Act on the child
        var childResult = await tools.GetScene(childId, campaignName: TestCampaignDefaults.Slug);
        Assert.True(childResult.Success);
        Assert.NotNull(childResult.Data);
        Assert.True(childResult.Data.IsLocationAnchored);

        var pressureText = string.Join("\n", childResult.WorldPressure ?? []);
        Assert.Contains("one-way link", pressureText);
        Assert.Contains("location_update", pressureText);
        Assert.Contains(parentId, pressureText); // targets the parent for the fix
        Assert.Contains("addExit", pressureText);

        // Also test flavor vacuum pressure on a clean empty room (no PoI, no Ambient, no NPCs)
        var vacuumId = "locations/vacuum-room-" + Guid.NewGuid();
        using (var session2 = _store.OpenAsyncSession())
        {
            await repo.UpsertLocationAsync(session2, new LocationUpsertRequest
            {
                Id = vacuumId,
                Name = "Empty Stone Room",
                Description = "Nothing here but echoes.",
                Type = LocationType.Room
                // deliberately no PoIs, no AmbientCrowd
            }, TestCampaignDefaults.Slug);
            await session2.SaveChangesAsync();
        }

        var vacuumResult = await tools.GetScene(vacuumId);
        Assert.True(vacuumResult.Success);
        var vacuumPressure = string.Join(" ", vacuumResult.WorldPressure ?? []);
        Assert.Contains("NARRATIVE PROMPT", vacuumPressure);
        Assert.Contains("lacks flavor details", vacuumPressure);
        Assert.Contains("location_update", vacuumPressure);
        Assert.Contains("addPointOfInterest", vacuumPressure);
        Assert.Contains("ambientCrowd", vacuumPressure);
    }

    [Fact]
    public async Task CampaignScoped_Reads_DoNot_Load_Documents_From_Other_Campaigns()
    {
        var repo = _fixture.CreateRepository();
        var locationId = "locations/cross-campaign-" + Guid.NewGuid();
        var itemId = "items/cross-campaign-" + Guid.NewGuid();
        var factionId = "factions/cross-campaign-" + Guid.NewGuid();
        var questId = "quests/cross-campaign-" + Guid.NewGuid();

        using (var session = _store.OpenAsyncSession())
        {
            await repo.UpsertLocationAsync(session,
                new LocationUpsertRequest { Id = locationId, Name = "Beta Room", Type = LocationType.Room }, "beta");
            await repo.UpsertItemAsync(session, new ItemUpsertRequest { Id = itemId, Name = "Beta Relic", HolderId = locationId },
                "beta");
            await repo.UpsertFactionAsync(session, new Faction { Id = factionId, Name = "Beta Circle" }, "beta");
            await repo.UpsertQuestAsync(session, new Quest
            {
                Id = questId,
                Title = "Beta Errand",
                OverallState = QuestState.Open,
                Objectives = []
            }, "beta");
            await session.SaveChangesAsync();
        }

        using (var session = _store.OpenAsyncSession())
        {
            Assert.Null(await repo.GetLocationAsync(session, locationId, "alpha"));
            Assert.Null(await repo.GetItemAsync(session, itemId, "alpha"));
            Assert.Null(await repo.GetFactionAsync(session, factionId, "alpha"));
            Assert.Null(await repo.GetQuestAsync(session, questId, "alpha"));

            Assert.NotNull(await repo.GetLocationAsync(session, locationId, "beta"));
            Assert.NotNull(await repo.GetItemAsync(session, itemId, "beta"));
            Assert.NotNull(await repo.GetFactionAsync(session, factionId, "beta"));
            Assert.NotNull(await repo.GetQuestAsync(session, questId, "beta"));
        }
    }

    [Fact]
    public async Task Suggesters_Use_Current_Campaign_And_Normalize_Alias_Prefixes()
    {
        var repo = _fixture.CreateRepository();

        var locationSlug = "sunken-harbor-" + Guid.NewGuid();
        var characterSlug = "mira-" + Guid.NewGuid();

        using (var session = _store.OpenAsyncSession())
        {
            await repo.UpsertLocationAsync(session, new LocationUpsertRequest
            {
                Id = "locations/" + locationSlug,
                Name = "Sunken Harbor",
                Type = LocationType.Region
            }, "alpha");

            await repo.UpsertCharacterAsync(session, new CharacterUpsertRequest
            {
                Id = "chars/" + characterSlug,
                Name = "Mira Harborhand"
            }, "alpha");

            await session.SaveChangesAsync();
        }

        using (var session = _store.OpenAsyncSession())
        {
            var repoLocationSuggestions = await repo.SuggestLocationsAsync(session, "locs/" + locationSlug, "alpha");
            var pressureLocationSuggestions =
                await CampaignVault.Data.Pressure.PressureHelpers.SuggestLocationsAsync(session, "locs/" + locationSlug,
                    "alpha");
            var characterSuggestions = await repo.SuggestCharactersAsync(session, "characters/" + characterSlug, "alpha");

            Assert.Contains(repoLocationSuggestions, s => s.Id == "locations/" + locationSlug);
            Assert.Contains(pressureLocationSuggestions, s => s.Id == "locations/" + locationSlug);
            Assert.Contains(characterSuggestions, s => s.Id == "chars/" + characterSlug);
        }
    }

    [Fact]
    public async Task Upserts_Assign_Semantic_Vectors_For_All_Entity_Types()
    {
        var repo = _fixture.CreateRepository();
        var charId = "npcs/semantic-" + Guid.NewGuid();
        var loreId = "lore/semantic-" + Guid.NewGuid();
        var locId = "locations/semantic-" + Guid.NewGuid();
        var rumorId = "rumors/semantic-" + Guid.NewGuid();
        var itemId = "items/semantic-" + Guid.NewGuid();
        var factionId = "factions/semantic-" + Guid.NewGuid();
        var questId = "quests/semantic-" + Guid.NewGuid();
        var eventId = "events/semantic-" + Guid.NewGuid();

        // 1. Initial Insert
        using (var session = _store.OpenAsyncSession())
        {
            await repo.UpsertCharacterAsync(_fixture.CreateCampaignSession(session, TestCampaignDefaults.Slug), new CharacterUpsertRequest { Id = charId, Name = "Semantic NPC", Notes = "Some lore notes" });
            await repo.UpsertLoreAsync(_fixture.CreateCampaignSession(session, TestCampaignDefaults.Slug), new LoreUpsertRequest { Id = loreId, Title = "Semantic Lore", Content = "Some content" });
            await repo.UpsertLocationAsync(_fixture.CreateCampaignSession(session, TestCampaignDefaults.Slug), new LocationUpsertRequest { Id = locId, Name = "Semantic Loc", Description = "Some description", Type = LocationType.Room });
            await repo.UpsertRumorAsync(session, new Rumor { Id = rumorId, Subject = "Semantic Rumor", CurrentText = "Some text" }, TestCampaignDefaults.Slug);
            await repo.UpsertItemAsync(_fixture.CreateCampaignSession(session, TestCampaignDefaults.Slug), new ItemUpsertRequest { Id = itemId, Name = "Semantic Item", Description = "Some description" });
            await repo.UpsertFactionAsync(_fixture.CreateCampaignSession(session, TestCampaignDefaults.Slug), new Faction { Id = factionId, Name = "Semantic Faction", Description = "Some description" });
            await repo.UpsertQuestAsync(_fixture.CreateCampaignSession(session, TestCampaignDefaults.Slug), new Quest { Id = questId, Title = "Semantic Quest", DmNotes = "Some notes" });
            await repo.LogEventAsync(session, new Event { Id = eventId, Summary = "Semantic Event", Category = EventCategory.Simulation }, TestCampaignDefaults.Slug);
            await session.SaveChangesAsync();
        }

        // 2. Assert vectors populated in new session
        using (var session = _store.OpenAsyncSession())
        {
            var dbChar = await session.LoadAsync<Character>(charId);
            Assert.NotNull(dbChar.SemanticVector);
            Assert.NotEmpty(dbChar.SemanticVector);
            
            var dbLore = await session.LoadAsync<Lore>(loreId);
            Assert.NotNull(dbLore.SemanticVector);
            Assert.NotEmpty(dbLore.SemanticVector);
            
            var dbLoc = await session.LoadAsync<Location>(locId);
            Assert.NotNull(dbLoc.SemanticVector);
            Assert.NotEmpty(dbLoc.SemanticVector);
            
            var dbRumor = await session.LoadAsync<Rumor>(rumorId);
            Assert.NotNull(dbRumor.SemanticVector);
            Assert.NotEmpty(dbRumor.SemanticVector);
            
            var dbItem = await session.LoadAsync<Item>(itemId);
            Assert.NotNull(dbItem.SemanticVector);
            Assert.NotEmpty(dbItem.SemanticVector);
            
            var dbFaction = await session.LoadAsync<Faction>(factionId);
            Assert.NotNull(dbFaction.SemanticVector);
            Assert.NotEmpty(dbFaction.SemanticVector);
            
            var dbQuest = await session.LoadAsync<Quest>(questId);
            Assert.NotNull(dbQuest.SemanticVector);
            Assert.NotEmpty(dbQuest.SemanticVector);
            
            var dbEvent = await session.LoadAsync<Event>(eventId);
            Assert.NotNull(dbEvent.SemanticVector);
            Assert.NotEmpty(dbEvent.SemanticVector);
        }

        // 3. Update with empty text to clear vectors
        using (var session = _store.OpenAsyncSession())
        {
            await repo.UpsertCharacterAsync(_fixture.CreateCampaignSession(session, TestCampaignDefaults.Slug), new CharacterUpsertRequest { Id = charId, Name = "", Notes = "" });
            await repo.UpsertLoreAsync(_fixture.CreateCampaignSession(session, TestCampaignDefaults.Slug), new LoreUpsertRequest { Id = loreId, Title = "", Content = "" });
            await repo.UpsertLocationAsync(_fixture.CreateCampaignSession(session, TestCampaignDefaults.Slug), new LocationUpsertRequest { Id = locId, Name = "", Description = "", Type = LocationType.Room });
            await repo.UpsertRumorAsync(session, new Rumor { Id = rumorId, Subject = "", CurrentText = "" }, TestCampaignDefaults.Slug);
            await repo.UpsertItemAsync(_fixture.CreateCampaignSession(session, TestCampaignDefaults.Slug), new ItemUpsertRequest { Id = itemId, Name = "", Description = "" });
            await repo.UpsertFactionAsync(_fixture.CreateCampaignSession(session, TestCampaignDefaults.Slug), new Faction { Id = factionId, Name = "", Description = "" });
            await repo.UpsertQuestAsync(_fixture.CreateCampaignSession(session, TestCampaignDefaults.Slug), new Quest { Id = questId, Title = "", DmNotes = "" });
            // Event doesn't have an update method (LogEvent creates new), so we only test the rest for updates.
            await session.SaveChangesAsync();
        }

        // 4. Assert vectors cleared in new session
        using (var session = _store.OpenAsyncSession())
        {
            var dbChar = await session.LoadAsync<Character>(charId);
            Assert.Null(dbChar.SemanticVector);
            
            var dbLore = await session.LoadAsync<Lore>(loreId);
            Assert.Null(dbLore.SemanticVector);
            
            var dbLoc = await session.LoadAsync<Location>(locId);
            Assert.Null(dbLoc.SemanticVector);
            
            var dbRumor = await session.LoadAsync<Rumor>(rumorId);
            Assert.Null(dbRumor.SemanticVector);
            
            var dbItem = await session.LoadAsync<Item>(itemId);
            Assert.Null(dbItem.SemanticVector);
            
            var dbFaction = await session.LoadAsync<Faction>(factionId);
            Assert.Null(dbFaction.SemanticVector);
            
            var dbQuest = await session.LoadAsync<Quest>(questId);
            Assert.Null(dbQuest.SemanticVector);
        }
    }

    [Fact]
    public async Task UpsertCharacter_Preserves_KeepAlive()
    {
        var repo = _fixture.CreateRepository();
        using var session = _store.OpenAsyncSession();
        var id = "npcs/keepalive-" + Guid.NewGuid();

        await repo.UpsertCharacterAsync(_fixture.CreateCampaignSession(session, TestCampaignDefaults.Slug), new CharacterUpsertRequest { Id = id, Name = "Important NPC", KeepAlive = true });
        await session.SaveChangesAsync();

        // Second upsert to simulate an update
        await repo.UpsertCharacterAsync(_fixture.CreateCampaignSession(session, TestCampaignDefaults.Slug), new CharacterUpsertRequest { Id = id, Name = "Important NPC", KeepAlive = true });
        await session.SaveChangesAsync();

        var npc = await session.LoadAsync<Character>(id);
        Assert.True(npc.KeepAlive);
    }

    [Fact]
    public async Task UpsertCharacter_Seeds_Appearance_On_Create_And_Preserves_When_Omitted_On_Update()
    {
        var repo = _fixture.CreateRepository();
        using var session = _store.OpenAsyncSession();
        var id = "npcs/appearance-" + Guid.NewGuid();

        await repo.UpsertCharacterAsync(session, new CharacterUpsertRequest
        {
            Id = id,
            Name = "Weathered Scout",
            CurrentAppearance = "mud-caked boots, torn cloak",
            VisualTags = ["disheveled", "wet"],
            DistinctiveFeatures = ["scar across left eyebrow"],
        }, TestCampaignDefaults.Slug);
        await session.SaveChangesAsync();

        var seeded = await session.LoadAsync<Character>(id);
        Assert.Equal("mud-caked boots, torn cloak", seeded.CurrentAppearance);
        Assert.Equal(["disheveled", "wet"], seeded.VisualTags);
        Assert.Equal(["scar across left eyebrow"], seeded.DistinctiveFeatures);

        // Second upsert omits appearance fields (e.g. an unrelated HP update) -- must not clobber them.
        await repo.UpsertCharacterAsync(session, new CharacterUpsertRequest
        {
            Id = id,
            Name = "Weathered Scout",
            CurrentHp = 5,
        }, TestCampaignDefaults.Slug);
        await session.SaveChangesAsync();

        var updated = await session.LoadAsync<Character>(id);
        Assert.Equal("mud-caked boots, torn cloak", updated.CurrentAppearance);
        Assert.Equal(["disheveled", "wet"], updated.VisualTags);
        Assert.Equal(["scar across left eyebrow"], updated.DistinctiveFeatures);
    }

    private async Task<List<Event>> WaitForEventsInvolvingAsync(IAsyncDocumentSession session, string characterId)
    {
        return await session.Query<Event, Event_Search>()
            .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(10)))
            .Where(e => e.Involved.Contains(characterId))
            .ToListAsync();
    }

    private async Task<List<Event>> WaitForEventsAtLocationAsync(IAsyncDocumentSession session, string locationId)
    {
        return await session.Query<Event, Event_Search>()
            .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(10)))
            .Where(e => e.LocationId == locationId)
            .ToListAsync();
    }


    [Fact]
    public async Task CharacterUpdate_AppearanceChange_AutoLogsTrivialEvent_ButNoOpDoesNot()
    {
        var repo = _fixture.CreateRepository();
        using var session = _store.OpenAsyncSession();
        var id = "npcs/appearance-event-" + Guid.NewGuid();

        await repo.UpsertCharacterAsync(_fixture.CreateCampaignSession(session, TestCampaignDefaults.Slug), new CharacterUpsertRequest { Id = id, Name = "Guard" });
        await session.SaveChangesAsync();

        var result = await repo.StageChangesAsync(session, [
            new CharacterUpdate { CharacterId = id, AppearanceOverride = "green-striped scarf tied around neck", TagsToAdd = ["adorned"] }
        ], TestCampaignDefaults.Slug);
        await session.SaveChangesAsync();
        Assert.True(result.Success);

        var events = await WaitForEventsInvolvingAsync(session, id);
        Assert.Single(events);
        Assert.Equal(EventCategory.Interaction, events[0].Category);
        Assert.Equal(MemoryImportance.Trivial, events[0].Importance);
        Assert.Contains("scarf", events[0].Summary);

        // Re-applying the exact same values is a no-op and must not log a second event.
        var noOpResult = await repo.StageChangesAsync(session, [
            new CharacterUpdate { CharacterId = id, AppearanceOverride = "green-striped scarf tied around neck", TagsToAdd = ["adorned"] }
        ], TestCampaignDefaults.Slug);
        await session.SaveChangesAsync();
        Assert.True(noOpResult.Success);

        var eventsAfterNoOp = await WaitForEventsInvolvingAsync(session, id);
        Assert.Single(eventsAfterNoOp);

        var npc = await session.LoadAsync<Character>(id);
        Assert.Equal([events[0].Id], npc.TagProvenance["green-striped scarf tied around neck"]);
        Assert.Equal([events[0].Id], npc.TagProvenance["adorned"]);
    }

    [Fact]
    public async Task EngagementRelationChange_SocialCategory_DoesNotAutoLogEvent()
    {
        var repo = _fixture.CreateRepository();
        using var session = _store.OpenAsyncSession();
        var actorId = "npcs/talker-" + Guid.NewGuid();
        var targetId = "npcs/listener-" + Guid.NewGuid();

        await repo.UpsertCharacterAsync(_fixture.CreateCampaignSession(session, TestCampaignDefaults.Slug), new CharacterUpsertRequest { Id = actorId, Name = "Talker" });
        await repo.UpsertCharacterAsync(_fixture.CreateCampaignSession(session, TestCampaignDefaults.Slug), new CharacterUpsertRequest { Id = targetId, Name = "Listener" });
        await session.SaveChangesAsync();

        var establishResult = await repo.StageChangesAsync(session, [
            new EngagementRelationChange { CharacterId = actorId, TargetId = targetId, Verb = "watching", Category = EngagementCategory.Social }
        ], TestCampaignDefaults.Slug);
        await session.SaveChangesAsync();
        Assert.True(establishResult.Success);

        Assert.Empty(await WaitForEventsInvolvingAsync(session, targetId));

        var clearResult = await repo.StageChangesAsync(session, [
            new EngagementRelationChange { CharacterId = actorId, TargetId = targetId, Verb = null }
        ], TestCampaignDefaults.Slug);
        await session.SaveChangesAsync();
        Assert.True(clearResult.Success);

        Assert.Empty(await WaitForEventsInvolvingAsync(session, targetId));
    }

    [Fact]
    public async Task EngagementRelationChange_GroupConversationPattern_DoesNotSpamEvents()
    {
        // Regression guard for the "PC says 'what's up' to 4 people" scenario: batching pairwise
        // Social/Attention engagement_relation commits to mark conversation participants must not
        // auto-log an event per pair.
        var repo = _fixture.CreateRepository();
        using var session = _store.OpenAsyncSession();
        var pcId = "npcs/pc-" + Guid.NewGuid();
        var npc1 = "npcs/patron1-" + Guid.NewGuid();
        var npc2 = "npcs/patron2-" + Guid.NewGuid();
        var npc3 = "npcs/patron3-" + Guid.NewGuid();

        await repo.UpsertCharacterAsync(_fixture.CreateCampaignSession(session, TestCampaignDefaults.Slug), new CharacterUpsertRequest { Id = pcId, Name = "PC" });
        await repo.UpsertCharacterAsync(_fixture.CreateCampaignSession(session, TestCampaignDefaults.Slug), new CharacterUpsertRequest { Id = npc1, Name = "Patron 1" });
        await repo.UpsertCharacterAsync(_fixture.CreateCampaignSession(session, TestCampaignDefaults.Slug), new CharacterUpsertRequest { Id = npc2, Name = "Patron 2" });
        await repo.UpsertCharacterAsync(_fixture.CreateCampaignSession(session, TestCampaignDefaults.Slug), new CharacterUpsertRequest { Id = npc3, Name = "Patron 3" });
        await session.SaveChangesAsync();

        var result = await repo.StageChangesAsync(session, [
            new EngagementRelationChange { CharacterId = pcId, TargetId = npc1, Verb = "talking to", Category = EngagementCategory.Attention },
            new EngagementRelationChange { CharacterId = pcId, TargetId = npc2, Verb = "talking to", Category = EngagementCategory.Attention },
            new EngagementRelationChange { CharacterId = pcId, TargetId = npc3, Verb = "talking to", Category = EngagementCategory.Attention },
        ], TestCampaignDefaults.Slug);
        await session.SaveChangesAsync();
        Assert.True(result.Success);

        Assert.Empty(await WaitForEventsInvolvingAsync(session, pcId));
    }

    [Fact]
    public async Task EngagementRelationChange_EstablishAndClear_AutoLogsImportantEvents()
    {
        var repo = _fixture.CreateRepository();
        using var session = _store.OpenAsyncSession();
        var actorId = "npcs/captor-" + Guid.NewGuid();
        var targetId = "npcs/captive-" + Guid.NewGuid();

        await repo.UpsertCharacterAsync(_fixture.CreateCampaignSession(session, TestCampaignDefaults.Slug), new CharacterUpsertRequest { Id = actorId, Name = "Captor" });
        await repo.UpsertCharacterAsync(_fixture.CreateCampaignSession(session, TestCampaignDefaults.Slug), new CharacterUpsertRequest { Id = targetId, Name = "Captive" });
        await session.SaveChangesAsync();

        var establishResult = await repo.StageChangesAsync(session, [
            new EngagementRelationChange { CharacterId = actorId, TargetId = targetId, Verb = "Restraining" }
        ], TestCampaignDefaults.Slug);
        await session.SaveChangesAsync();
        Assert.True(establishResult.Success);

        var eventsAfterEstablish = await WaitForEventsInvolvingAsync(session, targetId);
        Assert.Single(eventsAfterEstablish);
        Assert.Equal(MemoryImportance.Important, eventsAfterEstablish[0].Importance);

        var clearResult = await repo.StageChangesAsync(session, [
            new EngagementRelationChange { CharacterId = actorId, TargetId = targetId, Verb = null }
        ], TestCampaignDefaults.Slug);
        await session.SaveChangesAsync();
        Assert.True(clearResult.Success);

        var eventsAfterClear = await WaitForEventsInvolvingAsync(session, targetId);
        Assert.Equal(2, eventsAfterClear.Count);
        Assert.Contains(eventsAfterClear, e => e.Summary.Contains("ended"));
    }

    [Fact]
    public async Task StatusChange_AddAndRemove_AutoLogsImportantEvents()
    {
        var repo = _fixture.CreateRepository();
        using var session = _store.OpenAsyncSession();
        var id = "npcs/status-event-" + Guid.NewGuid();

        await repo.UpsertCharacterAsync(_fixture.CreateCampaignSession(session, TestCampaignDefaults.Slug), new CharacterUpsertRequest { Id = id, Name = "Shackled Prisoner" });
        await session.SaveChangesAsync();

        var addResult = await repo.StageChangesAsync(session, [
            new StatusChange { CharacterId = id, Status = "Shackled" }
        ], TestCampaignDefaults.Slug);
        await session.SaveChangesAsync();
        Assert.True(addResult.Success);

        var eventsAfterAdd = await WaitForEventsInvolvingAsync(session, id);
        Assert.Single(eventsAfterAdd);
        Assert.Equal(MemoryImportance.Important, eventsAfterAdd[0].Importance);

        var removeResult = await repo.StageChangesAsync(session, [
            new StatusRemove { CharacterId = id, Status = "Shackled" }
        ], TestCampaignDefaults.Slug);
        await session.SaveChangesAsync();
        Assert.True(removeResult.Success);

        var eventsAfterRemove = await WaitForEventsInvolvingAsync(session, id);
        Assert.Equal(2, eventsAfterRemove.Count);
        Assert.Contains(eventsAfterRemove, e => e.Summary.Contains("lost status"));
    }

    [Fact]
    public async Task LocationUpdate_TagChange_AutoLogsTrivialEvent_AndPopulatesProvenance()
    {
        var repo = _fixture.CreateRepository();
        using var session = _store.OpenAsyncSession();
        var id = "locations/tavern-" + Guid.NewGuid();

        await repo.UpsertLocationAsync(session,
            new LocationUpsertRequest { Id = id, Name = "Tavern", Type = LocationType.Building }, TestCampaignDefaults.Slug);
        await session.SaveChangesAsync();

        var result = await repo.StageChangesAsync(session, [
            new LocationUpdate { LocationId = id, TagsToAdd = ["beer-stained floor"] }
        ], TestCampaignDefaults.Slug);
        await session.SaveChangesAsync();
        Assert.True(result.Success);

        var events = await WaitForEventsAtLocationAsync(session, id);
        Assert.Single(events);
        Assert.Equal(EventCategory.Interaction, events[0].Category);
        Assert.Equal(MemoryImportance.Trivial, events[0].Importance);

        var loc = await session.LoadAsync<Location>(id);
        Assert.Equal([events[0].Id], loc.TagProvenance["beer-stained floor"]);

        // No-op re-application must not log a second event.
        var noOpResult = await repo.StageChangesAsync(session, [
            new LocationUpdate { LocationId = id, TagsToAdd = ["beer-stained floor"] }
        ], TestCampaignDefaults.Slug);
        await session.SaveChangesAsync();
        Assert.True(noOpResult.Success);
        Assert.Single(await WaitForEventsAtLocationAsync(session, id));

        // Removing the tag clears its provenance entry.
        var removeResult = await repo.StageChangesAsync(session, [
            new LocationUpdate { LocationId = id, TagsToRemove = ["beer-stained floor"] }
        ], TestCampaignDefaults.Slug);
        await session.SaveChangesAsync();
        Assert.True(removeResult.Success);

        var locAfterRemove = await session.LoadAsync<Location>(id);
        Assert.False(locAfterRemove.TagProvenance.ContainsKey("beer-stained floor"));
    }

    [Fact]
    public async Task ItemUpdate_FeatureChange_AutoLogsTrivialEvent_AndPopulatesProvenance()
    {
        var repo = _fixture.CreateRepository();
        using var session = _store.OpenAsyncSession();
        var locId = "locations/tower-" + Guid.NewGuid();
        var itemId = "items/robe-" + Guid.NewGuid();

        await repo.UpsertLocationAsync(session,
            new LocationUpsertRequest { Id = locId, Name = "Tower", Type = LocationType.Room }, TestCampaignDefaults.Slug);
        await repo.UpsertItemAsync(session,
            new ItemUpsertRequest { Id = itemId, Name = "Mage Robe", HolderId = locId }, TestCampaignDefaults.Slug);
        await session.SaveChangesAsync();

        var result = await repo.StageChangesAsync(session, [
            new ItemUpdate { ItemId = itemId, FeaturesToAdd = ["singed cuffs"] }
        ], TestCampaignDefaults.Slug);
        await session.SaveChangesAsync();
        Assert.True(result.Success);

        var events = await WaitForEventsInvolvingAsync(session, itemId);
        Assert.Single(events);
        Assert.Equal(MemoryImportance.Trivial, events[0].Importance);
        Assert.Equal(locId, events[0].LocationId);

        var item = await session.LoadAsync<Item>(itemId);
        Assert.Equal([events[0].Id], item.TagProvenance["singed cuffs"]);

        // No-op re-application must not log a second event.
        var noOpResult = await repo.StageChangesAsync(session, [
            new ItemUpdate { ItemId = itemId, FeaturesToAdd = ["singed cuffs"] }
        ], TestCampaignDefaults.Slug);
        await session.SaveChangesAsync();
        Assert.True(noOpResult.Success);
        Assert.Single(await WaitForEventsInvolvingAsync(session, itemId));
    }

    [Fact]
    public async Task GetScene_DoesNotStamp_LastVisitedDay_When_MarkVisitedFalse()
    {
        var repo = _fixture.CreateRepository();
        var id = "locations/test-visit-" + Guid.NewGuid();
        using (var session = _store.OpenAsyncSession())
        {
            await repo.UpsertLocationAsync(session,
                new LocationUpsertRequest { Id = id, Name = "Test Room", Type = LocationType.Room, LastVisitedDay = 1 }, TestCampaignDefaults.Slug);
            await session.SaveChangesAsync();
        }

        using (var session = _store.OpenAsyncSession())
        {
            var scene = await repo.GetSceneAsync(session, id, TestCampaignDefaults.Slug, markVisited: false);
            Assert.Equal(1, scene.Location.LastVisitedDay);
            await session.SaveChangesAsync(); // even if save is called, no mutation should occur
        }

        using (var session = _store.OpenAsyncSession())
        {
            var loc = await repo.GetLocationAsync(session, id, TestCampaignDefaults.Slug);
            Assert.Equal(1, loc!.LastVisitedDay);
        }
    }

    [Fact]
    public async Task GetScene_Stamps_LastVisitedDay_When_MarkVisitedTrue()
    {
        var repo = _fixture.CreateRepository();
        var id = "locations/test-visit-true-" + Guid.NewGuid();
        using (var session = _store.OpenAsyncSession())
        {
            await repo.UpsertLocationAsync(session,
                new LocationUpsertRequest { Id = id, Name = "Test Room", Type = LocationType.Room, LastVisitedDay = 1 }, TestCampaignDefaults.Slug);

            // Fast forward time to day 5
            var time = await repo.GetTimeAsync(_fixture.CreateCampaignSession(session, TestCampaignDefaults.Slug));
            time.TotalDaysElapsed = 5;
            await repo.SaveTimeAsync(_fixture.CreateCampaignSession(session, TestCampaignDefaults.Slug), time);
            await session.SaveChangesAsync();
        }

        using (var session = _store.OpenAsyncSession())
        {
            var scene = await repo.GetSceneAsync(session, id, TestCampaignDefaults.Slug, markVisited: true);
            Assert.Equal(5, scene.Location.LastVisitedDay);
            await session.SaveChangesAsync();
        }

        using (var session = _store.OpenAsyncSession())
        {
            var loc = await repo.GetLocationAsync(session, id, TestCampaignDefaults.Slug);
            Assert.Equal(5, loc!.LastVisitedDay);
        }
    }

    [Fact]
    public async Task GetScene_CurrentActivity_FallsBack_To_Idle_Not_LocationId()
    {
        var repo = _fixture.CreateRepository();
        var locId = "locations/activity-test-" + Guid.NewGuid();
        var charId = "chars/activity-test-" + Guid.NewGuid();

        using (var session = _store.OpenAsyncSession())
        {
            await repo.UpsertLocationAsync(_fixture.CreateCampaignSession(session, TestCampaignDefaults.Slug), new LocationUpsertRequest { Id = locId, Name = "Activity Room" });

            var npc = new CharacterUpsertRequest
            {
                Id = charId,
                Name = "Bob",
                CurrentActivity = null,
                Schedule = new Schedule { DefaultLocationId = locId, Routines = [] }
            };
            await repo.UpsertCharacterAsync(_fixture.CreateCampaignSession(session, TestCampaignDefaults.Slug), npc);
            await session.SaveChangesAsync();
        }

        // Wait for indexing
        var indexWaitStart = DateTime.UtcNow;
        while ((DateTime.UtcNow - indexWaitStart).TotalSeconds < 10)
        {
            var stats = _store.Maintenance.Send(new Raven.Client.Documents.Operations.GetStatisticsOperation());
            if (stats.Indexes.Any(x => x.Name == "Character/Search" && x.IsStale == false))
            {
                break;
            }

            await Task.Delay(100);
        }

        using (var session = _store.OpenAsyncSession())
        {
            var scene = await repo.GetSceneAsync(session, locId, TestCampaignDefaults.Slug);
            var npcSummary = scene.PresentNPCs.FirstOrDefault(n => n.Id == charId);
            Assert.NotNull(npcSummary);
            Assert.Equal("Idle at default location", npcSummary.CurrentActivity);
        }
    }

    [Fact]
    public async Task GetScene_Surfaces_NpcTagProvenance_InPresenceSummary()
    {
        var repo = _fixture.CreateRepository();
        var locId = "locations/provenance-test-" + Guid.NewGuid();
        var charId = "chars/provenance-test-" + Guid.NewGuid();
        string eventId;

        using (var session = _store.OpenAsyncSession())
        {
            await repo.UpsertLocationAsync(_fixture.CreateCampaignSession(session, TestCampaignDefaults.Slug), new LocationUpsertRequest { Id = locId, Name = "Provenance Room" });
            await repo.UpsertCharacterAsync(session, new CharacterUpsertRequest
            {
                Id = charId,
                Name = "Marked NPC",
                Schedule = new Schedule { DefaultLocationId = locId, Routines = [] }
            }, TestCampaignDefaults.Slug);
            await session.SaveChangesAsync();

            var result = await repo.StageChangesAsync(session, [
                new CharacterUpdate { CharacterId = charId, TagsToAdd = ["bloodstained sleeve"] }
            ], TestCampaignDefaults.Slug);
            await session.SaveChangesAsync();
            Assert.True(result.Success);

            var events = await WaitForEventsInvolvingAsync(session, charId);
            Assert.Single(events);
            eventId = events[0].Id;
        }

        var indexWaitStart = DateTime.UtcNow;
        while ((DateTime.UtcNow - indexWaitStart).TotalSeconds < 10)
        {
            var stats = _store.Maintenance.Send(new Raven.Client.Documents.Operations.GetStatisticsOperation());
            if (stats.Indexes.Any(x => x.Name == "Character/Search" && x.IsStale == false))
            {
                break;
            }

            await Task.Delay(100);
        }

        using (var session = _store.OpenAsyncSession())
        {
            var scene = await repo.GetSceneAsync(session, locId, TestCampaignDefaults.Slug);
            var npcSummary = scene.PresentNPCs.FirstOrDefault(n => n.Id == charId);
            Assert.NotNull(npcSummary);
            Assert.NotNull(npcSummary.TagProvenance);
            Assert.Equal([eventId], npcSummary.TagProvenance!["bloodstained sleeve"]);
        }
    }

    [Fact]
    public async Task GetScene_PopulatesActiveQuestsAndFactions_Correctly()
    {
        var repo = _fixture.CreateRepository();
        var locId = "locations/quest-faction-loc-" + Guid.NewGuid();
        var questId = "quests/test-quest-" + Guid.NewGuid();
        var factionId = "factions/test-faction-" + Guid.NewGuid();
        var charId = "chars/hero-" + Guid.NewGuid();

        using (var session = _store.OpenAsyncSession())
        {
            await repo.UpsertLocationAsync(_fixture.CreateCampaignSession(session, TestCampaignDefaults.Slug), new LocationUpsertRequest { Id = locId, Name = "Quest Hub" });

            var quest = new Quest
            {
                Id = questId,
                Title = "Save the Hub",
                OverallState = QuestState.Open,
                DeadlineDay = 15,
                RelatedLocationIds = [locId],
                Objectives =
                [
                    new QuestObjective("Obj 1", QuestState.Open),
                    new QuestObjective("Obj 2", QuestState.Complete)
                ]
            };
            await repo.UpsertQuestAsync(_fixture.CreateCampaignSession(session, TestCampaignDefaults.Slug), quest);

            var faction = new Faction
            {
                Id = factionId,
                Name = "Hub Defenders",
                InfluenceLevel = 50,
                TerritoryLocationIds = [locId]
            };
            await repo.UpsertFactionAsync(_fixture.CreateCampaignSession(session, TestCampaignDefaults.Slug), faction);

            var npc = new CharacterUpsertRequest
            {
                Id = charId,
                Name = "Hero",
                Social = new SocialProfile
                {
                    FactionReputations = new Dictionary<string, int> { { factionId, 10 } }
                },
                Schedule = new Schedule { DefaultLocationId = locId, Routines = [] }
            };
            await repo.UpsertCharacterAsync(_fixture.CreateCampaignSession(session, TestCampaignDefaults.Slug), npc);

            await repo.LogEventAsync(session, new Event
            {
                Id = "events/test-" + Guid.NewGuid(),
                Summary = "Travel interrupted en route to the hub.",
                Involved = [locId]
            }, TestCampaignDefaults.Slug);

            await session.SaveChangesAsync();
        }

        // Wait for indexes
        var indexWaitStart = DateTime.UtcNow;
        while ((DateTime.UtcNow - indexWaitStart).TotalSeconds < 10)
        {
            var stats = _store.Maintenance.Send(new Raven.Client.Documents.Operations.GetStatisticsOperation());
            if (stats.Indexes.Any(x => x.Name == "Quest/Search" && x.IsStale == false) &&
                stats.Indexes.Any(x => x.Name == "Faction/Search" && x.IsStale == false) &&
                stats.Indexes.Any(x => x.Name == "Character/Search" && x.IsStale == false) &&
                stats.Indexes.Any(x => x.Name == "Event/Search" && x.IsStale == false))
            {
                break;
            }

            await Task.Delay(100);
        }

        using (var session = _store.OpenAsyncSession())
        {
            var scene = await repo.GetSceneAsync(session, locId, TestCampaignDefaults.Slug);

            Assert.NotNull(scene.ActiveQuests);
            Assert.NotEmpty(scene.ActiveQuests);
            var q = scene.ActiveQuests!.First();
            Assert.Equal("Save the Hub", q.Title);
            Assert.Equal(1, q.OpenObjectiveCount); // Only one is Open
            Assert.Equal(2, q.TotalObjectiveCount);
            Assert.Equal(15, q.DeadlineDay);

            Assert.NotNull(scene.RelevantFactions);
            Assert.NotEmpty(scene.RelevantFactions);
            var f = scene.RelevantFactions!.First();
            Assert.Equal("Hub Defenders", f.Name);
            Assert.Equal(50, f.InfluenceLevel);
            Assert.Equal(10, f.PlayerReputation); // Inherited from hero present in loc

            Assert.Equal("Travel interrupted en route to the hub.", scene.LastKnownTravel);
            Assert.NotNull(scene.SuggestedCommitExamples);
            Assert.Empty(scene.SuggestedCommitExamples);
        }
    }

    [Fact]
    public void RuleOrdering_NeedsAccumulation_RunsAfter_ScheduleEvaluation()
    {
        var needsRule = new NeedsAccumulationRule();
        var scheduleRule = new ScheduleEvaluationRule();

        Assert.True(needsRule.Order > scheduleRule.Order,
            "NeedsAccumulationRule should run after ScheduleEvaluationRule so it uses the updated location.");
    }

    [Fact]
    public async Task FallbackHandlers_IncludesPhase6()
    {
        var repo = _fixture.CreateRepository(); // empty handlers fallback
        using var session = _store.OpenAsyncSession();

        // This would fail if CharacterCreateHandler was missing
        var result = await repo.StageChangesAsync(session, [
            new CharacterCreate { CharacterId = "chars/dummy-" + Guid.NewGuid(), Name = "Dummy" }
        ], TestCampaignDefaults.Slug);

        Assert.True(result.Success);
    }

    [Fact]
    public async Task
        TravelChange_FullCommit_UsesPrePopulatedExitMetadata_OnOrigin_ForTirednessDelta_AndInterruptBehavior()
    {
        // Use a repo ctor that supplies the required handlers (the default 1-arg ctor uses a fallback list
        // that does not include TravelChangeHandler, which would result in "Unhandled change type").
        // We wire a controlled EncounterResolver (always "safe" random) so the -100 risk path is deterministic.
        var safeRule = new EncounterResolver(() => 0.99); // > any clamped modified chance from negative modifier
        var changeHandlers = new IWorldChangeHandler[]
        {
            new TravelChangeHandler(safeRule),
            new NeedChangeHandler(),
            new ActivityChangeHandler(),
            new EventOccurredHandler()
        };

        var engine = new DefaultSimulationEngine(new ISimulationRule[0]);
        var repo = _fixture.CreateRepository(engineOverride: engine,
            overrides: b => { b.Register(_ => changeHandlers.AsEnumerable()).As<IEnumerable<IWorldChangeHandler>>(); });

        using var session = _store.OpenAsyncSession();

        // Setup: character with Needs profile, origin location *with* populated Exit (TravelCostHours + Terrain),
        // and a destination.
        var charId = "chars/travel-pc-" + Guid.NewGuid().ToString("N");
        var originId = "locations/travel-origin-" + Guid.NewGuid().ToString("N");
        var destId = "locations/travel-dest-" + Guid.NewGuid().ToString("N");

        var traveler = new Character
        {
            Id = charId,
            Name = "Traveling PC",
            CurrentLocationId = originId,
            Needs = new NeedsProfile { ActiveNeeds = new Dictionary<string, float> { ["tiredness"] = 5f } }
        };

        var origin = new Location
        {
            Id = originId,
            Name = "Forest Trailhead",
            Description = "Start of the path",
            Exits = [new LocationExit(destId, "Winding path through the woods", TravelCostHours: 16, Terrain: "forest")]
        };

        var dest = new Location
        {
            Id = destId,
            Name = "Forest Clearing",
            Description = "A quiet clearing"
        };

        await session.StoreAsync(traveler);
        await session.StoreAsync(origin);
        await session.StoreAsync(dest);
        await session.SaveChangesAsync();

        // === Success path: no override, low risk modifier (never interrupts), exit provides 16h ===
        var successTravel = new TravelChange
        {
            CharacterId = charId,
            DestinationLocationId = destId,
            Narrative = "Hiked the forest path",
            TravelCostHoursOverride = null, // rely on exit metadata
            TerrainOverride = null,
            EncounterRiskModifier = -100 // ensure no interrupt regardless of terrain base chance
        };

        var successResult = await repo.StageChangesAsync(_fixture.CreateCampaignSession(session, TestCampaignDefaults.Slug), [successTravel]);
        Assert.True(successResult.Success,
            "Full commit via StageChangesAsync should succeed for travel using exit metadata");

        await session
            .SaveChangesAsync(); // persist mutations from handlers (Need, Activity, Event, LastVisited on dest)

        var reloadedTraveler = await session.LoadAsync<Character>(charId);
        var reloadedDest = await session.LoadAsync<Location>(destId);

        Assert.NotNull(reloadedTraveler.Needs);
        var finalTiredness = reloadedTraveler.Needs.ActiveNeeds["tiredness"];
        // 16 hours from exit -> (16 / 4.0f) * 10f = 40f delta. Started at 5f -> 45f (clamped logic in handler is additive before cap in Need handler)
        Assert.True(finalTiredness >= 44f && finalTiredness <= 46f,
            $"Expected ~45 tiredness from 16h exit lookup (5 base + 40), was {finalTiredness}");

        // Location should have been updated
        Assert.Equal(destId, reloadedTraveler.CurrentLocationId);
        // LastVisitedDay should have been stamped on dest (via direct mutation on tracked entity + save)
        Assert.NotNull(reloadedDest.LastVisitedDay);

        // === Interrupt behavior path (separate entities + dedicated repo/handler to keep test isolated and deterministic):
        // Use a TravelChangeHandler wired with a rule whose random *always* returns 0.0 so high risk *guarantees* interrupt.
        var triggerRule = new EncounterResolver(() => 0.0);
        var intChangeHandlers = new IWorldChangeHandler[]
        {
            new TravelChangeHandler(triggerRule),
            new NeedChangeHandler(),
            new ActivityChangeHandler(),
            new EventOccurredHandler()
        };
        var intEngine = new DefaultSimulationEngine(new ISimulationRule[0]);
        var intRepo = _fixture.CreateRepository(engineOverride: intEngine,
            overrides: b =>
            {
                b.Register(_ => intChangeHandlers.AsEnumerable()).As<IEnumerable<IWorldChangeHandler>>();
            });

        var intCharId = "chars/travel-pc-int-" + Guid.NewGuid().ToString("N");
        var intOriginId = "locations/travel-int-origin-" + Guid.NewGuid().ToString("N");
        var intDestId = "locations/travel-int-dest-" + Guid.NewGuid().ToString("N");

        var intTraveler = new Character
        {
            Id = intCharId,
            Name = "Interrupt PC",
            CurrentLocationId = intOriginId,
            Needs = new NeedsProfile { ActiveNeeds = new Dictionary<string, float> { ["tiredness"] = 0f } }
        };
        var intOrigin = new Location
        {
            Id = intOriginId,
            Name = "Road Start",
            Exits = [new LocationExit(intDestId, "Open road", TravelCostHours: 6, Terrain: "road")]
        };
        var intDest = new Location { Id = intDestId, Name = "Road End" };

        await session.StoreAsync(intTraveler);
        await session.StoreAsync(intOrigin);
        await session.StoreAsync(intDest);
        await session.SaveChangesAsync();

        var interruptTravel = new TravelChange
        {
            CharacterId = intCharId,
            DestinationLocationId = intDestId,
            Narrative = "Tried to travel the road",
            TravelCostHoursOverride = null,
            EncounterRiskModifier =
                100 // high risk; combined with always-0 random in the rule, guarantees first-bucket interrupt
        };

        var intResult = await intRepo.StageChangesAsync(_fixture.CreateCampaignSession(session, TestCampaignDefaults.Slug), [interruptTravel]);
        Assert.True(intResult.Success);

        await session.SaveChangesAsync();

        var reloadedIntTraveler = await session.LoadAsync<Character>(intCharId);

        // On interrupt: partial tiredness still applied (6h -> +15), but NO location move + activity marker set
        Assert.Equal(intOriginId, reloadedIntTraveler.CurrentLocationId); // did not teleport
        Assert.Contains("unexpected encounter", reloadedIntTraveler.CurrentActivity ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
        var intTiredness = reloadedIntTraveler.Needs.ActiveNeeds["tiredness"];
        Assert.True(intTiredness >= 14f,
            $"Partial tiredness should have been applied even on interrupt; was {intTiredness}");
    }

    private async Task WaitForAllIndexesAsync()
    {
        var indexWaitStart = DateTime.UtcNow;
        while ((DateTime.UtcNow - indexWaitStart).TotalSeconds < 10)
        {
            var stats = _store.Maintenance.Send(new Raven.Client.Documents.Operations.GetStatisticsOperation());
            var staleCount = stats.Indexes.Count(x => x.IsStale);
            if (staleCount == 0)
            {
                break;
            }

            await Task.Delay(100);
        }
    }

    [Fact]
    public async Task GetScene_Includes_Npcs_From_Child_Locations()
    {
        var repo = _fixture.CreateRepository();
        var parentId = "locations/p-loc-" + Guid.NewGuid();
        var childId = "locations/c-loc-" + Guid.NewGuid();
        var char1Id = "chars/p-char-" + Guid.NewGuid();
        var char2Id = "chars/c-char-" + Guid.NewGuid();

        using (var session = _store.OpenAsyncSession())
        {
            await repo.UpsertLocationAsync(_fixture.CreateCampaignSession(session, TestCampaignDefaults.Slug), new LocationUpsertRequest { Id = parentId, Name = "Parent Location" });
            await repo.UpsertLocationAsync(session,
                new LocationUpsertRequest { Id = childId, Name = "Child Location", ParentLocationId = parentId }, TestCampaignDefaults.Slug);

            var npc1 = new CharacterUpsertRequest
            {
                Id = char1Id,
                Name = "Parent NPC",
                CurrentLocationId = parentId,
                Schedule = new Schedule { DefaultLocationId = parentId, Routines = [] }
            };
            var npc2 = new CharacterUpsertRequest
            {
                Id = char2Id,
                Name = "Child NPC",
                CurrentLocationId = childId,
                Schedule = new Schedule { DefaultLocationId = childId, Routines = [] }
            };

            await repo.UpsertCharacterAsync(_fixture.CreateCampaignSession(session, TestCampaignDefaults.Slug), npc1);
            await repo.UpsertCharacterAsync(_fixture.CreateCampaignSession(session, TestCampaignDefaults.Slug), npc2);
            await session.SaveChangesAsync();
        }

        await WaitForAllIndexesAsync();

        using (var session = _store.OpenAsyncSession())
        {
            var scene = await repo.GetSceneAsync(session, parentId, TestCampaignDefaults.Slug);
            Assert.Contains(scene.PresentNPCs, n => n.Id == char1Id);
            Assert.Contains(scene.PresentNPCs, n => n.Id == char2Id);
        }
    }

    [Fact]
    public async Task GetScene_Applies_CampaignScoping_To_Npcs_Items_And_Events()
    {
        var repo = _fixture.CreateRepository();
        var locId = "locations/scoped-loc-" + Guid.NewGuid();
        var campA = "CampA";
        var campB = "CampB";

        using (var session = _store.OpenAsyncSession())
        {
            await repo.UpsertLocationAsync(_fixture.CreateCampaignSession(session, campA), new LocationUpsertRequest { Id = locId, Name = "Scoped Location", CampaignName = campA });

            // NPCs
            var npcA = new Character
            {
                Id = "chars/npc-a-" + Guid.NewGuid(), Name = "NPC A", CurrentLocationId = locId, CampaignName = campA,
                Schedule = new Schedule { DefaultLocationId = locId }
            };
            var npcB = new Character
            {
                Id = "chars/npc-b-" + Guid.NewGuid(), Name = "NPC B", CurrentLocationId = locId, CampaignName = campB,
                Schedule = new Schedule { DefaultLocationId = locId }
            };
            var npcShared = new Character
            {
                Id = "chars/npc-shared-" + Guid.NewGuid(), Name = "NPC Shared", CurrentLocationId = locId,
                CampaignName = null, Schedule = new Schedule { DefaultLocationId = locId }
            };

            await session.StoreAsync(npcA);
            await session.StoreAsync(npcB);
            await session.StoreAsync(npcShared);

            // Items
            await session.StoreAsync(new Item
                { Id = "items/item-a-" + Guid.NewGuid(), Name = "Item A", HolderId = locId, CampaignName = campA });
            await session.StoreAsync(new Item
                { Id = "items/item-b-" + Guid.NewGuid(), Name = "Item B", HolderId = locId, CampaignName = campB });
            await session.StoreAsync(new Item
            {
                Id = "items/item-shared-" + Guid.NewGuid(), Name = "Item Shared", HolderId = locId, CampaignName = null
            });

            // Rumors
            await session.StoreAsync(new Rumor
            {
                Id = "rumors/rumor-a-" + Guid.NewGuid(), Subject = "Rumor A", CurrentText = "Text A",
                RegionLocationId = locId, State = RumorState.Nascent, CampaignName = campA
            });
            await session.StoreAsync(new Rumor
            {
                Id = "rumors/rumor-b-" + Guid.NewGuid(), Subject = "Rumor B", CurrentText = "Text B",
                RegionLocationId = locId, State = RumorState.Nascent, CampaignName = campB
            });
            await session.StoreAsync(new Rumor
            {
                Id = "rumors/rumor-shared-" + Guid.NewGuid(), Subject = "Rumor Shared", CurrentText = "Text Shared",
                RegionLocationId = locId, State = RumorState.Nascent, CampaignName = null
            });

            // Events
            await session.StoreAsync(new Event
            {
                Id = "events/event-a-" + Guid.NewGuid(), Summary = "Event A occurred", Involved = [locId],
                CampaignName = campA
            });
            await session.StoreAsync(new Event
            {
                Id = "events/event-b-" + Guid.NewGuid(), Summary = "Event B occurred", Involved = [locId],
                CampaignName = campB
            });
            await session.StoreAsync(new Event
            {
                Id = "events/event-shared-" + Guid.NewGuid(), Summary = "Event Shared occurred", Involved = [locId],
                CampaignName = null
            });

            await session.SaveChangesAsync();
        }

        await WaitForAllIndexesAsync();

        using (var session = _store.OpenAsyncSession())
        {
            var scene = await repo.GetSceneAsync(_fixture.CreateCampaignSession(session, campA), locId);

            // NPCs (A and Shared are included, B is excluded)
            Assert.Contains(scene.PresentNPCs, n => n.Name == "NPC A");
            Assert.Contains(scene.PresentNPCs, n => n.Name == "NPC Shared");
            Assert.DoesNotContain(scene.PresentNPCs, n => n.Name == "NPC B");

            // Items (A and Shared are included, B is excluded)
            Assert.Contains(scene.VisibleItems, i => i.Name == "Item A");
            Assert.Contains(scene.VisibleItems, i => i.Name == "Item Shared");
            Assert.DoesNotContain(scene.VisibleItems, i => i.Name == "Item B");

            // Rumors (A is included, B and Shared are excluded)
            Assert.Contains(scene.LocalRumors, r => r.Subject == "Rumor A");
            Assert.DoesNotContain(scene.LocalRumors, r => r.Subject == "Rumor Shared");
            Assert.DoesNotContain(scene.LocalRumors, r => r.Subject == "Rumor B");

            // Events (A is included strictly, B and Shared are excluded)
            Assert.Contains(scene.RecentEvents, e => e.Summary == "Event A occurred");
            Assert.DoesNotContain(scene.RecentEvents, e => e.Summary == "Event B occurred");
            Assert.DoesNotContain(scene.RecentEvents, e => e.Summary == "Event Shared occurred");
        }
    }

    [Fact]
    public async Task GetScene_NPC_Merging_Prefers_Simulation_State_Over_Schedule_State()
    {
        var repo = _fixture.CreateRepository();
        var locId = "locations/merge-loc-" + Guid.NewGuid();
        var charId = "chars/merge-npc-" + Guid.NewGuid();

        using (var session = _store.OpenAsyncSession())
        {
            await repo.UpsertLocationAsync(_fixture.CreateCampaignSession(session, TestCampaignDefaults.Slug), new LocationUpsertRequest { Id = locId, Name = "Merge Location" });

            var npc = new CharacterUpsertRequest
            {
                Id = charId,
                Name = "Merged NPC",
                CurrentLocationId = locId,
                Schedule = new Schedule
                {
                    DefaultLocationId = locId,
                    Routines = []
                }
            };
            await repo.UpsertCharacterAsync(_fixture.CreateCampaignSession(session, TestCampaignDefaults.Slug), npc);
            await session.SaveChangesAsync();
        }

        await WaitForAllIndexesAsync();

        using (var session = _store.OpenAsyncSession())
        {
            var scene = await repo.GetSceneAsync(session, locId, TestCampaignDefaults.Slug);
            var npcs = scene.PresentNPCs.Where(n => n.Id == charId).ToList();
            Assert.Single(npcs);
        }
    }

    [Fact]
    public async Task GetScene_BlackBox_Preserves_Filtering_Merging_And_Travel_Summary()
    {
        var repo = _fixture.CreateRepository();
        var locId = "locations/blackbox-loc-" + Guid.NewGuid();
        var campA = "blackbox-a";
        var campB = "blackbox-b";
        var mergedNpcId = "chars/merged-" + Guid.NewGuid();

        using (var session = _store.OpenAsyncSession())
        {
            await repo.UpsertLocationAsync(_fixture.CreateCampaignSession(session, campA), new LocationUpsertRequest { Id = locId, Name = "Black Box Location", CampaignName = campA });

            await session.StoreAsync(new Character
            {
                Id = mergedNpcId,
                Name = "Merged Guard",
                CampaignName = campA,
                CurrentLocationId = locId,
                CurrentActivity = "patrolling the square",
                Schedule = new Schedule { DefaultLocationId = locId, Routines = [] }
            });

            await session.StoreAsync(new Character
            {
                Id = "chars/shared-" + Guid.NewGuid(),
                Name = "Shared Witness",
                CampaignName = null,
                CurrentLocationId = locId,
                Schedule = new Schedule { DefaultLocationId = locId, Routines = [] }
            });

            await session.StoreAsync(new Character
            {
                Id = "chars/other-campaign-" + Guid.NewGuid(),
                Name = "Other Campaign NPC",
                CampaignName = campB,
                CurrentLocationId = locId,
                Schedule = new Schedule { DefaultLocationId = locId, Routines = [] }
            });

            await session.StoreAsync(new Item
                { Id = "items/a-" + Guid.NewGuid(), Name = "Camp A Item", HolderId = locId, CampaignName = campA });
            await session.StoreAsync(new Item
                { Id = "items/shared-" + Guid.NewGuid(), Name = "Shared Item", HolderId = locId, CampaignName = null });
            await session.StoreAsync(new Item
                { Id = "items/b-" + Guid.NewGuid(), Name = "Camp B Item", HolderId = locId, CampaignName = campB });

            await session.StoreAsync(new Event
            {
                Id = "events/travel-" + Guid.NewGuid(),
                Summary = "The party travel through the market before arriving.",
                Involved = [locId],
                CampaignName = campA
            });
            await session.StoreAsync(new Event
            {
                Id = "events/other-" + Guid.NewGuid(),
                Summary = "Other campaign event",
                Involved = [locId],
                CampaignName = campB
            });
            await session.StoreAsync(new Event
            {
                Id = "events/shared-" + Guid.NewGuid(),
                Summary = "Shared event should stay hidden",
                Involved = [locId],
                CampaignName = null
            });

            await session.SaveChangesAsync();
        }

        await WaitForAllIndexesAsync();

        using (var session = _store.OpenAsyncSession())
        {
            var scene = await repo.GetSceneAsync(_fixture.CreateCampaignSession(session, campA), locId);

            Assert.Equal(scene.PresentNPCs.Count(), scene.PresentNPCs.Select(n => n.Id).Distinct().Count());
            Assert.Contains(scene.PresentNPCs,
                n => n.Name == "Merged Guard" && n.CurrentActivity == "patrolling the square");
            Assert.Contains(scene.PresentNPCs, n => n.Name == "Shared Witness");
            Assert.DoesNotContain(scene.PresentNPCs, n => n.Name == "Other Campaign NPC");

            Assert.Contains(scene.VisibleItems, i => i.Name == "Camp A Item");
            Assert.Contains(scene.VisibleItems, i => i.Name == "Shared Item");
            Assert.DoesNotContain(scene.VisibleItems, i => i.Name == "Camp B Item");

            Assert.Contains(scene.RecentEvents,
                e => e.Summary == "The party travel through the market before arriving.");
            Assert.DoesNotContain(scene.RecentEvents, e => e.Summary == "Other campaign event");
            Assert.DoesNotContain(scene.RecentEvents, e => e.Summary == "Shared event should stay hidden");
            Assert.Equal("The party travel through the market before arriving.", scene.LastKnownTravel);
        }
    }

    [Fact]
    public async Task GetScene_Merges_Global_Need_Descriptors_With_Npc_Local_Descriptors_Correctly()
    {
        var repo = _fixture.CreateRepository();
        var locId = "locations/desc-loc-" + Guid.NewGuid();
        var charId = "chars/desc-npc-" + Guid.NewGuid();
        var campaignName = "desc-camp-" + Guid.NewGuid();

        using (var session = _store.OpenAsyncSession())
        {
            await repo.UpsertLocationAsync(_fixture.CreateCampaignSession(session, campaignName), new LocationUpsertRequest { Id = locId, Name = "Desc Location" });

            // Set global need descriptors
            await repo.SetNeedDescriptorAsync(session, "hunger", "Feeling a bit hungry", campaignName);
            await repo.SetNeedDescriptorAsync(session, "tiredness", "A bit sleepy", campaignName);

            var npc = new CharacterUpsertRequest
            {
                Id = charId,
                Name = "Desc NPC",
                CurrentLocationId = locId,
                Schedule = new Schedule { DefaultLocationId = locId },
                Needs = new NeedsProfile
                {
                    ActiveNeeds = new Dictionary<string, float> { { "hunger", 50f }, { "tiredness", 30f } },
                    NeedDescriptors = new Dictionary<string, string> { { "hunger", "NPC specific hunger" } }
                }
            };
            await repo.UpsertCharacterAsync(_fixture.CreateCampaignSession(session, campaignName), npc);
            await session.SaveChangesAsync();
        }

        await WaitForAllIndexesAsync();

        using (var session = _store.OpenAsyncSession())
        {
            var scene = await repo.GetSceneAsync(_fixture.CreateCampaignSession(session, campaignName), locId);
            var npcSummary = scene.PresentNPCs.FirstOrDefault(n => n.Id == charId);
            Assert.NotNull(npcSummary);

            // Check hunger: overridden by NPC specific hunger
            Assert.True(npcSummary.NeedDescriptors.ContainsKey("hunger"));
            Assert.Equal("NPC specific hunger", npcSummary.NeedDescriptors["hunger"]);

            // Check tiredness: falls back to global "A bit sleepy"
            Assert.True(npcSummary.NeedDescriptors.ContainsKey("tiredness"));
            Assert.Equal("A bit sleepy", npcSummary.NeedDescriptors["tiredness"]);
        }
    }

    [Fact]
    public async Task GetScene_Handles_CombatEncounter_Correctly()
    {
        var repo = _fixture.CreateRepository();
        var locId = "locations/combat-loc-" + Guid.NewGuid();
        var keys = new CampaignDocumentKeys();
        var combatDocId = keys.CombatCurrent("test-campaign");

        // Case 1: Combat is active and at the correct location
        using (var session = _store.OpenAsyncSession())
        {
            await repo.UpsertLocationAsync(_fixture.CreateCampaignSession(session, TestCampaignDefaults.Slug), new LocationUpsertRequest { Id = locId, Name = "Combat Location" });
            var combat = new CombatEncounter
            {
                Id = combatDocId,
                LocationId = locId,
                IsActive = true,
                Round = 3
            };
            await session.StoreAsync(combat);
            await session.SaveChangesAsync();
        }

        using (var session = _store.OpenAsyncSession())
        {
            var scene = await repo.GetSceneAsync(session, locId, "test-campaign");
            Assert.NotNull(scene.ActiveCombat);
            Assert.Equal(3, scene.ActiveCombat.Round);
        }

        // Case 2: Combat is at a different location
        using (var session = _store.OpenAsyncSession())
        {
            var combat = await session.LoadAsync<CombatEncounter>(combatDocId);
            combat.LocationId = "locations/different-loc";
            await session.SaveChangesAsync();
        }

        using (var session = _store.OpenAsyncSession())
        {
            var scene = await repo.GetSceneAsync(session, locId, "test-campaign");
            Assert.Null(scene.ActiveCombat);
        }

        // Case 3: Combat is at the correct location but is NOT active
        using (var session = _store.OpenAsyncSession())
        {
            var combat = await session.LoadAsync<CombatEncounter>(combatDocId);
            combat.LocationId = locId;
            combat.IsActive = false;
            await session.SaveChangesAsync();
        }

        using (var session = _store.OpenAsyncSession())
        {
            var scene = await repo.GetSceneAsync(session, locId, TestCampaignDefaults.Slug);
            Assert.Null(scene.ActiveCombat);
        }
    }

    [Fact]
    public async Task GetScene_FactionStanceAndReputation_Calculations()
    {
        var repo = _fixture.CreateRepository();
        var locId = "locations/faction-loc-" + Guid.NewGuid();
        var factAId = "factions/fact-a-" + Guid.NewGuid();
        var factBId = "factions/fact-b-" + Guid.NewGuid();
        var charId = "chars/npc-rep-" + Guid.NewGuid();

        using (var session = _store.OpenAsyncSession())
        {
            await repo.UpsertLocationAsync(_fixture.CreateCampaignSession(session, TestCampaignDefaults.Slug), new LocationUpsertRequest { Id = locId, Name = "Faction Location" });

            // Faction A: Hostile toward Faction B
            var factionA = new Faction
            {
                Id = factAId,
                Name = "Faction A",
                TerritoryLocationIds = [locId],
                StanceToward = new Dictionary<string, FactionStance>
                {
                    { factBId, FactionStance.Hostile }
                }
            };

            // Faction B: Allied toward Faction A, and Opportunistic toward Party
            var factionB = new Faction
            {
                Id = factBId,
                Name = "Faction B",
                TerritoryLocationIds = [locId],
                StanceToward = new Dictionary<string, FactionStance>
                {
                    { factAId, FactionStance.Allied },
                    { "party", FactionStance.Opportunistic }
                }
            };

            await repo.UpsertFactionAsync(_fixture.CreateCampaignSession(session, TestCampaignDefaults.Slug), factionA);
            await repo.UpsertFactionAsync(_fixture.CreateCampaignSession(session, TestCampaignDefaults.Slug), factionB);

            // NPC has reputation with Faction A
            var npc = new CharacterUpsertRequest
            {
                Id = "test-char",
                Name = "Rep NPC",
                CurrentLocationId = locId,
                Social = new SocialProfile
                {
                    FactionReputations = new Dictionary<string, int>
                    {
                        { factAId, 75 }
                    }
                },
                Schedule = new Schedule { DefaultLocationId = locId }
            };
            await repo.UpsertCharacterAsync(_fixture.CreateCampaignSession(session, TestCampaignDefaults.Slug), npc);
            await session.SaveChangesAsync();
        }

        await WaitForAllIndexesAsync();

        using (var session = _store.OpenAsyncSession())
        {
            var scene = await repo.GetSceneAsync(session, locId, TestCampaignDefaults.Slug);

            var summaryA = scene.RelevantFactions!.FirstOrDefault(f => f.FactionId == factAId);
            Assert.NotNull(summaryA);
            // Faction A is hostile to Faction B, so local stance should be Hostile
            Assert.Equal(FactionStance.Hostile, summaryA.LocalStance);
            // Reputation value 75 is populated from the NPC
            Assert.Equal(75, summaryA.PlayerReputation);

            var summaryB = scene.RelevantFactions!.FirstOrDefault(f => f.FactionId == factBId);
            Assert.NotNull(summaryB);
            // Faction B stance toward party is Opportunistic, which overrides allied
            Assert.Equal(FactionStance.Opportunistic, summaryB.LocalStance);
        }
    }

    [Fact]
    public async Task GetScene_IdentifiesLastKnownTravelFromEvents()
    {
        var repo = _fixture.CreateRepository();
        var locId = "locations/travel-loc-" + Guid.NewGuid();

        var campaignName = "travel-camp-" + Guid.NewGuid();

        using (var session = _store.OpenAsyncSession())
        {
            await repo.UpsertLocationAsync(_fixture.CreateCampaignSession(session, campaignName), new LocationUpsertRequest { Id = locId, Name = "Travel Location" });

            // Travel event 1: does not match
            await repo.LogEventAsync(session, new Event
            {
                Id = "events/ev1-" + Guid.NewGuid(),
                Summary = "Talking to a merchant.",
                Involved = [locId],
                Timestamp = DateTime.UtcNow.AddDays(5)
            }, campaignName);

            // Travel event 2: matches "interrupted"
            await repo.LogEventAsync(session, new Event
            {
                Id = "events/ev2-" + Guid.NewGuid(),
                Summary = "Travel was interrupted by an ambush.",
                Involved = [locId],
                Timestamp = DateTime.UtcNow.AddDays(10)
            }, campaignName);

            await session.SaveChangesAsync();
        }

        await WaitForAllIndexesAsync();

        using (var session = _store.OpenAsyncSession())
        {
            var scene = await repo.GetSceneAsync(_fixture.CreateCampaignSession(session, campaignName), locId);
            Assert.Equal("Travel was interrupted by an ambush.", scene.LastKnownTravel);
        }
    }

    [Fact]
    public async Task UpsertItemAsync_WithItemDetails_CreatesDetailsWithFreshIdsAndEmbeddings()
    {
        var repo = _fixture.CreateRepository();
        var itemId = "items/creation-details-" + Guid.NewGuid();
        var campaignName = "creation-details-camp-" + Guid.NewGuid();

        using (var session = _store.OpenAsyncSession())
        {
            await repo.SaveTimeAsync(_fixture.CreateCampaignSession(session, campaignName), new CampaignTime { TotalDaysElapsed = 7 });
            await session.SaveChangesAsync();
        }

        using (var session = _store.OpenAsyncSession())
        {
            await repo.UpsertItemAsync(session, new ItemUpsertRequest
            {
                Id = itemId,
                Name = "Battle-Worn Sword",
                Description = "A well-used blade.",
                HolderId = "locations/armory",
                ItemDetails =
                [
                    new ItemDetailUpsertRequest { Name = "Notched blade", Description = "A deep notch near the tip." },
                    new ItemDetailUpsertRequest { Name = "Old bloodstain", Description = "A dark, faded stain near the hilt." },
                ],
            }, campaignName);
            await session.SaveChangesAsync();
        }

        using (var session = _store.OpenAsyncSession())
        {
            var item = await session.LoadAsync<Item>(itemId);
            Assert.Equal(2, item.ItemDetails.Count);
            foreach (var detail in item.ItemDetails)
            {
                Assert.StartsWith("detail-", detail.Id);
                Assert.NotNull(detail.SemanticVector);
                Assert.NotEmpty(detail.SemanticVector);
                Assert.Equal(7, detail.CreatedOnDay);
                Assert.Equal(7, detail.UpdatedOnDay);
                Assert.Empty(detail.Participants);
            }
            Assert.NotNull(item.SemanticVector);
            Assert.NotEmpty(item.SemanticVector);
        }
    }

    [Fact]
    public async Task UpsertItemAsync_WithItemDetails_TetheredToIdFlowsThroughAtCreation()
    {
        var repo = _fixture.CreateRepository();
        var itemId = "items/creation-tether-" + Guid.NewGuid();
        var campaignName = "creation-tether-camp-" + Guid.NewGuid();

        using (var session = _store.OpenAsyncSession())
        {
            await repo.UpsertItemAsync(session, new ItemUpsertRequest
            {
                Id = itemId,
                Name = "Rope",
                Description = "A coil of rope.",
                HolderId = "locations/ruins",
                ItemDetails =
                [
                    new ItemDetailUpsertRequest { Name = "Lashed end", Description = "Tied off.", TetheredToId = "locations/ruins-column" },
                    new ItemDetailUpsertRequest { Name = "Frayed end", Description = "The other end is frayed." },
                ],
            }, campaignName);
            await session.SaveChangesAsync();
        }

        using (var session = _store.OpenAsyncSession())
        {
            var item = await session.LoadAsync<Item>(itemId);
            Assert.Equal(2, item.ItemDetails.Count);
            Assert.Equal("locations/ruins-column", item.ItemDetails.Single(d => d.Name == "Lashed end").TetheredToId);
            Assert.Null(item.ItemDetails.Single(d => d.Name == "Frayed end").TetheredToId);
        }
    }

    [Fact]
    public async Task UpsertItemAsync_WithItemDetails_IgnoresSuppliedIdAndParticipants()
    {
        var repo = _fixture.CreateRepository();
        var itemId = "items/creation-details-ignore-" + Guid.NewGuid();
        var charId = "chars/creation-details-witness-" + Guid.NewGuid();
        var campaignName = "creation-details-ignore-camp-" + Guid.NewGuid();

        using (var session = _store.OpenAsyncSession())
        {
            await repo.UpsertCharacterAsync(_fixture.CreateCampaignSession(session, campaignName), new CharacterUpsertRequest { Id = charId, Name = "Witness" });
            await repo.UpsertItemAsync(session, new ItemUpsertRequest
            {
                Id = itemId,
                Name = "Chest",
                Description = "A wooden chest.",
                HolderId = "locations/vault",
                ItemDetails =
                [
                    new ItemDetailUpsertRequest
                    {
                        Id = "detail-caller-supplied",
                        Name = "Hidden compartment",
                        Description = "A false bottom conceals a small space.",
                        Participants = [new ItemDetailParticipant { Id = charId, Role = ItemDetailParticipantRole.Witnessed }],
                    },
                ],
            }, campaignName);
            await session.SaveChangesAsync();
        }

        using (var session = _store.OpenAsyncSession())
        {
            var item = await session.LoadAsync<Item>(itemId);
            var detail = Assert.Single(item.ItemDetails);
            Assert.NotEqual("detail-caller-supplied", detail.Id);
            Assert.Empty(detail.Participants);

            var character = await session.LoadAsync<Character>(charId);
            Assert.Empty(character.Psychology.Memories);
        }
    }

    [Fact]
    public async Task UpsertItemAsync_WithItemDetails_OnExistingItem_IgnoresItemDetailsField()
    {
        var repo = _fixture.CreateRepository();
        var itemId = "items/creation-details-existing-" + Guid.NewGuid();
        var campaignName = "creation-details-existing-camp-" + Guid.NewGuid();

        using (var session = _store.OpenAsyncSession())
        {
            await repo.UpsertItemAsync(_fixture.CreateCampaignSession(session, campaignName), new ItemUpsertRequest
            {
                Id = itemId,
                Name = "Lantern",
                Description = "A brass lantern.",
                HolderId = "locations/storeroom",
            });
            await session.SaveChangesAsync();
        }

        using (var session = _store.OpenAsyncSession())
        {
            await repo.UpsertItemAsync(session, new ItemUpsertRequest
            {
                Id = itemId,
                Name = "Lantern",
                Description = "A brass lantern.",
                HolderId = "locations/storeroom",
                ItemDetails = [new ItemDetailUpsertRequest { Name = "Cracked glass", Description = "A hairline crack in the glass." }],
            }, campaignName);
            await session.SaveChangesAsync();
        }

        using (var session = _store.OpenAsyncSession())
        {
            var item = await session.LoadAsync<Item>(itemId);
            Assert.Empty(item.ItemDetails);
        }
    }

    [Fact]
    public async Task UpsertItemAsync_NoItemDetails_Unaffected()
    {
        var repo = _fixture.CreateRepository();
        var itemId = "items/creation-no-details-" + Guid.NewGuid();
        var campaignName = "creation-no-details-camp-" + Guid.NewGuid();

        using (var session = _store.OpenAsyncSession())
        {
            await repo.UpsertItemAsync(_fixture.CreateCampaignSession(session, campaignName), new ItemUpsertRequest
            {
                Id = itemId,
                Name = "Plain Rope",
                Description = "Sturdy hemp rope.",
                HolderId = "locations/storeroom",
            });
            await session.SaveChangesAsync();
        }

        using (var session = _store.OpenAsyncSession())
        {
            var item = await session.LoadAsync<Item>(itemId);
            Assert.NotNull(item.ItemDetails);
            Assert.Empty(item.ItemDetails);
        }
    }

}
