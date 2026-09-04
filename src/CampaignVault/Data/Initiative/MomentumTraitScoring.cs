namespace CampaignVault.Data.Initiative;

/// <summary>
/// Scales raw Character.IdleSceneBeats by personality before SceneMomentumInitiativeProvider and
/// DefaultBehavioralTensionCalculator compare it against thresholds. Mirrors DispositionMatcher's
/// free-text Psychology.Traits keyword scan (same idea, different trait set): an extraverted/impulsive
/// NPC reads more "pressure to act" out of the same raw idle-beat count than an introverted/reserved one,
/// so two companions who've been equally silent for N beats don't carry identical momentum urgency.
/// Shared by both call sites so a candidate's Urgency and its TensionBreakdown.MomentumStress never
/// disagree about how "idle" a given NPC currently reads as.
/// </summary>
internal static class MomentumTraitScoring
{
    private static readonly string[] BoldTraits =
        ["extraverted", "extroverted", "gregarious", "outgoing", "impulsive", "bold", "chatty", "restless", "impatient"];

    private static readonly string[] ReservedTraits =
        ["introverted", "reserved", "shy", "quiet", "taciturn", "timid", "stoic", "patient"];

    public static float EffectiveIdleBeats(int rawIdleBeats, IReadOnlyList<string>? traits)
    {
        if (rawIdleBeats <= 0 || traits is not { Count: > 0 })
        {
            return rawIdleBeats;
        }

        var boldHits = traits.Count(t => BoldTraits.Any(b => t.Equals(b, StringComparison.OrdinalIgnoreCase)));
        var reservedHits = traits.Count(t => ReservedTraits.Any(r => t.Equals(r, StringComparison.OrdinalIgnoreCase)));
        if (boldHits == 0 && reservedHits == 0)
        {
            return rawIdleBeats;
        }

        var multiplier = Math.Clamp(1.0f + (0.25f * boldHits) - (0.25f * reservedHits), 0.4f, 2.0f);
        return rawIdleBeats * multiplier;
    }
}
