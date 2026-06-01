using CampaignVault.Data;
using CampaignVault.Models;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;

namespace CampaignVault.Tests;

public class StatusExpiryRuleTests
{
    [Fact]
    public async Task Removes_Round_Based_Effects_When_CurrentRound_Reached()
    {
        var rule = new StatusExpiryRule();

        var character = new Character
        {
            Id = "npcs/test-round",
            Name = "Test NPC",
            SystemStats = new SystemExtension
            {
                StatusEffects = new List<StatusEffect>
                {
                    new StatusEffect { Name = "Frightened", ExpiresAtRound = 5 },
                    new StatusEffect { Name = "Poisoned", ExpiresAtRound = 10 },
                    new StatusEffect { Name = "Blessed", ExpiresAtDay = 200 }
                }
            }
        };

        var context = new SimulationContext(
            Time: new CampaignTime { TotalDaysElapsed = 100 },
            ActiveRumors: new List<Rumor>(),
            ScheduledNpcs: new List<Character>(),
            Session: null!,
            DaysPassed: 0,
            CampaignName: "default",
            CurrentRound: 5
        );

        var result = await rule.ApplyAsync(context);

        Assert.Single(result.Deltas);
        var remove = Assert.IsType<StatusRemove>(result.Deltas[0]);
        Assert.Equal("Frightened", remove.Status);
        Assert.Contains(result.NarrativeEvents, n => n.Contains("Frightened"));
    }

    [Fact]
    public async Task Removes_Day_Based_Effects_When_TotalDaysElapsed_Reached()
    {
        var rule = new StatusExpiryRule();

        var character = new Character
        {
            Id = "npcs/test-day",
            Name = "Day NPC",
            SystemStats = new SystemExtension
            {
                StatusEffects = new List<StatusEffect>
                {
                    new StatusEffect { Name = "Cursed", ExpiresAtDay = 150 }
                }
            }
        };

        var context = new SimulationContext(
            Time: new CampaignTime { TotalDaysElapsed = 160 },
            ActiveRumors: new List<Rumor>(),
            ScheduledNpcs: new List<Character>(),
            Session: null!,
            DaysPassed: 10,
            CampaignName: "default",
            CurrentRound: 1
        );

        var result = await rule.ApplyAsync(context);

        Assert.Single(result.Deltas);
        var remove = Assert.IsType<StatusRemove>(result.Deltas[0]);
        Assert.Equal("Cursed", remove.Status);
    }
}