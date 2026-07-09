using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using CampaignVault.Data;
using CampaignVault.Data.Initiative;
using CampaignVault.Models;
using Xunit;

namespace CampaignVault.Tests;

[Collection("RavenDB")]
public class Phase10InitiativeProviderTests : IClassFixture<RavenDBFixture>
{
    private readonly RavenDBFixture _fixture;
    private readonly CampaignDocumentKeys _keys = new();

    public Phase10InitiativeProviderTests(RavenDBFixture fixture)
    {
        _fixture = fixture;
    }

    private sealed class TestSimulationEngine : IWorldSimulationEngine
    {
        public Task<SimulationResult> RunAsync(SimulationContext context, CancellationToken ct = default) =>
            Task.FromResult(new SimulationResult([], [], [], [], []));
    }

    private CampaignRepository CreateRepo() =>
        _fixture.CreateRepository(
            engineOverride: new TestSimulationEngine(),
            overrides: b => b.RegisterInstance(InitiativeServiceFactory.CreateDefault()).As<INpcInitiativeService>());

    private static NpcInitiativeContext BuildCtx(
        Character npc,
        CampaignConfig? config = null,
        Location? location = null,
        IReadOnlyList<Character>? present = null,
        IReadOnlyList<Event>? npcEvents = null,
        IReadOnlyList<Item>? items = null) =>
        new()
        {
            Npc = npc,
            Location = location,
            PresentEntities = present ?? [npc],
            NpcRecentEvents = npcEvents ?? [],
            NpcHeldItems = items ?? [],
            Config = config ?? new CampaignConfig(),
            CurrentDay = 10,
            SurfacedViaTool = "get_npc_context",
            IncludeTensionBreakdown = true
        };

    [Fact]
    public void RelationalProvider_StructuredGiftBeat_EmitsGratitude()
    {
        var provider = new RelationalInitiativeProvider();
        var npc = new Character { Id = "chars/barliman", Name = "Barliman" };
        var ev = new Event
        {
            Summary = "Received a necklace",
            DayLogged = 10,
            Involved = ["chars/barliman", "chars/pc1"],
            EmotionalBeat = "gift_received",
            RelatedEntityId = "items/necklace"
        };

        var candidates = provider.GetCandidates(BuildCtx(npc, npcEvents: [ev]));

        Assert.Contains(candidates, c => c.Driver == InitiativeDriver.Relational && c.Key.Contains("gratitude", StringComparison.Ordinal));
    }

    [Fact]
    public void RelationalProvider_HeuristicGiftSummary_EmitsGratitude()
    {
        var provider = new RelationalInitiativeProvider();
        var npc = new Character { Id = "chars/bob", Name = "Bob" };
        var ev = new Event
        {
            Summary = "The party gave Bob a silver reward after the rescue.",
            DayLogged = 9,
            Involved = ["chars/bob"]
        };
        var config = new CampaignConfig();

        var candidates = provider.GetCandidates(BuildCtx(npc, config, npcEvents: [ev]));

        Assert.Single(candidates);
        Assert.Equal(InitiativeDriver.Relational, candidates[0].Driver);
    }

    [Fact]
    public void RelationalProvider_AffectionBand_EmitsForPresentTarget()
    {
        var provider = new RelationalInitiativeProvider();
        var npc = new Character
        {
            Id = "chars/barliman",
            Name = "Barliman",
            Social = new SocialProfile
            {
                Relationships = new Dictionary<string, int> { ["chars/pc1"] = 85 }
            }
        };
        var pc = new Character { Id = "chars/pc1", Name = "Aldric" };

        var candidates = provider.GetCandidates(BuildCtx(npc, present: [npc, pc]));

        Assert.Contains(candidates, c => c.Key.StartsWith("affection:", StringComparison.Ordinal));
    }

    [Fact]
    public void MemoryProvider_SceneMatch_EmitsMemoryDriver()
    {
        var provider = new MemoryInitiativeProvider();
        var npc = new Character
        {
            Id = "chars/guard",
            Name = "Guard",
            Psychology = new PsychologyProfile
            {
                Memories = new Dictionary<string, MemoryNode>
                {
                    ["Market violence"] = new MemoryNode
                    {
                        Topic = "Market violence",
                        Details = "Witnessed a brawl in the Market square.",
                        Salience = 0.8,
                        Valence = EmotionalValence.Negative,
                        Urgency = MemoryUrgency.High,
                        DayAcquired = 8
                    }
                }
            }
        };
        var location = new Location { Id = "locs/market", Name = "Market", VisualTags = ["busy"] };

        var candidates = provider.GetCandidates(BuildCtx(npc, location: location));

        Assert.Single(candidates);
        Assert.Equal(InitiativeDriver.Memory, candidates[0].Driver);
    }

