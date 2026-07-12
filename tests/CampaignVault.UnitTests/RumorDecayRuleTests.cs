using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Models;
using Xunit;

namespace CampaignVault.Tests;

public class RumorDecayRuleTests
{
    private readonly RumorDecayRule _sut = new();

    private static SimulationContext CreateContext(int daysPassed, params Rumor[] rumors) =>
        new(
            new CampaignTime { TotalDaysElapsed = daysPassed },
            rumors,
            new List<Character>(),
            null!,
            daysPassed,
            "test-camp");

    [Fact]
    public async Task ApplyAsync_LargeTimeSkip_EscalatesNascentRumorAllTheWayToPeakInOneCall()
    {
        var rumor = new Rumor
        {
            Id = "rumors/r1",
            Subject = "Bandits on the road",
            RegionLocationId = "loc",
            State = RumorState.Nascent,
            LastStateChangeDay = 0
        };

        // 20 days of silence crosses both the Nascent->Spreading (7d) and Spreading->Peak (7d) thresholds.
        var result = await _sut.ApplyAsync(CreateContext(20, rumor));

        var delta = Assert.Single(result.Deltas.OfType<RumorEvolves>());
        Assert.Equal("rumors/r1", delta.RumorId);
        Assert.Equal(RumorState.Peak, delta.NewState);
        Assert.Contains(result.NarrativeEvents, n => n.Contains("beginning to spread"));
        Assert.Contains(result.NarrativeEvents, n => n.Contains("peak circulation"));
    }

    [Fact]
    public async Task ApplyAsync_LargeTimeSkip_DecaysPeakRumorAllTheWayToForgottenInOneCall()
    {
        var rumor = new Rumor
        {
            Id = "rumors/r2",
            Subject = "The mayor's affair",
            RegionLocationId = "loc",
            State = RumorState.Peak,
            LastStateChangeDay = 0
        };

        // 40 days of silence crosses both the Peak->Fading (14d) and Fading->Forgotten (14d) thresholds.
        var result = await _sut.ApplyAsync(CreateContext(40, rumor));

        var delta = Assert.Single(result.Deltas.OfType<RumorEvolves>());
        Assert.Equal(RumorState.Forgotten, delta.NewState);
        Assert.Contains(result.NarrativeEvents, n => n.Contains("starting to fade"));
        Assert.Contains(result.NarrativeEvents, n => n.Contains("has been forgotten"));
    }

    [Fact]
    public async Task ApplyAsync_SmallTimeSkip_AdvancesOnlyOneStep()
    {
        var rumor = new Rumor
        {
            Id = "rumors/r3",
            Subject = "A missing cat",
            RegionLocationId = "loc",
            State = RumorState.Nascent,
            LastStateChangeDay = 0
        };

        // 10 days crosses only the first threshold (7d); not enough remaining (3d) to reach Peak.
        var result = await _sut.ApplyAsync(CreateContext(10, rumor));

        var delta = Assert.Single(result.Deltas.OfType<RumorEvolves>());
        Assert.Equal(RumorState.Spreading, delta.NewState);
    }

    [Fact]
    public async Task ApplyAsync_NoThresholdCrossed_EmitsNoDelta()
    {
        var rumor = new Rumor
        {
            Id = "rumors/r4",
            Subject = "Rats in the cellar",
            RegionLocationId = "loc",
            State = RumorState.Nascent,
            LastStateChangeDay = 0
        };

        var result = await _sut.ApplyAsync(CreateContext(3, rumor));

        Assert.Empty(result.Deltas);
        Assert.Empty(result.Narratives);
    }
}
