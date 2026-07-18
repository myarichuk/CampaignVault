using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Data.Pressure;
using CampaignVault.Models;

namespace CampaignVault.Data;

public class EncounterResolver
{
    private readonly Func<double> _nextDouble;

    public EncounterResolver() : this(() => Random.Shared.NextDouble())
    {
    }

    public EncounterResolver(Func<double> nextDouble)
    {
        _nextDouble = nextDouble;
    }

    /// <summary>
    /// Evaluates an encounter roll loop for travel or resting.
    /// </summary>
    public async Task<(bool Interrupted, int HoursPassed, List<WorldChange> Deltas, List<string> Narratives)>
        EvaluateAsync(
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
                    Involved = [character.Id],
                    LocationId = location.Id
                });

                // Generate seed
                var category = RollEncounterCategory();
                var directive =
                    $"ENGINE DIRECTIVE: Random Encounter triggered (Category: {category}). Do not default to combat! Look at the Location's flavor, the PC's current Needs, their Attributes, and their Reputation. Generate a highly contextual NPC/situation. Make it fit the scene.";

                if (!string.IsNullOrEmpty(location.ControllingFactionId))
                {
                    directive += $" NOTE: This area is controlled by faction '{location.ControllingFactionId}'.";
                }

                var transientId = $"chars/transient_encounter_{Guid.NewGuid().ToString("N")[..6]}";
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

    /// <summary>
    /// Single-roll crowd interrupt check for in-scene beats (Phase B).
    /// </summary>
    public async Task<(bool Interrupted, List<WorldChange> Deltas, List<string> Narratives)>
        EvaluateSceneInterruptAsync(
            ChangeContext context,
            Character character,
            Location location,
            int riskModifier,
            int contextModifier = 0,
            string? notes = null)
    {
        var deltas = new List<WorldChange>();
        var narratives = new List<string>();
        var options = await context.GetSystemOptionsAsync();

        var baseChance = GetBaseChance(location, options, "Scene", null);

        if (!string.IsNullOrWhiteSpace(location.AmbientCrowd)
            && AmbientCrowdHeuristics.IsCrowdDenseEnough(location.AmbientCrowd))
        {
            baseChance += 0.03;
        }

        if (!string.IsNullOrEmpty(location.ControllingFactionId))
        {
            baseChance -= 0.02;
        }

        baseChance += location.DangerModifier * 0.005;
        var modifiedChance = baseChance + ((riskModifier + contextModifier) * 0.005);
        modifiedChance = Math.Clamp(modifiedChance, 0.01, 0.75);

        if (_nextDouble() >= modifiedChance)
        {
            narratives.Add("No crowd interrupt triggered this beat.");
            return (false, deltas, narratives);
        }

        var category = RollEncounterCategory();
        var directive =
            $"ENGINE DIRECTIVE: Scene interrupt from ambient crowd (Category: {category}). "
            + "Promote ONE figure from the crowd — pickpocket, zealot, drunk, merc, witness, etc. "
            + "Do not spawn the whole crowd or start full combat unless the party escalates. "
            + "Tie behavior to location flavor and the PC's visual tags/appearance.";

        if (!string.IsNullOrWhiteSpace(location.AmbientCrowd))
        {
            directive += $" Crowd: '{location.AmbientCrowd}'.";
        }

        if (!string.IsNullOrWhiteSpace(notes))
        {
            directive += $" Beat notes: {notes}.";
        }

        if (character.VisualTags.Count > 0)
        {
            directive += $" PC tags: {string.Join(", ", character.VisualTags)}.";
        }

        if (!string.IsNullOrWhiteSpace(character.CurrentAppearance))
        {
            directive += $" PC appearance: {character.CurrentAppearance}.";
        }

        var interruptMsg =
            $"Someone from the crowd at {location.Name} interrupts the scene ({category}).";
        narratives.Add(interruptMsg);

        deltas.Add(new EventOccurred
        {
            Category = EventCategory.SceneInterrupt,
            Summary = interruptMsg,
            Involved = [character.Id],
            LocationId = location.Id
        });

        var transientId = $"chars/crowd_interrupt_{Guid.NewGuid().ToString("N")[..6]}";
        deltas.Add(new CharacterCreate
        {
            CharacterId = transientId,
            Name = "Figure from the Crowd",
            CurrentLocationId = location.Id,
            KeepAlive = false,
            CurrentActivity = "Stepping out of the crowd...",
            Notes = directive
        });

        deltas.Add(new ActivityChange
        {
            CharacterId = character.Id,
            NewActivity = "Reacting to someone stepping out of the crowd."
        });

        return (true, deltas, narratives);
    }

    private double GetBaseChance(Location location, Dictionary<string, string> options, string contextType,
        string? terrain)
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

        if (contextType == "Scene")
        {
            return location.Type switch
            {
                LocationType.Room or LocationType.Building => 0.08,
                LocationType.Settlement or LocationType.District => 0.10,
                LocationType.Wilderness or LocationType.Region => 0.06,
                _ => 0.05
            };
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