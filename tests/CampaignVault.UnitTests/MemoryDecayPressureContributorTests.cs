using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CampaignVault.Data.Pressure;
using CampaignVault.Data.Pressure.Contributors;
using CampaignVault.Models;
using NSubstitute;
using Raven.Client.Documents.Session;
using Xunit;

namespace CampaignVault.Tests;

public class MemoryDecayPressureContributorTests
{
    [Fact]
    public async Task EvaluateAsync_IncludesSourceEventIds_WhenMemoryHasThem()
    {
        var memory = new MemoryNode
        {
            Topic = "Caravan disappearances",
            Details = "Three caravans vanished near Whispering Pass.",
            DayAcquired = 0,
            Importance = MemoryImportance.Important,
            SourceEventIds = ["events/valen-lirael-caravans"]
        };

        var result = await Evaluate(memory, currentDay: 41);
        var list = result.ToList();

        Assert.Single(list);
        Assert.Contains("events/valen-lirael-caravans", list[0].Text);
        Assert.Contains("recall_history", list[0].Text);
    }

    [Fact]
    public async Task EvaluateAsync_OmitsSourceEventNote_WhenMemoryHasNone()
    {
        var memory = new MemoryNode
        {
            Topic = "Caravan disappearances",
            Details = "Three caravans vanished near Whispering Pass.",
            DayAcquired = 0,
            Importance = MemoryImportance.Important
        };

        var result = await Evaluate(memory, currentDay: 41);
        var list = result.ToList();

        Assert.Single(list);
        Assert.DoesNotContain("recall_history", list[0].Text);
    }

    private static async Task<IEnumerable<WorldPressureItem>> Evaluate(MemoryNode memory, int currentDay)
    {
        var session = Substitute.For<IAsyncDocumentSession>();
        var npc = new NpcPresenceSummary(
            Id: "chars/npc1",
            Name: "Valen",
            CurrentActivity: "Standing at the bar",
            CurrentMood: "Wary",
            TopNeeds: new Dictionary<string, float>(),
            KnownNeeds: new Dictionary<string, float>(),
            NeedDescriptors: new Dictionary<string, string>(),
            Memories: new Dictionary<string, MemoryNode> { [memory.Topic] = memory });

        var scene = new SceneView
        {
            Location = LocationDetailView.From(new Location { Id = "locations/rusty-nail", Name = "Rusty Nail" }),
            PresentNPCs = [npc]
        };

        var ctx = new PressureContext(
            CampaignName: "test",
            Time: new CampaignTime { TotalDaysElapsed = currentDay },
            Config: new CampaignConfig(),
            Session: session,
            Scene: scene,
            RequestedLocationId: "locations/rusty-nail",
            PartyPresent: true);

        return await new MemoryDecayPressureContributor().EvaluateAsync(ctx);
    }
}
