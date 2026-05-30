using CampaignVault.Models;

namespace CampaignVault.Data;

/// <summary>
/// Deterministic, template-based behavioral synthesizer.
/// 
/// Produces short, useful descriptions like:
/// "Barliman is currently serving customers but looks exhausted. He is ravenous and his morale is slipping."
/// 
/// This keeps token usage low while giving the LLM DM immediate, actionable insight.
/// </summary>
public sealed class DefaultBehaviorSynthesizer : INpcBehaviorSynthesizer
{
    public string GenerateSummary(Character npc, CampaignTime? currentTime = null, IEnumerable<Event>? recentEvents = null)
    {
        if (npc?.Mind == null)
            return $"{npc?.Name ?? "The NPC"} has no available psychological data.";

        var mind = npc.Mind;
        var parts = new List<string>();

        // Current activity / location
        if (!string.IsNullOrWhiteSpace(npc.CurrentActivity))
        {
            parts.Add($"currently {npc.CurrentActivity}");
        }
        else if (npc.Schedule != null)
        {
            parts.Add($"at their default location ({npc.Schedule.DefaultLocationId})");
        }

        // Mood
        if (!string.IsNullOrWhiteSpace(mind.CurrentMood))
        {
            parts.Add($"mood: {mind.CurrentMood.ToLowerInvariant()}");
        }

        // Dominant needs
        var dominantNeeds = mind.Needs
            .Where(kv => kv.Value > NpcMoodThresholds.DominantNeedMin)
            .OrderByDescending(kv => kv.Value)
            .Take(2)
            .Select(kv => $"{kv.Key} ({kv.Value:F0})")
            .ToList();

        if (dominantNeeds.Count > 0)
        {
            parts.Add($"suffering from {string.Join(" and ", dominantNeeds)}");
        }

        // Morale / willpower signals
        if (mind.Morale < NpcMoodThresholds.LowMorale)
        {
            parts.Add("morale is low");
        }
        else if (mind.Morale > NpcMoodThresholds.HighMorale)
        {
            parts.Add("in good spirits");
        }

        // Recent events hint (very lightweight)
        var recentRelevant = recentEvents?
            .Where(e => e.Involved.Contains(npc.Id))
            .Take(1)
            .ToList();

        if (recentRelevant?.Count > 0)
        {
            parts.Add("recently involved in notable events");
        }

        // Wants / fears teaser (if present)
        if (mind.Wants.Count > 0)
        {
            parts.Add($"wants: {mind.Wants.First()}");
        }

        if (parts.Count == 0)
        {
            return $"{npc.Name} appears to be in a stable state.";
        }

        return $"{npc.Name} is {string.Join(", ", parts)}.";
    }
}
