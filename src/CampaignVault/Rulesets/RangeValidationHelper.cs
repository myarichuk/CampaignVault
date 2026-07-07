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

    // Case-insensitive: LLM-supplied band strings (e.g. "near" vs "Near") should still match.
    private static readonly Dictionary<string, int> BandRank =
        BandOrder.Select((band, index) => (band, index))
                 .ToDictionary(x => x.band, x => x.index, StringComparer.OrdinalIgnoreCase);

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

        if (!BandRank.TryGetValue(maxBand, out var maxRank))
        {
            return true;
        }

        var originPositions = new Dictionary<string, SpatialPosition>();
        if (originCharacter.SystemStats?.SpatialPositions != null)
        {
            foreach (var p in originCharacter.SystemStats.SpatialPositions)
            {
                originPositions.TryAdd(p.TargetId, p);
            }
        }

        foreach (var targetId in action.TargetIds)
        {
            SpatialPosition? pos = originPositions.TryGetValue(targetId, out var originPos) ? originPos : null;

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

            if (!BandRank.TryGetValue(pos.DistanceBand, out var targetRank))
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
