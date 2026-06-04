using System.Linq;
using CampaignVault.Data;
using CampaignVault.Models;
using Xunit;

namespace CampaignVault.Tests;

public class TravelEncounterRuleTests
{
    [Fact]
    public void EvaluateTravel_WithRoadTerrain_SkipsEncounter()
    {
        // Arrange
        // Force random to 0.10. Road terrain has 5% base chance. 0.10 > 0.05, so no encounter.
        var rule = new TravelEncounterRule(() => 0.10);
        var character = new Character { Id = "chars/1", Name = "Test" };
        var loc = new Location { Id = "loc/2", Name = "Dest" };

        // Act
        var result = rule.EvaluateTravel(character, loc, 12, "road", 0);

        // Assert
        Assert.False(result.Interrupted);
        Assert.Equal(12, result.HoursTraveled); // 2 buckets (6 + 6)
        Assert.Empty(result.Deltas);
    }

    [Fact]
    public void EvaluateTravel_EncounterTriggered_InterruptsTravelAndEmitsMarker()
    {
        // Arrange
        // Force random to 0.02. Wilderness has 15% base. 0.02 < 0.15, so encounter on FIRST bucket.
        var rule = new TravelEncounterRule(() => 0.02);
        var character = new Character { Id = "chars/1", Name = "Test" };
        var loc = new Location { Id = "loc/2", Name = "Dest" };

        // Act
        var result = rule.EvaluateTravel(character, loc, 12, "wilderness", 0);

        // Assert
        Assert.True(result.Interrupted);
        Assert.Equal(6, result.HoursTraveled); // Interrupted after first 6-hour bucket
        Assert.NotEmpty(result.Deltas);

        var eventOccurred = result.Deltas.OfType<EventOccurred>().FirstOrDefault();
        Assert.NotNull(eventOccurred);
        Assert.Contains("Travel interrupted", eventOccurred.Summary);

        var activityChange = result.Deltas.OfType<ActivityChange>().FirstOrDefault();
        Assert.NotNull(activityChange);
        Assert.Contains("Resolve the encounter", activityChange.NewActivity);
        Assert.False(activityChange.UpdateLocation); // Location is not updated
    }

    [Fact]
    public void EvaluateTravel_WithCautiousTravelAndSmallGroup_ReducesEncounterChance()
    {
        // Arrange
        // Force random to 0.10. Wilderness is 15% base. 
        // We apply a stealth modifier of -20 (reduces chance by 10%).
        // New chance = 15% - 10% = 5%. 
        // 0.10 is NOT less than 0.05, so no encounter! (If modifier wasn't applied, it WOULD trigger).
        var rule = new TravelEncounterRule(() => 0.10);
        var character = new Character { Id = "chars/1", Name = "Test" };
        var loc = new Location { Id = "loc/2", Name = "Dest" };

        // Act
        var result = rule.EvaluateTravel(character, loc, 6, "wilderness", -20);

        // Assert
        Assert.False(result.Interrupted);
        Assert.Empty(result.Deltas);
    }

    [Fact]
    public void EvaluateTravel_WithLoudCaravan_IncreasesEncounterChance()
    {
        // Arrange
        // Force random to 0.10. Road is 5% base.
        // We apply modifier of +20 (increases chance by 10%).
        // New chance = 5% + 10% = 15%.
        // 0.10 < 0.15, so it WILL trigger.
        var rule = new TravelEncounterRule(() => 0.10);
        var character = new Character { Id = "chars/1", Name = "Test" };
        var loc = new Location { Id = "loc/2", Name = "Dest" };

        // Act
        var result = rule.EvaluateTravel(character, loc, 6, "road", 20);

        // Assert
        Assert.True(result.Interrupted);
        Assert.NotEmpty(result.Deltas);
    }
}
