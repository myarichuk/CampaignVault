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
}