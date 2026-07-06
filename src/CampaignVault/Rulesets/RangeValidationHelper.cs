using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;

namespace CampaignVault.Rulesets;

/// <summary>
/// Validates that attack/spell targets are within the declared range or AoE radius.
/// Uses pairwise SpatialPosition distance bands (Touch/Close/Near/Far/Distant) for comparison.
/// Permissive by default: if no spatial position is recorded for a pair, validation passes.
/// Range enforcement is opt-in per-campaign via SpatialPositionChange commits and weapon properties.
/// </summary>
internal static class RangeValidationHelper
{
    private static readonly string[] BandOrder =
    [
        SpatialDistanceBand.Touch,
        SpatialDistanceBand.Close,
        SpatialDistanceBand.Near,
        SpatialDistanceBand.Far,
        SpatialDistanceBand.Distant
    ];

    /// <summary>
    /// Validates that all targets of an attack/spell are within range.
    /// Only gates ActionType.Attack and ActionType.Spell.
    /// </summary>
    /// <param name="action">The RulesetAction being validated.</param>
    /// <param name="context">The ChangeContext with loaded Character entities.</param>
    /// <param name="errorReason">On failure, a narrative-friendly error message.</param>
    /// <returns>True if validation passes (or does not apply); false if a target is out of range.</returns>
    public static bool Validate(RulesetAction action, ChangeContext context, out string? errorReason)
    {
        errorReason = null;

        if (action.ActionType != RulesetActionType.Attack && action.ActionType != RulesetActionType.Spell)
        {
            return true;
        }

        var maxBand = action.Parameters.TryGetValue("aoeRadius", out var aoeBand)
            ? aoeBand
            : (action.Parameters.TryGetValue("range", out var singleBand) ? singleBand : null);

        if (maxBand is null)
        {
            return true;
        }

        var originId = action.Parameters.TryGetValue("originId", out var origin) ? origin : action.CharacterId;

        if (!context.Characters.TryGetValue(originId, out var originCharacter))
        {
            return true;
        }

        var maxRank = Array.IndexOf(BandOrder, maxBand);
        if (maxRank < 0)
        {
            return true;
        }

        foreach (var targetId in action.TargetIds)
        {
            var pos = originCharacter.SystemStats?.SpatialPositions?.FirstOrDefault(p => p.TargetId == targetId);

            if (pos is null)
            {
                if (context.Characters.TryGetValue(targetId, out var targetCharacter) && targetCharacter?.SystemStats?.SpatialPositions != null)
                {
                    pos = targetCharacter.SystemStats.SpatialPositions.FirstOrDefault(p => p.TargetId == originId);
                }
            }

            if (pos is null)
            {
                continue;
            }

            var targetRank = Array.IndexOf(BandOrder, pos.DistanceBand);
            if (targetRank < 0)
            {
                continue;
            }

            if (targetRank > maxRank)
            {
                errorReason = $"Target '{targetId}' is {pos.DistanceBand} from {originId}, out of {maxBand} range.";
                return false;
            }
        }

        return true;
    }
}
