using CampaignVault.Models;

namespace CampaignVault.Data.Initiative;

public sealed class DefaultRelevantMemorySelector : IRelevantMemorySelector
{
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

        if (!string.IsNullOrWhiteSpace(locationId)
            && (memory.Topic.Contains(locationId, StringComparison.OrdinalIgnoreCase)
                || memory.Details.Contains(locationId, StringComparison.OrdinalIgnoreCase)))
        {
            score += 0.25;
        }
        else if (!string.IsNullOrWhiteSpace(locationName)
                 && (memory.Topic.Contains(locationName, StringComparison.OrdinalIgnoreCase)
                     || memory.Details.Contains(locationName, StringComparison.OrdinalIgnoreCase)))
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

        return score;
    }
}