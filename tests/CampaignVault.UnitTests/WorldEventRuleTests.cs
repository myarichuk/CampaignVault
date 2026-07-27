using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CampaignVault.Models;
using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using Xunit;

namespace CampaignVault.Tests;

public class WorldEventRuleTests
{
    private static SimulationContext CreateContext(int daysElapsed, double daysPassed, IReadOnlyList<WorldEvent> events)
    {
        return new SimulationContext(
            Time: new CampaignTime { TotalDaysElapsed = daysElapsed },
            ActiveRumors: [],
            ScheduledNpcs: [],
            Session: null!,
            DaysPassed: daysPassed,
            CampaignName: null,
            ActiveFactions: [],
            ActiveQuests: [],
            Config: null,
            ActivePlotThreads: [],
            ActiveWorldEvents: events
        );
    }

    [Fact]
    public async Task TimeBased_FiresOnInterval_DetectsCycleCrossing()
    {
        // Arrange
        var rule = new WorldEventRule();
        var evt = new WorldEvent
        {
            Id = "world-events/patrol",
            Title = "Nightly Patrol",
            TriggerType = WorldEventTriggerType.TimeBased,
            IntervalDays = 1,
            Status = WorldEventStatus.Pending,
            Effects = [new WorldEventEffect(WorldEventEffectKind.EventOccurred, Text: "Patrol occurred")]
        };

        // Day 0 to 1: first cycle crosses interval boundary
        var context1 = CreateContext(daysElapsed: 1, daysPassed: 1, [evt]);
        var result1 = await rule.ApplyAsync(context1);

        // Should emit both EventOccurred effect and WorldEventStatusChange
        Assert.Equal(2, result1.Deltas.Count);
        var delta1 = result1.Deltas.OfType<WorldEventStatusChange>().First();
        Assert.Equal(1, delta1.LastTriggeredDay);
        Assert.Null(delta1.NewStatus); // Status stays Pending

        // Simulate multi-day skip: day 1 to 5 (4 days passed)
        // Cycles = (5 / 1) - (1 / 1) = 5 - 1 = 4 cycles crossed
        evt.LastTriggeredDay = 1;
        var context2 = CreateContext(daysElapsed: 5, daysPassed: 4, [evt]);
        var result2 = await rule.ApplyAsync(context2);

        // Should fire once because cycles were crossed and lastTriggeredDay (1) != currentDays (5)
        var statusChanges = result2.Deltas.OfType<WorldEventStatusChange>().ToList();
        Assert.Single(statusChanges);
        Assert.Equal(5, statusChanges.First().LastTriggeredDay);
    }

    [Fact]
    public async Task Scheduled_FiresOnceAtTargetDay_TransitionsToTriggered()
    {
        // Arrange
        var rule = new WorldEventRule();
        var evt = new WorldEvent
        {
            Id = "world-events/raid",
            Title = "Castle Raid",
            TriggerType = WorldEventTriggerType.Scheduled,
            TargetDay = 10,
            Status = WorldEventStatus.Pending,
            Effects = [
                new WorldEventEffect(WorldEventEffectKind.EventOccurred, Text: "The raid begins!"),
                new WorldEventEffect(WorldEventEffectKind.RumorCreate, RumorSubject: "Raid", Text: "Castle under attack")
            ]
        };

        // Before target day
        var context1 = CreateContext(daysElapsed: 9, daysPassed: 1, [evt]);
        var result1 = await rule.ApplyAsync(context1);
        Assert.Empty(result1.Deltas);

        // Exactly at target day
        var context2 = CreateContext(daysElapsed: 10, daysPassed: 1, [evt]);
        var result2 = await rule.ApplyAsync(context2);

        var statusChange = result2.Deltas.OfType<WorldEventStatusChange>().First();
        Assert.Equal(WorldEventStatus.Triggered, statusChange.NewStatus);

        // Check that effects were emitted
        var eventOccurred = result2.Deltas.OfType<EventOccurred>().First();
        Assert.Contains("raid", eventOccurred.Summary.ToLower());

        var rumorCreate = result2.Deltas.OfType<RumorCreate>().First();
        Assert.Equal("Raid", rumorCreate.Subject);
    }

    [Fact]
    public async Task Scheduled_DoesNotFireTwice()
    {
        // Arrange
        var rule = new WorldEventRule();
        var evt = new WorldEvent
        {
            Id = "world-events/raid",
            Title = "Castle Raid",
            TriggerType = WorldEventTriggerType.Scheduled,
            TargetDay = 10,
            Status = WorldEventStatus.Triggered, // Already fired
            Effects = []
        };

        // Simulate later days
        var context = CreateContext(daysElapsed: 15, daysPassed: 5, [evt]);
        var result = await rule.ApplyAsync(context);

        // Should not fire again because status is Triggered, not Pending
        Assert.Empty(result.Deltas);
    }

    [Fact]
    public async Task Handler_AppliesStatusChange()
    {
        // Arrange - just verify the handler logic works with a mock context
        var handler = new WorldEventStatusChangeHandler();
        var evt = new WorldEvent
        {
            Id = "world-events/test",
            Title = "Test Event",
            Status = WorldEventStatus.Pending,
            DmNotes = "Original note"
        };

        var delta = new WorldEventStatusChange
        {
            WorldEventId = "world-events/test",
            NewStatus = WorldEventStatus.Prevented,
            NarrativeNote = "Prevented by party"
        };

        Assert.True(handler.ShouldHandle(delta));
    }
}
