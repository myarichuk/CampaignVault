using System;
using System.Threading.Tasks;
using CampaignVault.Data.Pressure;
using CampaignVault.Data.Pressure.Contributors;
using CampaignVault.Models;
using Xunit;

namespace CampaignVault.Tests;

public class AmbientCrowdHeuristicsTests
{
    [Theory]
    [InlineData("Okolo 25 voennikov", 25)]
    [InlineData("8-15 rough sailors", 11)]
    [InlineData("A packed horde of goblins", 12)]
    [InlineData("A few locals", 2)]
    [InlineData("", 0)]
    public void EstimateImpliedCrowdSize_ParsesHints(string ambient, int expected)
    {
        var size = AmbientCrowdHeuristics.EstimateImpliedCrowdSize(ambient);
        if (expected == 11)
        {
            Assert.InRange(size, 10, 12);
        }
        else
        {
            Assert.Equal(expected, size);
        }
    }

    [Fact]
    public void EventImpliesUnanchoredBeat_DetectsSpearBeatWithoutNpc()
    {
        var ev = new Event
        {
            Summary = "Someone in the back picks up a spear and steps forward.",
            Involved = ["chars/valen", "locations/harluaa/training-hall"]
        };

        Assert.True(AmbientCrowdHeuristics.EventImpliesUnanchoredBeat(ev, "locations/harluaa/training-hall"));
    }

    [Fact]
    public void EventImpliesUnanchoredBeat_IgnoresWhenNpcAnchored()
    {
        var ev = new Event
        {
            Summary = "The sergeant picks up a spear.",
            Involved = ["chars/valen", "chars/sergeant", "locations/harluaa/training-hall"]
        };

        Assert.False(AmbientCrowdHeuristics.EventImpliesUnanchoredBeat(ev, "locations/harluaa/training-hall"));
    }
}

public class AmbientCrowdPressureContributorTests
{
    [Fact]
    public async Task Evaluate_SparseCrowd_WhenManyImpliedButFewPresent()
    {
        var contributor = new AmbientCrowdPressureContributor();
        var scene = new SceneView
        {
            IsLocationAnchored = true,
            Location = LocationDetailView.From(new Location
            {
                Id = "locations/training-hall",
                Name = "Training Hall",
                AmbientCrowd = "About 25 warriors and mercenaries training"
            }),
            PresentNPCs =
            [
                new NpcPresenceSummary(
                    Id: "chars/sergeant",
                    Name: "Sergeant",
                    CurrentActivity: "Watching",
                    CurrentMood: null,
                    KnownNeeds: [],
                    NeedDescriptors: [])
            ],
            RecentEvents = []
        };

        var pressures = await contributor.EvaluateAsync(new PressureContext(
            CampaignName: "test",
            Time: new CampaignTime { TotalDaysElapsed = 5 },
            Config: new CampaignConfig(),
            Session: null!,
            Scene: scene));

        Assert.Contains(pressures, p =>
            p.GroupingKey == AmbientCrowdPressureContributor.SparseCrowdGroupingKey
            && p.Text.Contains("only 1 NPC", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Evaluate_NagsEmptyAmbientCrowd_WhenNoPointsOfInterestEither()
    {
        var contributor = new AmbientCrowdPressureContributor();
        var scene = new SceneView
        {
            IsLocationAnchored = true,
            Location = LocationDetailView.From(new Location
            {
                Id = "locations/driftwood-tavern",
                Name = "The Driftwood Tavern",
                AmbientCrowd = null,
                PointsOfInterest = []
            }),
            PresentNPCs = [],
            RecentEvents = []
        };

        var pressures = await contributor.EvaluateAsync(new PressureContext(
            CampaignName: "test",
            Time: new CampaignTime(),
            Config: new CampaignConfig(),
            Session: null!,
            Scene: scene));

        Assert.Contains(pressures, p =>
            p.GroupingKey == AmbientCrowdPressureContributor.SparseCrowdGroupingKey
            && p.Text.Contains("AmbientCrowd", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Evaluate_SkipsEmptyAmbientCrowdNag_WhenPointsOfInterestPresent()
    {
        var contributor = new AmbientCrowdPressureContributor();
        var scene = new SceneView
        {
            IsLocationAnchored = true,
            Location = LocationDetailView.From(new Location
            {
                Id = "locations/quiet-study",
                Name = "Quiet Study",
                AmbientCrowd = null,
                PointsOfInterest = ["A cluttered writing desk"]
            }),
            PresentNPCs = [],
            RecentEvents = []
        };

        var pressures = await contributor.EvaluateAsync(new PressureContext(
            CampaignName: "test",
            Time: new CampaignTime(),
            Config: new CampaignConfig(),
            Session: null!,
            Scene: scene));

        Assert.DoesNotContain(pressures, p => p.GroupingKey == AmbientCrowdPressureContributor.SparseCrowdGroupingKey);
    }

    [Fact]
    public async Task Evaluate_UnanchoredBeat_FromRecentEvent()
    {
        var contributor = new AmbientCrowdPressureContributor();
        var scene = new SceneView
        {
            IsLocationAnchored = true,
            Location = LocationDetailView.From(new Location
            {
                Id = "locations/tavern",
                Name = "Tavern",
                AmbientCrowd = "8-15 locals nursing drinks"
            }),
            PresentNPCs = [],
            RecentEvents =
            [
                new Event
                {
                    Timestamp = DateTime.UtcNow,
                    Summary = "A drunk stumbles toward the party from the bar.",
                    Involved = ["chars/pc", "locations/tavern"]
                }
            ]
        };

        var pressures = await contributor.EvaluateAsync(new PressureContext(
            CampaignName: "test",
            Time: new CampaignTime(),
            Config: new CampaignConfig(),
            Session: null!,
            Scene: scene));

        Assert.Contains(pressures, p =>
            p.GroupingKey == AmbientCrowdPressureContributor.UnanchoredBeatGroupingKey
            && p.Text.Contains("world_build", StringComparison.OrdinalIgnoreCase));
    }
}
