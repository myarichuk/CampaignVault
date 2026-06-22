using CampaignVault.Models;

namespace CampaignVault.Rulesets;

/// <summary>
/// Selects attack targets for multi-shot / multi-target ruleset_action attacks.
/// </summary>
internal static class AttackTargetHelper
{
    public static IReadOnlyList<string> SelectTargets(RulesetAction action)
    {
        if (action.TargetIds.Count == 0)
        {
            return [];
        }

        var distinctTargets = action.TargetIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var attackCount = ResolveAttackCount(action, distinctTargets.Count);
        return distinctTargets.Take(attackCount).ToList();
    }

    public static int ResolveAttackCount(RulesetAction action, int listedTargetCount)
    {
        if (TryGetIntParameter(action.Parameters, out var explicitCount, "attackCount", "shots", "rateOfFire", "attacks")
            && explicitCount > 0)
        {
            return explicitCount;
        }

        if (listedTargetCount > 1)
        {
            return listedTargetCount;
        }

        return 1;
    }

    private static bool TryGetIntParameter(
        IReadOnlyDictionary<string, string> parameters,
        out int value,
        params string[] keys)
    {
        foreach (var key in keys)
        {
            if (parameters.TryGetValue(key, out var raw) && int.TryParse(raw, out value))
            {
                return true;
            }
        }

        value = 0;
        return false;
    }
}