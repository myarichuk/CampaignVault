using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using CampaignVault.Models;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>
/// P3 (Round 4 item 7): get_entity NPC recentInteractions ran 3.7–4.2k chars
/// for 10 full events. The wire projection is now capped at
/// <see cref="EventSummaryView.NpcContextCap"/> entries with the involved list
/// and default importance stripped (<see cref="EventSummaryView.ForNpcContext"/>).
/// Fixture-free: pure projection + serialization size.
/// </summary>
public class NpcContextBudgetTests(ITestOutputHelper output)
{
    private static Event MakeEvent(int i) => new()
    {
        Id = $"events/00000000-0000-4000-8000-{i:000000000000}",
        Summary = $"Interaction number {i}: a long conversation beat about the harbor ledger and the missing tide charts. ",
        Category = EventCategory.Conversation,
        Involved = ["chars/npc", "chars/pc", "chars/companion", "chars/bystander"],
        LocationId = "locations/harbor",
        DayLogged = 12,
        Importance = MemoryImportance.Important,
        EmotionalBeat = "trust",
    };

    private static List<EventSummaryView> Project(IEnumerable<Event> events) =>
        [.. events.Take(EventSummaryView.NpcContextCap).Select(EventSummaryView.ForNpcContext)];

    [Fact]
    public void Projection_IsCappedAtFive()
    {
        var projected = Project(Enumerable.Range(0, 8).Select(MakeEvent));

        Assert.Equal(5, projected.Count);
    }

    [Fact]
    public void Projection_DropsInvolvedAndDefaultImportance_KeepsId()
    {
        var view = EventSummaryView.ForNpcContext(MakeEvent(0));

        Assert.Equal("events/00000000-0000-4000-8000-000000000000", view.Id);
        Assert.Empty(view.Involved);
        Assert.Null(view.Importance);
        Assert.Equal("locations/harbor", view.LocationId);
        Assert.Contains("harbor ledger", view.Summary);
    }

    [Fact]
    public void Projection_PreservesNonDefaultImportance()
    {
        var ev = MakeEvent(0);
        ev.Importance = MemoryImportance.Core;
        var view = EventSummaryView.ForNpcContext(ev);

        Assert.Equal(MemoryImportance.Core, view.Importance);
    }

    [Fact]
    public void ProjectedList_StaysWithinCharBudget_AndBeatsUncappedFrom()
    {
        var events = Enumerable.Range(0, 8).Select(MakeEvent).ToList();

        var oldWire = events.Select(EventSummaryView.From).ToList();
        var newWire = Project(events);

        var oldJson = JsonSerializer.Serialize(oldWire);
        var newJson = JsonSerializer.Serialize(newWire);
        output.WriteLine($"recentInteractions: old {oldJson.Length} chars -> new {newJson.Length} chars");

        Assert.True(newJson.Length < oldJson.Length,
            $"capped projection ({newJson.Length} chars) should beat uncapped From ({oldJson.Length} chars)");
        Assert.True(newJson.Length <= 2_500,
            $"5 capped NPC interactions took {newJson.Length} chars; budget 2500.");
    }
}
