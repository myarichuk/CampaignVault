using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using CampaignVault.Data;
using CampaignVault.Data.Initiative;
using CampaignVault.Models;
using Xunit;

namespace CampaignVault.Tests;

[Collection("RavenDB")]
public class Phase10InitiativeCoreTests : IClassFixture<RavenDBFixture>
{
    private readonly RavenDBFixture _fixture;
    private readonly CampaignDocumentKeys _keys = new();

    public Phase10InitiativeCoreTests(RavenDBFixture fixture)
    {
        _fixture = fixture;
    }

    private sealed class StubInitiativeProvider(string key, double weight) : INpcInitiativeSignalProvider
    {
        public IReadOnlyList<InitiativeCandidate> GetCandidates(NpcInitiativeContext ctx) =>
        [
            new InitiativeCandidate(
                key,
                ctx.Npc.Id,
                InitiativeDriver.Relational,
                MemoryUrgency.Normal,
                "Test framing.",
                weight)
        ];
    }

    private CampaignRepository CreateRepo(params INpcInitiativeSignalProvider[] providers)
    {
        var service = new NpcInitiativeService(
            providers,
            new DefaultRelevantMemorySelector(),
            new DefaultBehavioralTensionCalculator(),
            new CampaignInitiativeSuppressionStore());

        return _fixture.CreateRepository(overrides: b => { b.RegisterInstance(service).As<INpcInitiativeService>(); });
    }

    private sealed class TestSimulationEngine : IWorldSimulationEngine
    {
        public Task<SimulationResult> RunAsync(SimulationContext context, CancellationToken ct = default) =>
            Task.FromResult(new SimulationResult([], [], [], [], []));
    }

    private async Task SeedCampaignAsync(string campaignName)
    {
        using var session = _fixture.Store.OpenAsyncSession();
        await session.StoreAsync(new Campaign
        {
            Id = _keys.Meta(campaignName),
            Name = campaignName,
            DisplayName = campaignName
        });
        await session.StoreAsync(new CampaignConfig
        {
            Id = _keys.Config(campaignName)
        });
        await session.StoreAsync(new CampaignTime
        {
            Id = _keys.StateTime(campaignName),
            TotalDaysElapsed = 10
        });
        await session.SaveChangesAsync();
    }

    [Fact]
    public void Tension_IsDeterministic_ForSameState()
    {
        var calc = new DefaultBehavioralTensionCalculator();
        var npc = BuildTensionNpc();
        var config = new CampaignConfig();
        var ctx = BuildTensionContext(config);

        var memories = new DefaultRelevantMemorySelector().Select(npc, ctx);
        var first = calc.Calculate(npc, ctx, memories);
        var second = calc.Calculate(npc, ctx, memories);

        Assert.Equal(first.Tension, second.Tension);
        Assert.Equal(first.Breakdown, second.Breakdown);
    }

    [Fact]
    public void Tension_ConfigWeight_Disposition_RaisesWeightedContribution()
    {
        var calc = new DefaultBehavioralTensionCalculator();
        var npc = new Character
        {
            Id = "chars/disposition-only",
            Name = "Disposition Only",
            Psychology = new PsychologyProfile { Fears = ["crowds"] },
            Needs = new NeedsProfile(),
            Social = new SocialProfile()
        };

        var location = new Location { Id = "locs/market", Name = "Market", VisualTags = ["busy"] };
        var lowDispositionConfig = new CampaignConfig
        {
            TensionWeightNeed = 0.25f,
            TensionWeightMemory = 0.25f,
            TensionWeightRelational = 0.25f,
            TensionWeightDisposition = 0.05f,
            DispositionKeywordExpansions = new Dictionary<string, List<string>>
            {
                ["crowds"] = ["busy"]
            }
        };
        var highDispositionConfig = new CampaignConfig
        {
            TensionWeightNeed = 0.25f,
            TensionWeightMemory = 0.25f,
            TensionWeightRelational = 0.25f,
            TensionWeightDisposition = 0.35f,
            DispositionKeywordExpansions = lowDispositionConfig.DispositionKeywordExpansions
        };

        var ctxLow = BuildTensionContext(lowDispositionConfig, location, npc);
        var ctxHigh = BuildTensionContext(highDispositionConfig, location, npc);

        var low = calc.Calculate(npc, ctxLow, []);
        var high = calc.Calculate(npc, ctxHigh, []);

        var lowContribution = low.Breakdown.DispositionStress * lowDispositionConfig.TensionWeightDisposition;
        var highContribution = high.Breakdown.DispositionStress * highDispositionConfig.TensionWeightDisposition;

        Assert.Equal(low.Breakdown.DispositionStress, high.Breakdown.DispositionStress);
        Assert.True(highContribution > lowContribution);
    }

