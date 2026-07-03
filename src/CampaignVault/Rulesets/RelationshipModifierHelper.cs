using CampaignVault.Models;

namespace CampaignVault.Rulesets;

/// <summary>
/// Translates relationship scores into social roll modifiers, following the band thresholds
/// from WORLD_COHERENCE_DESIGN.md Item 2.
/// </summary>
public static class RelationshipModifierHelper
{
    /// <summary>
    /// Gets the roll modifier (bonus or penalty) for a social check based on the target's relationship to the actor.
    /// Also returns a narrative label to include in the roll result (e.g. "trusted friend", "hated enemy").
    /// </summary>
    /// <param name="target">The character being persuaded/intimidated/etc.</param>
    /// <param name="actor">The character making the social check.</param>
    /// <param name="config">Campaign configuration (for symmetric fallback option).</param>
    /// <returns>Tuple of (modifier bonus/penalty, narrative label)</returns>
    public static (int Modifier, string Label) GetSocialModifier(Character? target, Character? actor, CampaignConfig config)
    {
        if (target?.Social?.Relationships == null || actor?.Id == null)
        {
            return (0, "neutral");
        }

        var hasDirectScore = target.Social.Relationships.TryGetValue(actor.Id, out var relationshipScore);
        if (!hasDirectScore)
        {
            relationshipScore = 0;
        }

        // Fallback only when the target→actor key is missing (not when explicitly neutral at 0).
        if (!hasDirectScore && config.SymmetricRelationshipFallback)
        {
            var reverseScore = actor.Social?.Relationships?.GetValueOrDefault(target.Id, 0) ?? 0;
            if (reverseScore != 0)
            {
                relationshipScore = (int)Math.Floor(reverseScore / 2.0);
            }
        }

        // Band thresholds per design doc: ≥80→+5, 60–79→+3, 40–59→+1, -39..39→0, -59..-40→-1, -79..-60→-3, ≤-80→-5
        return relationshipScore switch
        {
            >= 80 => (5, "trusted friend"),
            >= 60 => (3, "friendly"),
            >= 40 => (1, "acquainted"),
            <= -80 => (-5, "hated enemy"),
            <= -60 => (-3, "hostile"),
            <= -40 => (-1, "distrustful"),
            _ => (0, "neutral")
        };
    }
}
