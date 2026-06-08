using CampaignVault.Models;

namespace CampaignVault.Data.Initiative;

public sealed class NpcInitiativeService(
    IEnumerable<INpcInitiativeSignalProvider> providers,
    IRelevantMemorySelector memorySelector,
    IBehavioralTensionCalculator tensionCalculator,
    IInitiativeSuppressionStore suppressionStore) : INpcInitiativeService
{
    private readonly IReadOnlyList<INpcInitiativeSignalProvider> _providers = providers.ToList();

    public NpcInitiativeEnrichment Enrich(NpcInitiativeContext ctx, Campaign campaign)
    {
        var npc = ctx.Npc;
        var psych = npc.Psychology ?? new PsychologyProfile();

        var candidates = _providers
            .SelectMany(p => p.GetCandidates(ctx))
            .Where(c => c.NpcId == npc.Id)
            .Where(c => !suppressionStore.IsConsumed(campaign, BuildSuppressionKey(npc.Id, c.Key)))
            .Select(c => ApplyPersonalityWeight(c, psych))
            .OrderByDescending(c => c.Weight)
            .Take(3)
            .ToList();

        var relevantMemories = memorySelector.Select(npc, ctx);
        var (tension, breakdown) = tensionCalculator.Calculate(npc, ctx, relevantMemories);

        foreach (var candidate in candidates)
        {
            suppressionStore.MarkConsumed(
                campaign,
                BuildSuppressionKey(npc.Id, candidate.Key),
                ctx.CurrentDay,
                ctx.SurfacedViaTool);
        }

        suppressionStore.PruneStale(
            campaign,
            ctx.CurrentDay,
            ctx.Config.InitiativeSuppressionRetentionDays);

        return new NpcInitiativeEnrichment(
            tension,
            ctx.IncludeTensionBreakdown ? breakdown : null,
            candidates,
            relevantMemories);
    }

    private static InitiativeCandidate ApplyPersonalityWeight(InitiativeCandidate candidate, PsychologyProfile psych)
    {
        if (candidate.Driver != InitiativeDriver.Relational)
        {
            return candidate;
        }

        var openness = Math.Clamp(psych.Openness, 0.0, 1.0);
        var scaledWeight = candidate.Weight * (0.5 + openness);
        return candidate with { Weight = scaledWeight };
    }

    internal static string BuildSuppressionKey(string npcId, string initiativeKey) =>
        $"initiative:{npcId}:{initiativeKey}";
}