    [Fact]
    public void DispositionMatcher_RespectsMinTokenLength()
    {
        var config = new CampaignConfig { DispositionMinTokenLength = 3 };
        var psychology = new PsychologyProfile { Fears = ["in"] };
        var location = new Location { Id = "locs/inn", Name = "Inn", VisualTags = ["inn"] };

        var (_, _, stress) = DispositionMatcher.Score(psychology, [], location, config);
        Assert.Equal(0f, stress);

        psychology.Fears = ["crowd"];
        config.DispositionKeywordExpansions = new Dictionary<string, List<string>>
        {
            ["crowd"] = ["busy"]
        };
        location.VisualTags = ["busy"];

        (_, _, stress) = DispositionMatcher.Score(psychology, [], location, config);
        Assert.True(stress > 0f);
    }

    [Fact]
    public async Task SuppressionStore_MarkConsumed_PersistsOnCampaign()
    {
        await SeedCampaignAsync("suppression-test");
        var repo = CreateRepo(new StubInitiativeProvider("test:once", 10));
        using var session = _fixture.Store.OpenAsyncSession();

        var npc = new Character
        {
            Id = "chars/barliman",
            Name = "Barliman",
            CampaignName = "suppression-test",
            CurrentLocationId = "locs/prancing"
        };
        await session.StoreAsync(npc);
        await session.StoreAsync(new Location
        {
            Id = "locs/prancing",
            Name = "Prancing Pony",
            CampaignName = "suppression-test"
        });
        await session.SaveChangesAsync();

        var first = await repo.EnrichNpcInitiativeAsync(
            session, npc, "suppression-test", "get_npc_context", includeTensionBreakdown: true);
        await session.SaveChangesAsync();

        Assert.Single(first.ActiveInitiatives);

        var second = await repo.EnrichNpcInitiativeAsync(
            session, npc, "suppression-test", "get_npc_context", includeTensionBreakdown: true);

        Assert.Empty(second.ActiveInitiatives);

        var campaign = await session.LoadAsync<Campaign>(_keys.Meta("suppression-test"));
        Assert.Contains(campaign!.InitiativeSurfaced.Keys, k => k.Contains("test:once", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetScene_IncludesBehavioralTension_WithoutBreakdown()
    {
        await SeedCampaignAsync("scene-tension");
        var repo = CreateRepo();
        using var session = _fixture.Store.OpenAsyncSession();

        var locId = "locs/tavern";
        await session.StoreAsync(new Location { Id = locId, Name = "Tavern", CampaignName = "scene-tension" });
        await session.StoreAsync(new Character
        {
            Id = "chars/tired",
            Name = "Tired Bob",
            CampaignName = "scene-tension",
            CurrentLocationId = locId,
            Schedule = new Schedule { DefaultLocationId = locId },
            Needs = new NeedsProfile
            {
                ActiveNeeds = new Dictionary<string, float> { ["tiredness"] = 90f },
                ActivityConflictActive = true
            }
        });
        await session.SaveChangesAsync();
        session.Advanced.WaitForIndexesAfterSaveChanges(
            timeout: TimeSpan.FromSeconds(10),
            throwOnTimeout: true,
            indexes: ["Character/Search"]);

        var scene = await repo.GetSceneAsync(_fixture.CreateCampaignSession(session, "scene-tension"), locId);
        var npc = scene.PresentNPCs.Single();

        Assert.True(npc.BehavioralTension > 20);
        Assert.NotNull(npc.RelevantMemories);
    }

    [Fact]
    public async Task GetNpcContext_IncludesTensionBreakdown()
    {
        await SeedCampaignAsync("context-tension");
        var tools = TestCampaignToolsFactory.Create(_fixture);
        using var session = _fixture.Store.OpenAsyncSession();

        var charId = "chars/deep";
        await session.StoreAsync(new Character
        {
            Id = charId,
            Name = "Deep NPC",
            CampaignName = "context-tension",
            Psychology = new PsychologyProfile
            {
                Fears = ["crowds"],
                Memories = new Dictionary<string, MemoryNode>
                {
                    ["Market"] = new MemoryNode
                    {
                        Topic = "Market",
                        Details = "Busy place.",
                        Salience = 0.8,
                        Valence = EmotionalValence.Negative,
                        DayAcquired = 9
                    }
                }
            },
            Needs = new NeedsProfile
            {
                ActiveNeeds = new Dictionary<string, float> { ["hunger"] = 50f }
            }
        });
        await session.SaveChangesAsync();

        var result = await tools.GetNpcContext(charId, "context-tension");
        Assert.True(result.Success);
        Assert.NotNull(result.Data!.TensionComponents);
        Assert.True(result.Data.BehavioralTension >= 0);
    }

    [Fact]
    public void RelevantMemorySelector_PrefersEntityOverlap()
    {
        var selector = new DefaultRelevantMemorySelector();
        var npc = new Character
        {
            Id = "chars/npc",
            Name = "NPC",
            Psychology = new PsychologyProfile
            {
                Memories = new Dictionary<string, MemoryNode>
                {
                    ["Generic"] = new MemoryNode
                        { Topic = "Generic", Details = "Nothing special.", Salience = 0.4, DayAcquired = 1 },
                    ["Party"] = new MemoryNode
                    {
                        Topic = "Party",
                        Details = "They helped me.",
                        Salience = 0.5,
                        RelatedEntityIds = ["chars/pc1"],
                        DayAcquired = 1
                    }
                }
            }
        };

        var ctx = new NpcInitiativeContext
        {
            Npc = npc,
            Config = new CampaignConfig(),
            CurrentDay = 10,
            SurfacedViaTool = "get_npc_context",
            PresentEntities =
            [
                new Character { Id = "chars/pc1", Name = "PC" }
            ]
        };

        var selected = selector.Select(npc, ctx);
        Assert.Equal("Party", selected[0].Topic);
    }

    private static Character BuildTensionNpc() =>
        new()
        {
            Id = "chars/test",
            Name = "Test",
            Psychology = new PsychologyProfile
            {
                Fears = ["crowds"],
                Resilience = 0.5,
                Memories = new Dictionary<string, MemoryNode>
                {
                    ["Scene"] = new MemoryNode
                    {
                        Topic = "Scene",
                        Details = "Witnessed violence.",
                        Salience = 0.7,
                        Valence = EmotionalValence.Negative,
                        DayAcquired = 9
                    }
                }
            },
            Needs = new NeedsProfile
            {
                ActiveNeeds = new Dictionary<string, float> { ["tiredness"] = 60f }
            },
            Social = new SocialProfile
            {
                Relationships = new Dictionary<string, int> { ["chars/pc1"] = 85 }
            }
        };

    private static NpcInitiativeContext BuildTensionContext(
        CampaignConfig config,
        Location? location = null,
        Character? npc = null) =>
        new()
        {
            Npc = npc ?? BuildTensionNpc(),
            Location = location ?? new Location { Id = "locs/1", Name = "Square", VisualTags = ["busy"] },
            Config = config,
            CurrentDay = 10,
            SurfacedViaTool = "get_npc_context",
            IncludeTensionBreakdown = true,
            PresentEntities = []
        };
}
