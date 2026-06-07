using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CampaignVault.Models;
using CampaignVault.Data.ChangeHandlers;

namespace CampaignVault.Data;

public class EncounterResolver
{
    private readonly Func<double> _nextDouble;

    public EncounterResolver() : this(() => Random.Shared.NextDouble()) { }

    public EncounterResolver(Func<double> nextDouble)
    {
        _nextDouble = nextDouble;
    }

    /// <summary>
    /// Evaluates an encounter roll loop for travel or resting.
    /// </summary>
    public async Task<(bool Interrupted, int HoursPassed, List<WorldChange> Deltas, List<string> Narratives)> EvaluateAsync(
        ChangeContext context,
        Character character,
        Location location,
        int totalHours,
        int bucketSizeHours,
        int userModifier,
        string contextType, // "Travel" or "Rest"
        string? terrain = null)
    {
        var deltas = new List<WorldChange>();
        var narratives = new List<string>();

        var buckets = (int)Math.Ceiling((double)totalHours / bucketSizeHours);

        var options = await context.GetSystemOptionsAsync();

        // 1. Calculate Base Chance
        var baseChance = GetBaseChance(location, options, contextType, terrain);

        // 2. Faction Bias
        if (!string.IsNullOrEmpty(location.ControllingFactionId))
        {
            baseChance -= 0.05; // -5% if patrolled/controlled
        }

        // 3. Location Danger Modifier (LLM narrative skew)
        // DangerModifier is -50 to +50. Let's say each point is 0.5% (same as user modifier).
        baseChance += (location.DangerModifier * 0.005);

        // 4. User Risk/Security Modifier
        var modifiedChance = baseChance + (userModifier * 0.005);

        // 5. Clamp
        modifiedChance = Math.Clamp(modifiedChance, 0.01, 0.90);

        var hoursPassed = 0;
        var interrupted = false;

        for (var i = 0; i < buckets; i++)
        {
            var hoursInBucket = Math.Min(bucketSizeHours, totalHours - hoursPassed);
            hoursPassed += hoursInBucket;

            if (_nextDouble() < modifiedChance)
            {
                interrupted = true;
                
                var encounterMsg = $"{contextType} interrupted after {hoursPassed} hours! An encounter has occurred.";
                narratives.Add(encounterMsg);

                deltas.Add(new EventOccurred
                {
                    Category = EventCategory.Simulation,
                    Summary = encounterMsg,
                    Involved = [character.Id, location.Id]
                });

                // Generate seed
                var category = RollEncounterCategory();
                var directive = $"ENGINE DIRECTIVE: Random Encounter triggered (Category: {category}). Do not default to combat! Look at the Location's flavor, the PC's current Needs, their Attributes, and their Reputation. Generate a highly contextual NPC/situation. Make it fit the scene.";
                
                if (!string.IsNullOrEmpty(location.ControllingFactionId))
                {
                    directive += $" NOTE: This area is controlled by faction '{location.ControllingFactionId}'.";
                }

                var transientId = $"chars/transient_encounter_{Guid.NewGuid().ToString("N").Substring(0, 6)}";
                deltas.Add(new CharacterCreate
                {
                    CharacterId = transientId,
                    Name = "Unknown Encounter Entity",
                    CurrentLocationId = location.Id,
                    KeepAlive = false, // GC will clean it up if party leaves
                    CurrentActivity = "Approaching the party...",
                    Notes = directive
                });

                deltas.Add(new ActivityChange
                {
                    CharacterId = character.Id,
                    NewActivity = "Dealing with an unexpected encounter."
                });

                break;
            }
        }

        return (interrupted, hoursPassed, deltas, narratives);
    }

    private double GetBaseChance(Location location, Dictionary<string, string> options, string contextType, string? terrain)
    {
        var key = $"{contextType}Encounter_{location.Type}";
        if (options.TryGetValue(key, out var valStr) && double.TryParse(valStr, out var val))
        {
            return val;
        }

        if (contextType == "Travel" && !string.IsNullOrEmpty(terrain))
        {
            var t = terrain.ToLowerInvariant();
            if (t.Contains("road") || t.Contains("plains") || t.Contains("safe"))
            {
                return 0.05;
            }

            if (t.Contains("wilderness") || t.Contains("forest") || t.Contains("hills"))
            {
                return 0.15;
            }

            if (t.Contains("mountain") || t.Contains("swamp") || t.Contains("underdark") || t.Contains("dangerous"))
            {
                return 0.25;
            }
        }

        // Defaults if not in SystemOptions and no terrain match
        if (location.Type == LocationType.Room || location.Type == LocationType.Building)
        {
            return 0.02;
        }

        if (location.Type == LocationType.Wilderness || location.Type == LocationType.Region)
        {
            return 0.15;
        }

        return 0.05; // City, Settlement, PointOfInterest, Default
    }

    private string RollEncounterCategory()
    {
        var roll = _nextDouble();
        if (roll < 0.50)
        {
            return "Danger / Threat";
        }

        if (roll < 0.75)
        {
            return "Social / Neutral";
        }

        if (roll < 0.90)
        {
            return "Opportunity / Boon";
        }

        return "Consequence / Reputation";
    }
}
