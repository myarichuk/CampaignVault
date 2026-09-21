namespace CampaignVault.Data.Initiative;

/// <summary>
/// Shared fallback-chain similarity lookup for comparing a memory's cached vector against this
/// turn's trigger vector and recent events — used by both <see cref="MemoryInitiativeProvider"/>
/// (the initiative gate) and <see cref="DefaultRelevantMemorySelector"/> (the ranking term) so
/// they don't independently duplicate the same chain.
/// </summary>
internal static class SemanticTriggerMatcher
{
    /// <summary>Best cosine similarity between <paramref name="memoryVector"/> and
    /// <paramref name="ctx"/>'s TriggerVector/RecentEvents/NpcRecentEvents vectors, or 0 if none
    /// are available to compare against.</summary>
    public static double BestSimilarity(float[] memoryVector, NpcInitiativeContext ctx)
    {
        var best = 0.0;

        if (ctx.TriggerVector is { Length: > 0 } triggerVector)
        {
            best = SemanticEnrichmentHelper.CosineSimilarity(memoryVector, triggerVector);
        }

        var bestEventSim = ctx.RecentEvents.Concat(ctx.NpcRecentEvents)
            .Where(e => e.SemanticVector is { Length: > 0 })
            .Select(e => SemanticEnrichmentHelper.CosineSimilarity(memoryVector, e.SemanticVector!))
            .DefaultIfEmpty(0.0)
            .Max();

        return Math.Max(best, bestEventSim);
    }
}
