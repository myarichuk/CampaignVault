using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CampaignVault.Models;

namespace CampaignVault.Data;

public class FactionEcosystemRule : ISimulationRule
{
    public string Name => "Faction Ecosystem";
    public int Order => 40;

    private readonly Func<double> _nextDouble;
    private readonly Func<int, int> _nextInt;

    public FactionEcosystemRule() : this(() => Random.Shared.NextDouble(), max => Random.Shared.Next(max)) { }

    public FactionEcosystemRule(Func<double> nextDouble, Func<int, int> nextInt)
    {
        _nextDouble = nextDouble;
        _nextInt = nextInt;
    }

    public virtual Task<RuleResult> ApplyAsync(SimulationContext context, CancellationToken ct = default)
    {
        var narratives = new List<string>();
        var deltas = new List<WorldChange>();

        if (context.ActiveFactions == null || context.ActiveFactions.Count < 2)
        {
            return Task.FromResult(new RuleResult(narratives, deltas));
        }

        var decayInterval = context.Config?.EconomicDemandDecayDays ?? 7;
        var currentDays = (int)context.Time.TotalDaysElapsed;
        var previousDays = currentDays - (int)context.DaysPassed;
        var decayCycles = (currentDays / decayInterval) - (previousDays / decayInterval);

        // Apply economic decay toward 1.0 for all factions before actions
        foreach (var faction in context.ActiveFactions)
        {
            foreach (var key in faction.EconomicDemand.Keys.ToList())
            {
                var diff = faction.EconomicDemand[key] - 1.0f;
                if (Math.Abs(diff) > 0.01f)
                {
                    faction.EconomicDemand[key] -= Math.Sign(diff) * 0.1f * decayCycles;
                    if (Math.Abs(faction.EconomicDemand[key] - 1.0f) < 0.05f)
                    {
                        faction.EconomicDemand[key] = 1.0f;
                    }
                }
            }
        }

        foreach (var faction in context.ActiveFactions)
        {
            // Base chance to act: 1% per point of Influence, max 80%, rolled over 30 days
            // So on a single day, the chance is small.
            // If DaysPassed is e.g. 5, chance increases.
            var chanceToAct = (faction.InfluenceLevel / 100.0) * (context.DaysPassed / 30.0);
            
            // Fast clamp: never higher than 80%
            if (chanceToAct > 0.8)
            {
                chanceToAct = 0.8;
            }

            if (_nextDouble() < chanceToAct)
            {
                // Faction takes an action!
                // Pick a target faction that is not itself
                var targets = context.ActiveFactions.Where(f => f.Id != faction.Id).ToList();
                if (!targets.Any())
                {
                    continue;
                }

                var target = targets[_nextInt(targets.Count)];

                // Check Domain logic (e.g. if one is urban and another is deep_underdark, maybe skip)
                // For now, simple domains overlap check (if they both have Domains metadata, check intersection)
                faction.Metadata.TryGetValue("Domains", out var domainsA);
                target.Metadata.TryGetValue("Domains", out var domainsB);
                
                var domainsOverlap = true;
                if (!string.IsNullOrWhiteSpace(domainsA) && !string.IsNullOrWhiteSpace(domainsB))
                {
                    var tagsA = domainsA.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    var tagsB = domainsB.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    domainsOverlap = tagsA.Intersect(tagsB, StringComparer.OrdinalIgnoreCase).Any();
                }

                // If domains don't overlap, less chance of conflict/interaction, but maybe they do trade.
                if (!domainsOverlap && _nextDouble() < 0.7)
                {
                    continue; // Skip interaction
                }

                // Decide on action: 0 = Hostility/War, 1 = Diplomacy/Trade, 2 = Influence gain/loss
                var action = _nextInt(3);

                if (action == 0) // Conflict
                {
                    var newStance = FactionStance.Hostile;
                    if (faction.StanceToward.TryGetValue(target.Id, out var currentStance) && currentStance == FactionStance.Hostile)
                    {
                        newStance = FactionStance.AtWar; // Escalate
                    }

                    deltas.Add(new FactionStateChange
                    {
                        FactionId = faction.Id,
                        TargetFactionId = target.Id,
                        NewStance = newStance,
                        Narrative = $"{faction.Name} became {newStance} toward {target.Name}."
                    });

                    var eventSummary = $"{faction.Name} has declared {newStance} against {target.Name}.";
                    deltas.Add(new EventOccurred
                    {
                        Category = EventCategory.Simulation,
                        Summary = eventSummary,
                        Involved = [faction.Id, target.Id]
                    });

                    deltas.Add(new RumorCreate
                    {
                        RumorId = $"rumors/faction_{faction.Id.Split('/').LastOrDefault()}_{context.Time.TotalDaysElapsed}_{Guid.NewGuid().ToString("N")[..4]}",
                        Subject = $"{faction.Name} Conflict",
                        Text = $"Rumor is spreading that {eventSummary}"
                    });

                    // Economic impact of war
                    faction.EconomicDemand["Weapon"] = faction.EconomicDemand.GetValueOrDefault("Weapon", 1.0f) + 0.5f;
                    faction.EconomicDemand["Armor"] = faction.EconomicDemand.GetValueOrDefault("Armor", 1.0f) + 0.5f;
                    target.EconomicDemand["Weapon"] = target.EconomicDemand.GetValueOrDefault("Weapon", 1.0f) + 0.5f;
                    target.EconomicDemand["Armor"] = target.EconomicDemand.GetValueOrDefault("Armor", 1.0f) + 0.5f;

                    narratives.Add($"Faction simulation: {eventSummary}");
                }
                else if (action == 1) // Diplomacy
                {
                    deltas.Add(new FactionStateChange
                    {
                        FactionId = faction.Id,
                        TargetFactionId = target.Id,
                        NewStance = FactionStance.Allied,
                        Narrative = $"{faction.Name} formed an alliance with {target.Name}."
                    });

                    var eventSummary = $"{faction.Name} and {target.Name} have entered a formal alliance.";
                    deltas.Add(new EventOccurred
                    {
                        Category = EventCategory.Simulation,
                        Summary = eventSummary,
                        Involved = [faction.Id, target.Id]
                    });

                    deltas.Add(new RumorCreate
                    {
                        // Use a GUID fragment to avoid collisions if multiple events happen on the same day
                        RumorId = $"rumors/faction_{faction.Id.Split('/').LastOrDefault()}_{context.Time.TotalDaysElapsed}_{Guid.NewGuid().ToString("N")[..4]}",
                        Subject = $"{faction.Name} Alliance",
                        Text = $"Rumor is spreading that {eventSummary}"
                    });

                    narratives.Add($"Faction simulation: {eventSummary}");
                }
                else // Influence shift
                {
                    // For influence delta, we'll use _nextInt to get a value between 0 and 9, then add 1.
                    var influenceDelta = _nextInt(9) + 1;
                    var eventSummary = $"{faction.Name} grew in influence (+{influenceDelta}).";
                    deltas.Add(new FactionStateChange
                    {
                        FactionId = faction.Id,
                        InfluenceDelta = influenceDelta,
                        Narrative = eventSummary
                    });
                    
                    deltas.Add(new EventOccurred
                    {
                        Category = EventCategory.Simulation,
                        Summary = eventSummary,
                        Involved = [faction.Id]
                    });

                    deltas.Add(new RumorCreate
                    {
                        RumorId = $"rumors/faction_{faction.Id.Split('/').LastOrDefault()}_{context.Time.TotalDaysElapsed}_{Guid.NewGuid().ToString("N")[..4]}",
                        Subject = $"{faction.Name} Influence",
                        Text = $"Rumor has it that {eventSummary}"
                    });
                    
                    narratives.Add($"Faction simulation: {eventSummary}");
                }
            }
        }

        return Task.FromResult(new RuleResult(narratives, deltas));
    }
}
