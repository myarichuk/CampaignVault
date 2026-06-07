using System;
using System.Linq;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Models;
using Xunit;
using System.Collections.Generic;

namespace CampaignVault.Tests;

public class FactionEcosystemRuleTests
{
    [Fact]
    public async Task ApplyAsync_HighInfluenceFaction_ExpandsTerritory_ActuallyInfluenceShift()
    {
        // Arrange
        var rule = new FactionEcosystemRule(() => 0.0, max => max == 3 ? 2 : 0); // 0.0 forces action (0.0 < chanceToAct), 2 forces Influence shift
        
        var context = new SimulationContext(
            new CampaignTime { TotalDaysElapsed = 30 },
            new List<Rumor>(),
            new List<Character>(),
            null!,
            30, // 30 days passed
            "test-camp",
            new List<Faction>
            {
                new Faction { Id = "factions/1", Name = "Faction 1", InfluenceLevel = 100 },
                new Faction { Id = "factions/2", Name = "Faction 2", InfluenceLevel = 50 }
            },
            null
        );

        // Act
        var result = await rule.ApplyAsync(context);

        // Assert
        Assert.NotEmpty(result.Deltas);
        var stateChanges = result.Deltas.OfType<FactionStateChange>().ToList();
        Assert.Contains(stateChanges, sc => sc.InfluenceDelta > 0);
    }

    [Fact]
    public async Task ApplyAsync_HostileStance_TriggersWarEvent()
    {
        // Arrange
        // Force random to action 0 (Conflict)
        var rule = new FactionEcosystemRule(() => 0.0, _ => 0); 
        
        var context = new SimulationContext(
            new CampaignTime { TotalDaysElapsed = 30 },
            new List<Rumor>(),
            new List<Character>(),
            null!,
            30,
            "test-camp",
            new List<Faction>
            {
                new Faction 
                { 
                    Id = "factions/1", 
                    Name = "Faction 1", 
                    InfluenceLevel = 100,
                    StanceToward = new Dictionary<string, FactionStance> { { "factions/2", FactionStance.Hostile } },
                    Metadata = new Dictionary<string, string> { { "Domains", "urban" } }
                },
                new Faction 
                { 
                    Id = "factions/2", 
                    Name = "Faction 2", 
                    InfluenceLevel = 50,
                    Metadata = new Dictionary<string, string> { { "Domains", "urban" } }
                }
            },
            null
        );

        // Act
        var result = await rule.ApplyAsync(context);

        // Assert
        var stateChange = result.Deltas.OfType<FactionStateChange>().FirstOrDefault(c => c.FactionId == "factions/1");
        Assert.NotNull(stateChange);
        Assert.Equal(FactionStance.AtWar, stateChange.NewStance); // Escalate to AtWar

        var evt = result.Deltas.OfType<EventOccurred>().FirstOrDefault();
        Assert.NotNull(evt);
        Assert.Contains("AtWar", evt.Summary);
    }

    [Fact]
    public async Task ApplyAsync_DomainTags_PreventIllogicalExpansion()
    {
        // Arrange
        // Forcing action but 0.8 random makes non-overlapping domains fail (since 0.8 > 0.7)
        var rule = new FactionEcosystemRule(() => 0.8, _ => 0); 
        
        var context = new SimulationContext(
            new CampaignTime { TotalDaysElapsed = 30 },
            new List<Rumor>(),
            new List<Character>(),
            null!,
            30,
            "test-camp",
            new List<Faction>
            {
                new Faction 
                { 
                    Id = "factions/1", 
                    Name = "City Guard", 
                    InfluenceLevel = 100, // Will act because 0.8 < 1.0
                    Metadata = new Dictionary<string, string> { { "Domains", "urban" } }
                },
                new Faction 
                { 
                    Id = "factions/2", 
                    Name = "Mountain Orcs", 
                    InfluenceLevel = 100,
                    Metadata = new Dictionary<string, string> { { "Domains", "mountains, wilderness" } }
                }
            },
            null
        );

        // Act
        var result = await rule.ApplyAsync(context);

        // Assert
        // They should skip interaction because domains don't overlap and Random (0.8) >= 0.7
        Assert.Empty(result.Deltas);
    }
    [Fact]
    public async Task ApplyAsync_HostileStance_IncreasesEconomicDemand()
    {
        // Arrange
        var rule = new FactionEcosystemRule(() => 0.0, _ => 0); // Force Conflict
        
        var faction1 = new Faction 
        { 
            Id = "factions/1", 
            Name = "Faction 1", 
            InfluenceLevel = 100,
            EconomicDemand = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase) { ["Weapon"] = 1.0f, ["Armor"] = 1.0f }
        };
        var faction2 = new Faction 
        { 
            Id = "factions/2", 
            Name = "Faction 2", 
            InfluenceLevel = 50,
            EconomicDemand = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase) { ["Weapon"] = 1.0f, ["Armor"] = 1.0f }
        };
        
        var context = new SimulationContext(
            new CampaignTime { TotalDaysElapsed = 30 },
            new List<Rumor>(),
            new List<Character>(),
            null!,
            30,
            "test-camp",
            new List<Faction> { faction1, faction2 },
            null,
            new CampaignConfig { EconomicDemandDecayDays = 7 }
        );

        // Act
        var result = await rule.ApplyAsync(context);

        // Assert
        Assert.Equal(2.0f, faction1.EconomicDemand["Weapon"]);
        Assert.Equal(2.0f, faction1.EconomicDemand["Armor"]);
        Assert.Equal(2.0f, faction2.EconomicDemand["Weapon"]);
        Assert.Equal(2.0f, faction2.EconomicDemand["Armor"]);
    }

    [Fact]
    public async Task ApplyAsync_EconomicDemandDecay_BoundaryIndependentOfAdvanceGranularity()
    {
        var rule = new FactionEcosystemRule(() => 1.0, _ => 0); // never trigger faction actions

        async Task<float> AdvanceAndDecayAsync(int totalDays, int daysPerAdvance)
        {
            var faction1 = new Faction
            {
                Id = "factions/1",
                Name = "Faction 1",
                InfluenceLevel = 0,
                EconomicDemand = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase) { ["Weapon"] = 1.5f }
            };
            var faction2 = new Faction
            {
                Id = "factions/2",
                Name = "Faction 2",
                InfluenceLevel = 0,
                EconomicDemand = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase) { ["Weapon"] = 1.0f }
            };

            var elapsed = 0;
            while (elapsed < totalDays)
            {
                var step = Math.Min(daysPerAdvance, totalDays - elapsed);
                var context = new SimulationContext(
                    new CampaignTime { TotalDaysElapsed = elapsed + step },
                    new List<Rumor>(),
                    new List<Character>(),
                    null!,
                    step,
                    "test-camp",
                    new List<Faction> { faction1, faction2 },
                    null,
                    new CampaignConfig { EconomicDemandDecayDays = 7 }
                );

                await rule.ApplyAsync(context);
                elapsed += step;
            }

            return faction1.EconomicDemand["Weapon"];
        }

        var bulkDecay = await AdvanceAndDecayAsync(7, 7);
        var incrementalDecay = await AdvanceAndDecayAsync(7, 1);
        var noBoundaryDecay = await AdvanceAndDecayAsync(6, 1);

        Assert.Equal(bulkDecay, incrementalDecay);
        Assert.Equal(1.4f, bulkDecay);
        Assert.Equal(1.5f, noBoundaryDecay);
    }
}
