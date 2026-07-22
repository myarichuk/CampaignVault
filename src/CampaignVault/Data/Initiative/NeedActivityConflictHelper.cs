using CampaignVault.Models;

namespace CampaignVault.Data.Initiative;

internal static class NeedActivityConflictHelper
{
    private static readonly Dictionary<string, string[]> IncompatibleActivityKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["tiredness"] = ["duty", "guard", "tend", "serve", "patrol", "work"],
        ["hunger"] = ["cook", "serve", "feast", "bake"],
        ["thirst"] = ["speak", "perform", "preach", "negotiate"]
    };

    public static (bool HasConflict, string? Need, string? MatchedKeyword) Detect(Character npc, CampaignConfig config)
    {
        var needs = npc.Needs;
        if (needs?.ActivityConflictActive == true && !string.IsNullOrWhiteSpace(needs.ActivityConflictNeed))
        {
            return (true, needs.ActivityConflictNeed, null);
        }

        return EvaluateConflict(npc, config);
    }

    public static (bool HasConflict, string? Need, string? MatchedKeyword) EvaluateConflict(Character npc, CampaignConfig config)
    {
        var needs = npc.Needs;
        var activity = npc.CurrentActivity;
        if (string.IsNullOrWhiteSpace(activity))
        {
            return (false, null, null);
        }

        foreach (var (need, keywords) in IncompatibleActivityKeywords)
        {
            var value = needs?.ActiveNeeds.GetValueOrDefault(need, 0f) ?? 0f;
            if (value < config.NeedConflictThreshold)
            {
                continue;
            }

            var matched = keywords.FirstOrDefault(k => activity.Contains(k, StringComparison.OrdinalIgnoreCase));
            if (matched != null)
            {
                return (true, need, matched);
            }
        }

        return (false, null, null);
    }

    /// <summary>
    /// Broader than <see cref="EvaluateConflict"/>: surfaces the NPC's highest unaddressed need once it
    /// crosses threshold, regardless of whether the current activity keyword-clashes with it — an
    /// idle/leisure NPC (eating, resting, reading, chatting) whose thirst or tiredness is climbing
    /// should still register as an initiative candidate even though nothing about "reading a book"
    /// mechanically conflicts with being thirsty. Skips any need whose current activity already
    /// satisfies it (<see cref="NeedSatisfyingActivityKeywords"/>) — no point surfacing "wants to nap"
    /// for someone who is currently resting. Used only by the initiative pipeline for narrative color;
    /// never by <see cref="NeedConflictRule"/>, which stays duty-specific since it also flips mood to
    /// Exhausted.
    /// </summary>
    public static (bool HasWant, string? Need) EvaluateWant(Character npc, CampaignConfig config)
    {
        var needs = npc.Needs;
        if (needs is null || string.IsNullOrWhiteSpace(npc.CurrentActivity))
        {
            return (false, null);
        }

        var highest = needs.ActiveNeeds
            .Where(kv => kv.Value >= config.NeedConflictThreshold)
            .Where(kv => !NeedSatisfyingActivityKeywords.IsSatisfying(kv.Key, npc.CurrentActivity))
            .OrderByDescending(kv => kv.Value)
            .FirstOrDefault();

        return highest.Key != null ? (true, highest.Key) : (false, null);
    }
}