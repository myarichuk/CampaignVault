using CampaignVault.Models;

namespace CampaignVault.Data.Initiative;

public sealed class DefaultBehavioralTensionCalculator : IBehavioralTensionCalculator
{
    public (double Tension, TensionBreakdown Breakdown) Calculate(
        Character npc,
        NpcInitiativeContext ctx,
        IReadOnlyList<MemoryNode> relevantMemories)
    {
        var config = ctx.Config;
        var psych = npc.Psychology ?? new PsychologyProfile();
        var needs = npc.Needs ?? new NeedsProfile();
        var social = npc.Social ?? new SocialProfile();

        var needStress = ComputeNeedStress(needs, config);
        var memoryStress = ComputeMemoryStress(relevantMemories, psych.Resilience, config);
        var relationalStress = ComputeRelationalStress(social, ctx.RecentEvents);
        var dispositionStress = DispositionMatcher.Score(
            psych,
            ctx.PresentEntities,
            ctx.Location,
            config).DispositionStress;
        var momentumStress = ComputeMomentumStress(npc, config);

        var weights = NormalizeWeights(config);
        var tension = Math.Clamp(
            needStress * weights.Need
            + memoryStress * weights.Memory
            + relationalStress * weights.Relational
            + dispositionStress * weights.Disposition
            + momentumStress * weights.Momentum,
            0,
            100);

        return (tension, new TensionBreakdown(needStress, memoryStress, relationalStress, dispositionStress, momentumStress));
    }

    /// <summary>
    /// Scales Character.IdleSceneBeats (adjusted for personality via MomentumTraitScoring, same as
    /// SceneMomentumInitiativeProvider) to 0-100, reaching 100 at MomentumIdleBeatsHighThreshold so it
    /// lines up with that provider's own High-urgency cutover.
    /// </summary>
    private static float ComputeMomentumStress(Character npc, CampaignConfig config)
    {
        var highThreshold = Math.Max(1, config.MomentumIdleBeatsHighThreshold);
        var effectiveIdleBeats = MomentumTraitScoring.EffectiveIdleBeats(npc.IdleSceneBeats, npc.Psychology?.Traits);
        return Math.Clamp(effectiveIdleBeats * 100f / highThreshold, 0f, 100f);
    }

    private static float ComputeNeedStress(NeedsProfile needs, CampaignConfig config)
    {
        var maxNeed = needs.ActiveNeeds.Count == 0 ? 0f : needs.ActiveNeeds.Values.Max();
        var stress = maxNeed;
        if (needs.ActivityConflictActive)
        {
            stress = Math.Min(100f, stress + config.NeedConflictTensionBoost);
        }

        return stress;
    }

    private static float ComputeMemoryStress(
        IReadOnlyList<MemoryNode> relevantMemories,
        double resilience,
        CampaignConfig config)
    {
        if (relevantMemories.Count == 0)
        {
            return 0f;
        }

        var sum = 0f;
        foreach (var memory in relevantMemories)
        {
            memory.ApplyMigrationDefaultsIfNeeded();
            var valenceWeight = memory.Valence switch
            {
                EmotionalValence.Positive => config.TensionValencePositive,
                EmotionalValence.Negative => config.TensionValenceNegative,
                EmotionalValence.Traumatic => config.TensionValenceTraumatic,
                _ => config.TensionValenceNeutral
            };

            var contribution = (float)(memory.Salience * valenceWeight * 100);
            if (memory.Valence == EmotionalValence.Traumatic)
            {
                contribution *= (float)(1.0 - Math.Clamp(resilience, 0.0, 1.0));
            }

            sum += contribution;
        }

        return Math.Min(100f, sum);
    }

    private static float ComputeRelationalStress(SocialProfile social, IReadOnlyList<Event> recentEvents)
    {
        var stress = 0f;
        foreach (var value in social.Relationships.Values)
        {
            if (Math.Abs(value) >= 80)
            {
                stress += 40f;
            }
        }

        stress = Math.Min(100f, stress);

        if (recentEvents.Any(e =>
                e.Summary.Contains("betray", StringComparison.OrdinalIgnoreCase)
                || e.Summary.Contains("anger", StringComparison.OrdinalIgnoreCase)
                || e.Summary.Contains("hostile", StringComparison.OrdinalIgnoreCase)))
        {
            stress = Math.Min(100f, stress + 20f);
        }

        return stress;
    }

    private static (float Need, float Memory, float Relational, float Disposition, float Momentum) NormalizeWeights(CampaignConfig config)
    {
        var need = config.TensionWeightNeed;
        var memory = config.TensionWeightMemory;
        var relational = config.TensionWeightRelational;
        var disposition = config.TensionWeightDisposition;
        var momentum = config.TensionWeightMomentum;
        var sum = need + memory + relational + disposition + momentum;
        if (sum <= 0.0001f)
        {
            return (0.30f, 0.25f, 0.25f, 0.20f, 0f);
        }

        if (Math.Abs(sum - 1f) < 0.0001f)
        {
            return (need, memory, relational, disposition, momentum);
        }

        return (need / sum, memory / sum, relational / sum, disposition / sum, momentum / sum);
    }
}