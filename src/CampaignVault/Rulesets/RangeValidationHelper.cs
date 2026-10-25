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

    /// <summary>What a range check found: a refusal, or a disadvantage the attack takes (long range, a ranged attack with a hostile within Touch).</summary>
    /// <summary><paramref name="BandsBeyond"/>: how many distance bands past normal range the farthest target is (PF2e's per-increment penalty).</summary>
    internal readonly record struct RangeOutcome(string? Error, string? DisadvantageReason, int BandsBeyond = 0);

    /// <summary>
    /// Validates that all targets of an attack/spell are within range.
    /// Only gates ActionType.Attack and ActionType.Spell.
    /// </summary>
    /// <param name="action">The RulesetAction being validated.</param>
    /// <param name="context">The ChangeContext with loaded Character entities.</param>
    /// <param name="errorReason">On failure, a narrative-friendly error message.</param>
    /// <returns>True if validation passes (or does not apply); false if a target is out of range.</returns>
    public static bool Validate(RulesetAction action, IChangeContext context, out string? errorReason)
    {
        errorReason = Evaluate(action, context).Error;
        return errorReason is null;
    }

    /// <summary>
    /// The full range check (M-80). Parameters, all bands: <c>range</c> is the normal maximum (for an area spell, how far the
    /// origin point may be from the caster); <c>longRange</c> lets a target beyond <c>range</c> be hit with disadvantage;
    /// <c>minRange</c> refuses a target closer than it; <c>aoeRadius</c> is the area's reach from <c>originId</c>.
    /// A 5e ranged weapon attack with a hostile character within Touch of the attacker has disadvantage. Every check
    /// passes when no position is recorded.
    /// </summary>
    public static RangeOutcome Evaluate(RulesetAction action, IChangeContext context)
    {
        if (action.ActionType != RulesetActionType.Attack && action.ActionType != RulesetActionType.Spell)
        {
            return default;
        }

        var p = action.Parameters;
        var hasAoe = p.TryGetValue("aoeRadius", out var aoeBand);
        p.TryGetValue("range", out var rangeBand);
        p.TryGetValue("longRange", out var longBand);
        p.TryGetValue("minRange", out var minBand);

        var originId = p.TryGetValue("originId", out var origin) ? origin : action.CharacterId;
        context.Characters.TryGetValue(action.CharacterId, out var caster);

        // The area's origin point: it has its own range from the caster, separate from the radius around it.
        if (hasAoe && rangeBand is not null && !string.Equals(originId, action.CharacterId, StringComparison.OrdinalIgnoreCase)
            && BandRank.TryGetValue(rangeBand, out var originMax)
            && BandBetween(context, action.CharacterId, originId) is { } originBand
            && BandRank.TryGetValue(originBand, out var originRank) && originRank > originMax)
        {
            return new RangeOutcome($"The origin '{originId}' is {originBand} from {action.CharacterId}, out of {rangeBand} range.", null);
        }

        var maxBand = hasAoe ? aoeBand : rangeBand;
        string? disadvantage = null;
        var bandsBeyond = 0;

        if (maxBand is not null && BandRank.TryGetValue(maxBand, out var maxRank))
        {
            var longRank = !hasAoe && longBand is not null && BandRank.TryGetValue(longBand, out var lr) ? lr : -1;
            foreach (var targetId in action.TargetIds)
            {
                var measuredFrom = hasAoe ? originId : action.CharacterId;
                if (!context.Characters.ContainsKey(measuredFrom) || BandBetween(context, measuredFrom, targetId) is not { } band
                    || !BandRank.TryGetValue(band, out var targetRank))
                {
                    continue;
                }

                if (targetRank > maxRank)
                {
                    if (longRank >= targetRank)
                    {
                        disadvantage = $"{targetId} is {band}, beyond normal {maxBand} range (long range)";
                        bandsBeyond = Math.Max(bandsBeyond, targetRank - maxRank);
                        continue;
                    }

                    return new RangeOutcome($"Target '{targetId}' is {band} from {measuredFrom}, out of {(longRank >= 0 ? longBand : maxBand)} range.", null);
                }

                if (!hasAoe && minBand is not null && BandRank.TryGetValue(minBand, out var minRank) && targetRank < minRank)
                {
                    return new RangeOutcome($"Target '{targetId}' is {band} from {measuredFrom}, closer than this attack's {minBand} minimum range.", null);
                }
            }
        }

        if (disadvantage is null && caster is not null && IsRangedWeaponAttack(action, caster)
            && HostileAtTouch(context, caster) is { } adjacent)
        {
            disadvantage = $"{adjacent} is hostile and within Touch of {caster.Name}";
        }

        return new RangeOutcome(null, disadvantage, bandsBeyond);
    }

    /// <summary>A 5e ranged weapon attack, or a thrown weapon used at range (a dagger stabbed at Touch is a melee attack).</summary>
    private static bool IsRangedWeaponAttack(RulesetAction action, Character caster)
    {
        if (action.ActionType != RulesetActionType.Attack || caster.SystemStats is not Dnd5eExtension
            || !action.Parameters.TryGetValue("weaponTags", out var raw))
        {
            return false;
        }

        var tags = raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (tags.Contains("ranged", StringComparer.OrdinalIgnoreCase))
            return true;
        if (!tags.Contains("thrown", StringComparer.OrdinalIgnoreCase))
            return false;
        return action.Parameters.TryGetValue("range", out var band)
            && !string.Equals(band, SpatialDistanceBand.Touch, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The name of a living hostile character recorded at Touch from <paramref name="caster"/> (the other side of the PC/NPC line), or null.</summary>
    private static string? HostileAtTouch(IChangeContext context, Character caster)
    {
        var casterSide = caster.IsPc || caster.IsPartyCompanion;
        foreach (var other in context.Characters.Values)
        {
            // SRD: the hostile must be able to see the attacker and not be incapacitated.
            if (other is null || other.Id == caster.Id || other.IsDead || other.CurrentHp <= 0
                || (other.IsPc || other.IsPartyCompanion) == casterSide
                || ActionBlock.IsBlocked(other, out _))
            {
                continue;
            }

            if (string.Equals(BandBetween(context, caster.Id, other.Id), SpatialDistanceBand.Touch, StringComparison.OrdinalIgnoreCase))
            {
                return other.Name;
            }
        }

        return null;
    }

    /// <summary>The recorded distance band between two characters, from either side's record, or null when none is recorded.</summary>
    private static string? BandBetween(IChangeContext context, string fromId, string toId)
    {
        if (context.Characters.TryGetValue(fromId, out var from)
            && from?.SystemStats?.SpatialPositions?.FirstOrDefault(x => x.TargetId == toId) is { } a)
        {
            return a.DistanceBand;
        }

        return context.Characters.TryGetValue(toId, out var to)
            && to?.SystemStats?.SpatialPositions?.FirstOrDefault(x => x.TargetId == fromId) is { } b
            ? b.DistanceBand
            : null;
    }
}
