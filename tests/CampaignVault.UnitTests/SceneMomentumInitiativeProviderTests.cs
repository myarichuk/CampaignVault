using System.Collections.Generic;
using System.Linq;
using CampaignVault.Data.Initiative;
using CampaignVault.Models;
using Xunit;

namespace CampaignVault.Tests;

public class SceneMomentumInitiativeProviderTests
{
    private static readonly Character Pc = new() { Id = "chars/pc1", Name = "PC", IsPc = true };

    private static Character BuildCompanion(int idleBeats, bool isCompanion = true, bool keepAlive = false) =>
        new()
        {
            Id = "chars/companion",
            Name = "Companion",
            IsPartyCompanion = isCompanion,
            KeepAlive = keepAlive,
            IdleSceneBeats = idleBeats
        };

    private static NpcInitiativeContext BuildContext(Character npc, CampaignConfig? config = null, IReadOnlyList<Character>? presentEntities = null) =>
        new()
        {
            Npc = npc,
            Config = config ?? new CampaignConfig(),
            CurrentDay = 1,
            SurfacedViaTool = "take_turn",
            PresentEntities = presentEntities ?? [Pc]
        };

    [Fact]
    public void BelowNormalThreshold_ReturnsNoCandidate()
    {
        var config = new CampaignConfig { MomentumIdleBeatsNormalThreshold = 4 };
        var npc = BuildCompanion(idleBeats: 3);

        var candidates = new SceneMomentumInitiativeProvider().GetCandidates(BuildContext(npc, config));

        Assert.Empty(candidates);
    }

    [Fact]
    public void AtNormalThreshold_ReturnsNormalUrgencyCandidate()
    {
        var config = new CampaignConfig { MomentumIdleBeatsNormalThreshold = 4, MomentumIdleBeatsHighThreshold = 8 };
        var npc = BuildCompanion(idleBeats: 4);

        var candidates = new SceneMomentumInitiativeProvider().GetCandidates(BuildContext(npc, config));

        var candidate = Assert.Single(candidates);
        Assert.Equal(InitiativeDriver.Momentum, candidate.Driver);
        Assert.Equal(MemoryUrgency.Normal, candidate.Urgency);
    }

    [Fact]
    public void AtHighThreshold_EscalatesToHighUrgency()
    {
        var config = new CampaignConfig { MomentumIdleBeatsNormalThreshold = 4, MomentumIdleBeatsHighThreshold = 8 };
        var npc = BuildCompanion(idleBeats: 8);

        var candidates = new SceneMomentumInitiativeProvider().GetCandidates(BuildContext(npc, config));

        var candidate = Assert.Single(candidates);
        Assert.Equal(MemoryUrgency.High, candidate.Urgency);
    }

    [Fact]
    public void NotCompanionOrKeepAlive_NeverSurfaces_RegardlessOfIdleBeats()
    {
        var npc = BuildCompanion(idleBeats: 20, isCompanion: false, keepAlive: false);

        var candidates = new SceneMomentumInitiativeProvider().GetCandidates(BuildContext(npc));

        Assert.Empty(candidates);
    }

    [Fact]
    public void KeepAliveNonCompanion_StillSurfaces()
    {
        var config = new CampaignConfig { MomentumIdleBeatsNormalThreshold = 4 };
        var npc = BuildCompanion(idleBeats: 5, isCompanion: false, keepAlive: true);

        var candidates = new SceneMomentumInitiativeProvider().GetCandidates(BuildContext(npc, config));

        Assert.Single(candidates);
    }

    [Fact]
    public void NoPcPresent_ReturnsNoCandidate()
    {
        var config = new CampaignConfig { MomentumIdleBeatsNormalThreshold = 4 };
        var npc = BuildCompanion(idleBeats: 10);

        var candidates = new SceneMomentumInitiativeProvider().GetCandidates(
            BuildContext(npc, config, presentEntities: []));

        Assert.Empty(candidates);
    }

    [Fact]
    public void HigherIdleBeats_ProducesHigherWeight()
    {
        var config = new CampaignConfig { MomentumIdleBeatsNormalThreshold = 4, MomentumIdleBeatsHighThreshold = 8 };
        var lowWeight = new SceneMomentumInitiativeProvider()
            .GetCandidates(BuildContext(BuildCompanion(4), config)).Single().Weight;
        var highWeight = new SceneMomentumInitiativeProvider()
            .GetCandidates(BuildContext(BuildCompanion(9), config)).Single().Weight;

        Assert.True(highWeight > lowWeight);
    }

    [Fact]
    public void ExtravertedTrait_CrossesThresholdSoonerThanNeutral()
    {
        var config = new CampaignConfig { MomentumIdleBeatsNormalThreshold = 4, MomentumIdleBeatsHighThreshold = 8 };
        var extravert = BuildCompanion(idleBeats: 3);
        extravert.Psychology = new PsychologyProfile { Traits = ["gregarious", "impulsive"] };

        var candidates = new SceneMomentumInitiativeProvider().GetCandidates(BuildContext(extravert, config));

        Assert.Single(candidates);
    }

    [Fact]
    public void IntrovertedTrait_HoldsBackPastNeutralThreshold()
    {
        var config = new CampaignConfig { MomentumIdleBeatsNormalThreshold = 4, MomentumIdleBeatsHighThreshold = 8 };
        var introvert = BuildCompanion(idleBeats: 4);
        introvert.Psychology = new PsychologyProfile { Traits = ["reserved"] };

        var candidates = new SceneMomentumInitiativeProvider().GetCandidates(BuildContext(introvert, config));

        Assert.Empty(candidates);
    }
}
