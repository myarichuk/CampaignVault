using CampaignVault.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CampaignVault.Data.Initiative;

public sealed class NpcInitiativeService(
    IEnumerable<INpcInitiativeSignalProvider> providers,
    IRelevantMemorySelector memorySelector,
    IBehavioralTensionCalculator tensionCalculator,
    IInitiativeSuppressionStore suppressionStore,
    ILogger<NpcInitiativeService>? logger = null) : INpcInitiativeService
{
    private readonly IReadOnlyList<INpcInitiativeSignalProvider> _providers = providers.ToList();
    private readonly ILogger<NpcInitiativeService> _logger = logger ?? NullLogger<NpcInitiativeService>.Instance;

    public NpcInitiativeEnrichment Enrich(NpcInitiativeContext ctx, Campaign campaign)
    {
        var npc = ctx.Npc;
        var psych = npc.Psychology ?? new PsychologyProfile();

        // Per-provider isolation: one throwing heuristic must not zero the whole
        // enrichment (or skip suppression bookkeeping below). The per-NPC try/catch in
        // MutationTools.SelectAndEnrichInitiativeAsync stays as the outer net.
        var provided = new List<InitiativeCandidate>();
        foreach (var provider in _providers)
        {
            try
            {
                provided.AddRange(provider.GetCandidates(ctx) ?? []);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Initiative provider {ProviderType} failed for NPC '{NpcId}'; continuing with remaining providers.",
                    provider.GetType().Name,
                    npc.Id);
            }
        }

        var candidates = provided
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

        var topCandidate = candidates.FirstOrDefault();
        var turnIntent = tension >= ctx.Config.BehavioralTensionSpeakingThreshold && topCandidate is { Urgency: >= MemoryUrgency.High }
            ? new TurnIntentSignal("npc", topCandidate.FramingPrompt, topCandidate.Urgency)
            : null;

        return new NpcInitiativeEnrichment(
            Math.Round(tension),
            ctx.IncludeTensionBreakdown ? breakdown : null,
            candidates,
            relevantMemories)
        {
            TurnIntent = turnIntent
        };
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