    [Fact]
    public void NeedProvider_ActivityConflict_EmitsNeedDriver()
    {
        var provider = new NeedActivityConflictProvider();
        var npc = new Character
        {
            Id = "chars/barkeep",
            Name = "Barkeep",
            CurrentActivity = "tending bar",
            Needs = new NeedsProfile
            {
                ActiveNeeds = new Dictionary<string, float> { ["tiredness"] = 85f },
                ActivityConflictActive = true,
                ActivityConflictNeed = "tiredness"
            }
        };

        var candidates = provider.GetCandidates(BuildCtx(npc));

        Assert.Single(candidates);
        Assert.Equal(InitiativeDriver.Need, candidates[0].Driver);
        Assert.Contains("Exhausted", candidates[0].FramingPrompt, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("bloodlust", "Bloodthirsty")]
    [InlineData("paranoia", "Paranoid")]
    [InlineData("obsession", "Consumed")]
    [InlineData("despair", "Despairing")]
    [InlineData("guilt", "Guilt-ridden")]
    public void NeedProvider_CustomNeeds_HaveEvocativeFramings(string need, string expectedAdjective)
    {
        var provider = new NeedActivityConflictProvider();
        var npc = new Character
        {
            Id = $"chars/npc-{need}",
            Name = "Test NPC",
            CurrentActivity = "some task",
            Needs = new NeedsProfile
            {
                ActiveNeeds = new Dictionary<string, float> { [need] = 75f },
                ActivityConflictActive = true,
                ActivityConflictNeed = need
            }
        };

        var candidates = provider.GetCandidates(BuildCtx(npc));

        Assert.Single(candidates);
        Assert.Contains(expectedAdjective, candidates[0].FramingPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public void NeedProvider_UnknownNeed_UsesGenericFallback()
    {
        var provider = new NeedActivityConflictProvider();
        var npc = new Character
        {
            Id = "chars/npc-custom",
            Name = "Test NPC",
            CurrentActivity = "working",
            Needs = new NeedsProfile
            {
                ActiveNeeds = new Dictionary<string, float> { ["wanderlust"] = 70f },
                ActivityConflictActive = true,
                ActivityConflictNeed = "wanderlust"
            }
        };

        var candidates = provider.GetCandidates(BuildCtx(npc));

        Assert.Single(candidates);
        Assert.Contains("Restless", candidates[0].FramingPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public void DispositionProvider_FearMatch_EmitsDispositionDriver()
    {
        var provider = new DispositionInitiativeProvider();
        var npc = new Character
        {
            Id = "chars/anxious",
            Name = "Anxious NPC",
            Psychology = new PsychologyProfile { Fears = ["crowds"] }
        };
        var config = new CampaignConfig
        {
            DispositionKeywordExpansions = new Dictionary<string, List<string>>
            {
                ["crowds"] = ["busy"]
            }
        };
        var location = new Location { Id = "locs/market", Name = "Market", VisualTags = ["busy"] };

        var candidates = provider.GetCandidates(BuildCtx(npc, config, location));

        Assert.Single(candidates);
        Assert.Equal(InitiativeDriver.Disposition, candidates[0].Driver);
    }

    [Fact]
    public async Task Integration_GratitudeStructured_SurfacesOnGetNpcContext()
    {
        const string campaign = "provider-integration";
        using var session = _fixture.Store.OpenAsyncSession();
        await session.StoreAsync(new Campaign { Id = _keys.Meta(campaign), Name = campaign, DisplayName = campaign });
        await session.StoreAsync(new CampaignConfig { Id = _keys.Config(campaign) });
        await session.StoreAsync(new CampaignTime { Id = _keys.StateTime(campaign), TotalDaysElapsed = 10 });
        await session.StoreAsync(new Character { Id = "chars/barliman", Name = "Barliman", CampaignName = campaign });
        await session.StoreAsync(new Event
        {
            Id = "events/gift",
            Summary = "Party gave Barliman a necklace",
            DayLogged = 10,
            CampaignName = campaign,
            Involved = ["chars/barliman", "chars/pc1"],
            EmotionalBeat = "gift_received",
            RelatedEntityId = "items/necklace"
        });
        await session.SaveChangesAsync();
        session.Advanced.WaitForIndexesAfterSaveChanges(
            timeout: TimeSpan.FromSeconds(10),
            throwOnTimeout: true,
            indexes: ["Event/Search"]);

        var repo = CreateRepo();
        var npc = await session.LoadAsync<Character>("chars/barliman");
        var giftEvent = await session.LoadAsync<Event>("events/gift");
        Assert.NotNull(giftEvent);

        var enrichment = await repo.EnrichNpcInitiativeAsync(
            session,
            npc!,
            campaign,
            "get_npc_context",
            includeTensionBreakdown: true,
            recentEvents: [giftEvent]);

        Assert.Contains(enrichment.ActiveInitiatives, c => c.Driver == InitiativeDriver.Relational);
    }

    [Fact]
    public async Task Integration_Suppression_SecondReadEmptyUntilRearm()
    {
        const string campaign = "provider-suppression";
        using var session = _fixture.Store.OpenAsyncSession();
        await session.StoreAsync(new Campaign { Id = _keys.Meta(campaign), Name = campaign, DisplayName = campaign });
        await session.StoreAsync(new CampaignConfig { Id = _keys.Config(campaign) });
        await session.StoreAsync(new CampaignTime { Id = _keys.StateTime(campaign), TotalDaysElapsed = 10 });
        await session.StoreAsync(new Character
        {
            Id = "chars/barliman",
            Name = "Barliman",
            CampaignName = campaign,
            Social = new SocialProfile { Relationships = new Dictionary<string, int> { ["chars/pc1"] = 85 } }
        });
        await session.StoreAsync(new Character { Id = "chars/pc1", Name = "PC", CampaignName = campaign, CurrentLocationId = "locs/inn" });
        await session.SaveChangesAsync();

        var repo = CreateRepo();
        var npc = await session.LoadAsync<Character>("chars/barliman");
        var pc = await session.LoadAsync<Character>("chars/pc1");

        var first = await repo.EnrichNpcInitiativeAsync(
            session, npc!, campaign, "get_npc_context", true, presentEntities: [npc!, pc!]);
        await session.SaveChangesAsync();

        var second = await repo.EnrichNpcInitiativeAsync(
            session, npc!, campaign, "get_npc_context", true, presentEntities: [npc!, pc!]);

        Assert.NotEmpty(first.ActiveInitiatives);
        Assert.Empty(second.ActiveInitiatives);
    }
}
