using CampaignVault.Models;

namespace CampaignVault.Data.Initiative;

public sealed class DefaultRelevantMemorySelector : IRelevantMemorySelector
{
    // Same threshold as MemoryInitiativeProvider's gate — see its comment for calibration notes.
    private const double SemanticMatchThreshold = 0.55;


    public IReadOnlyList<MemoryNode> Select(Character npc, NpcInitiativeContext ctx, int maxCount = 3)
    {
        var psych = npc.Psychology ?? new PsychologyProfile();
        if (psych.Memories.Count == 0)
        {
            return [];
        }

        var presentIds = new HashSet<string>(
            ctx.PresentEntities.Select(e => e.Id),
            StringComparer.OrdinalIgnoreCase);

        var locationName = ctx.Location?.Name;
        var locationId = ctx.Location?.Id;

        return psych.Memories.Values
            .Select(memory =>
            {
                memory.ApplyMigrationDefaultsIfNeeded();
                return (Memory: memory, Score: ScoreMemory(memory, ctx, presentIds, locationName, locationId));
            })
            .OrderByDescending(x => x.Score)
            .Take(maxCount)
            .Select(x => x.Memory)
            .ToList();
    }

    private static double ScoreMemory(
        MemoryNode memory,
        NpcInitiativeContext ctx,
        HashSet<string> presentIds,
        string? locationName,
        string? locationId)
    {
        var score = memory.Salience;

        if (memory.RelatedEntityIds.Any(id => presentIds.Contains(id)))
        {
            score += 0.35;
        }

        // Details is typed non-nullable (`= null!`) but legacy/malformed knowledge_update commits
        // can still persist it as null — guard the read side, not just the write side.
        var details = memory.Details ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(locationId)
            && (memory.Topic.Contains(locationId, StringComparison.OrdinalIgnoreCase)
                || details.Contains(locationId, StringComparison.OrdinalIgnoreCase)))
        {
            score += 0.25;
        }
        else if (!string.IsNullOrWhiteSpace(locationName)
                 && (memory.Topic.Contains(locationName, StringComparison.OrdinalIgnoreCase)
                     || details.Contains(locationName, StringComparison.OrdinalIgnoreCase)))
        {
            score += 0.2;
        }

        score *= memory.Urgency switch
        {
            MemoryUrgency.Urgent => 1.4,
            MemoryUrgency.High => 1.2,
            MemoryUrgency.Low => 0.8,
            _ => 1.0
        };

        if (ctx.CurrentDay - memory.DayAcquired <= 7)
        {
            score += 0.15;
        }

        if (memory.SemanticVector is { Length: > 0 } memVec
            && SemanticTriggerMatcher.BestSimilarity(memVec, ctx) >= SemanticMatchThreshold)
        {
            // Deliberately smaller than the +0.35 present-entity bonus — semantic match is
            // corroborating evidence, not as strong a signal as "this NPC is literally about
            // the entity standing right here."
            score += 0.2;
        }

        return score;
    }
}