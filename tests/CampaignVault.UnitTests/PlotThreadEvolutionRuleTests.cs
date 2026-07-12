using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Models;
using Xunit;

namespace CampaignVault.Tests;

public class PlotThreadEvolutionRuleTests
{
    private readonly PlotThreadEvolutionRule _sut = new();

    private static SimulationContext CreateContext(int daysPassed, params PlotThread[] threads) =>
        new(
            new CampaignTime { TotalDaysElapsed = daysPassed },
            new List<Rumor>(),
            new List<Character>(),
            null!,
            daysPassed,
            "test-camp",
            ActivePlotThreads: threads);

    [Fact]
    public async Task ApplyAsync_SmallTensionJump_OnlyEscalatesOneStep()
    {
        var thread = new PlotThread
        {
            Id = "plot-threads/t1",
            Title = "The Missing Heir",
            State = PlotThreadState.Active,
            TensionLevel = 50
        };

        // +5/day * 2 days = 10 -> tension 60, crosses Active->Escalating only.
        var result = await _sut.ApplyAsync(CreateContext(2, thread));

        var delta = Assert.Single(result.Deltas.OfType<PlotThreadProgress>());
        Assert.Equal(PlotThreadState.Escalating, delta.NewState);
        Assert.Contains(result.NarrativeEvents, n => n.Contains("has escalated"));
        Assert.DoesNotContain(result.NarrativeEvents, n => n.Contains("CLIMAX"));
    }

    [Fact]
    public async Task ApplyAsync_LargeTensionJumpFromActive_CascadesDirectlyToClimaxInOneCall()
    {
        var thread = new PlotThread
        {
            Id = "plot-threads/t2",
            Title = "The Cult's Ritual",
            State = PlotThreadState.Active,
            TensionLevel = 20
        };

        // +5/day * 16 days = 80 -> tension 100 (clamped), crosses BOTH Active->Escalating and
        // Escalating->Climax thresholds in a single AdvanceWorld tick.
        var result = await _sut.ApplyAsync(CreateContext(16, thread));

        var delta = Assert.Single(result.Deltas.OfType<PlotThreadProgress>());
        Assert.Equal(PlotThreadState.Climax, delta.NewState);
        Assert.Contains(result.NarrativeEvents, n => n.Contains("has escalated"));
        Assert.Contains(result.NarrativeEvents, n => n.Contains("CLIMAX"));
    }

    [Fact]
    public async Task ApplyAsync_LargeTensionJumpFromEscalating_ReachesClimaxDirectly()
    {
        var thread = new PlotThread
        {
            Id = "plot-threads/t3",
            Title = "War on the Horizon",
            State = PlotThreadState.Escalating,
            TensionLevel = 65
        };

        // +10/day * 2 days = 20 -> tension 85, crosses Escalating->Climax.
        var result = await _sut.ApplyAsync(CreateContext(2, thread));

        var delta = Assert.Single(result.Deltas.OfType<PlotThreadProgress>());
        Assert.Equal(PlotThreadState.Climax, delta.NewState);
        Assert.Contains(result.NarrativeEvents, n => n.Contains("CLIMAX"));
        Assert.DoesNotContain(result.NarrativeEvents, n => n.Contains("has escalated"));
    }
}
