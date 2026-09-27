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
        return [.. distinctTargets.Take(attackCount)];
    }

    /// <summary>
    /// Distributes N independently-resolved damage instances (Magic Missile darts, Scorching Ray
    /// rays) across the listed targets, round-robin: 3 targets + 3 instances → 1 each, 1 target +
    /// 3 instances → all 3 at that target, 2 targets + 3 instances → a 2/1 split. Unlike
    /// SelectTargets (which caps at the listed target count), the result can hold more entries
    /// than targets listed. Returns one entry per instance, in resolution order.
    /// </summary>
    public static IReadOnlyList<string> DistributeInstances(IReadOnlyList<string> targetIds, int instanceCount)
    {
        var distinctTargets = targetIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (distinctTargets.Count == 0 || instanceCount <= 0)
        {
            return [];
        }

        var distributed = new List<string>(instanceCount);
        for (var i = 0; i < instanceCount; i++)
        {
            distributed.Add(distinctTargets[i % distinctTargets.Count]);
        }

        return distributed;
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