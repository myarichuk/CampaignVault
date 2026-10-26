using System.Linq;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>EventFollowUpAdvisor hints must stay short: they ride in every take_turn Summary that logs a conversation.</summary>
public class EventFollowUpAdvisorTokenTests
{
    [Fact]
    public void EventFollowUpAdvisor_DoesNotEchoLongSummaries()
    {
        var longSummary = string.Join(" ", Enumerable.Repeat("The barkeep leaned in and whispered about the cellar.", 8));
        var notes = EventFollowUpAdvisor.Apply(
        [
            new EventOccurred { Category = EventCategory.Conversation, Summary = longSummary, Involved = ["chars/a", "chars/b"] },
        ]);

        Assert.Equal(2, notes.Count);
        Assert.Contains(notes, n => n.Contains("no 'activity' commit found"));
        Assert.Contains(notes, n => n.Contains("no 'knowledge_update' commit found"));
        Assert.All(notes, n => Assert.True(n.Length < 220, n));
        Assert.All(notes, n => Assert.DoesNotContain(longSummary, n));
    }

    [Fact]
    public void EventFollowUpAdvisor_UsesEventIdWhenPresent()
    {
        var notes = EventFollowUpAdvisor.Apply(
        [
            new EventOccurred { Category = EventCategory.Discovery, Summary = "Found a trapdoor.", Involved = ["chars/a"], EventId = "events/trapdoor-1" },
        ]);

        Assert.All(notes, n => Assert.Contains("event 'events/trapdoor-1'", n));
    }
}
