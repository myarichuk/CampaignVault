using CampaignVault.Models;

namespace CampaignVault.Data.Initiative;

/// <summary>
/// Cheap, in-memory priority estimate used by MutationTools.SelectAndEnrichInitiativeAsync to rank an
/// entire NPC pool before committing to the one full DefaultBehavioralTensionCalculator.Calculate call
/// (which needs a memory-selector query and is only worth paying for the actual winner). Deliberately
/// only looks at fields already loaded on Character — no DB calls — so scanning a whole scene's NPC pool
/// stays free. Uses need + momentum stress only (skipping memory/relational/disposition, which need a
/// query or a scene-token scan); those two are still the dominant signal for "who wants to act right now."
/// </summary>
internal static class InitiativeSelectionScorer
{
    public static float EstimatePriority(Character npc, CampaignConfig config, IReadOnlyCollection<string> recentSlotWinnerIds)
    {
        var needs = npc.Needs?.ActiveNeeds;
        var needStress = needs is { Count: > 0 } ? needs.Values.Max() : 0f;

        var highThreshold = Math.Max(1, config.MomentumIdleBeatsHighThreshold);
        var effectiveIdleBeats = MomentumTraitScoring.EffectiveIdleBeats(npc.IdleSceneBeats, npc.Psychology?.Traits);
        var momentumStress = Math.Clamp(effectiveIdleBeats * 100f / highThreshold, 0f, 100f);

        var priority = (needStress * 0.4f) + (momentumStress * 0.6f);

        if (recentSlotWinnerIds.Contains(npc.Id))
        {
            priority -= config.InitiativeCooldownPenalty;
        }

        return priority;
    }
}
