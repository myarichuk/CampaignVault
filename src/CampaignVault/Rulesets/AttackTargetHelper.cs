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
    /// The target of each individual attack of a weapon attack. Fewer shots than targets hits the first N; more shots
    /// than targets (a burst at one target, a machine gun across a horde) fans out round-robin, one attack per shot.
    /// Saves and other one-roll-per-target actions keep <see cref="SelectTargets"/>.
    /// </summary>
    public static IReadOnlyList<string> SelectAttackInstances(RulesetAction action)
    {
        var distinctTargets = action.TargetIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return DistributeInstances(distinctTargets, ResolveAttackCount(action, distinctTargets.Count));
    }

    /// <summary>
    /// A note for a multi-target weapon attack that does not add up: targets the attack count silently leaves out, or (when the
    /// ruleset knows how many attacks one action grants) more attacks than the character gets from one action. Null when fine.
    /// </summary>
    public static string? MultiTargetWarning(RulesetAction action, int? attacksPerAction)
    {
        var distinct = action.TargetIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (distinct.Count < 2)
        {
            return null;
        }

        var count = ResolveAttackCount(action, distinct.Count);
        if (count < distinct.Count)
        {
            return $" [WARNING] {distinct.Count} targets listed but attackCount is {count}: {string.Join(", ", distinct.Skip(count))} "
                   + "were not attacked. Raise attackCount or trim targetIds.";
        }

        if (!HasExplicitCount(action) && attacksPerAction is { } allowed && count > allowed)
        {
            return $" [WARNING] {count} attacks were resolved (one per listed target) but this character makes {allowed} per Attack action. "
                   + "The extra attacks need Action Surge or another action; trim targetIds or set attackCount to what the action allows.";
        }

        return null;
    }

    /// <summary>True when the caller (not a weapon default) named the attack count.</summary>
    public static bool HasExplicitCount(RulesetAction action) =>
        TryGetIntParameter(action.Parameters, out var n, "attackCount", "shots", "rateOfFire", "attacks") && n > 0;

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