using System.Collections.Generic;
using CampaignVault.Data.Initiative;
using CampaignVault.Models;
using Xunit;

namespace CampaignVault.Tests;

public class NpcInitiativeTurnIntentTests
{
    private sealed class StubInitiativeProvider(string key, MemoryUrgency urgency, double weight) : INpcInitiativeSignalProvider
    {
        public IReadOnlyList<InitiativeCandidate> GetCandidates(NpcInitiativeContext ctx) =>
        [
            new InitiativeCandidate(key, ctx.Npc.Id, InitiativeDriver.Relational, urgency, "The innkeeper looks at you expectantly.", weight)
        ];
    }

    private static NpcInitiativeService CreateService(INpcInitiativeSignalProvider provider) =>
        new(
            [provider],
            new DefaultRelevantMemorySelector(),
            new DefaultBehavioralTensionCalculator(),
            new CampaignInitiativeSuppressionStore());

    private static Character HighTensionNpc() => new()
    {
        Id = "chars/innkeeper",
        Name = "Innkeeper",
        Psychology = new PsychologyProfile(),
        Needs = new NeedsProfile
        {
            ActiveNeeds = new Dictionary<string, float> { ["hunger"] = 95f },
            ActivityConflictActive = true
        },
        Social = new SocialProfile()
    };

    private static Character LowTensionNpc() => new()
    {
        Id = "chars/calm",
        Name = "Calm NPC",
        Psychology = new PsychologyProfile(),
        Needs = new NeedsProfile(),
        Social = new SocialProfile()
    };

    [Fact]
    public void Enrich_HighTensionAndUrgentCandidate_ProducesNpcTurnIntent()
    {
        var provider = new StubInitiativeProvider("test:urgent", MemoryUrgency.Urgent, 10);
        var service = CreateService(provider);
        var npc = HighTensionNpc();
        var campaign = new Campaign { Name = "test-camp" };
        // Low threshold so the NeedStress-driven tension from HighTensionNpc reliably crosses it,
        // without over-fitting the test to the calculator's exact weighted-sum arithmetic.
        var ctx = new NpcInitiativeContext
        {
            Npc = npc,
            Config = new CampaignConfig { BehavioralTensionSpeakingThreshold = 20 },
            CurrentDay = 1,
            SurfacedViaTool = "get_scene"
        };

        var enrichment = service.Enrich(ctx, campaign);

        Assert.NotNull(enrichment.TurnIntent);
        Assert.Equal("npc", enrichment.TurnIntent!.Holder);
        Assert.Equal("The innkeeper looks at you expectantly.", enrichment.TurnIntent.Reason);
    }

    [Fact]
    public void Enrich_LowTension_ProducesNullTurnIntent()
    {
        var provider = new StubInitiativeProvider("test:mild", MemoryUrgency.Urgent, 10);
        var service = CreateService(provider);
        var npc = LowTensionNpc();
        var campaign = new Campaign { Name = "test-camp" };
        var ctx = new NpcInitiativeContext
        {
            Npc = npc,
            Config = new CampaignConfig(),
            CurrentDay = 1,
            SurfacedViaTool = "get_scene"
        };

        var enrichment = service.Enrich(ctx, campaign);

        Assert.Null(enrichment.TurnIntent);
    }

    [Fact]
    public void Enrich_HighTensionButLowUrgencyCandidate_ProducesNullTurnIntent()
    {
        var provider = new StubInitiativeProvider("test:low-urgency", MemoryUrgency.Normal, 10);
        var service = CreateService(provider);
        var npc = HighTensionNpc();
        var campaign = new Campaign { Name = "test-camp" };
        var ctx = new NpcInitiativeContext
        {
            Npc = npc,
            Config = new CampaignConfig { BehavioralTensionSpeakingThreshold = 20 },
            CurrentDay = 1,
            SurfacedViaTool = "get_scene"
        };

        var enrichment = service.Enrich(ctx, campaign);

        Assert.Null(enrichment.TurnIntent);
    }
}
