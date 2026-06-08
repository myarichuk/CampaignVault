using CampaignVault.Models;

namespace CampaignVault.Data.Initiative;

public sealed class NeedActivityConflictProvider : INpcInitiativeSignalProvider
{
    public IReadOnlyList<InitiativeCandidate> GetCandidates(NpcInitiativeContext ctx)
    {
        var (hasConflict, need, _) = NeedActivityConflictHelper.Detect(ctx.Npc, ctx.Config);
        if (!hasConflict || string.IsNullOrWhiteSpace(need))
        {
            return [];
        }

        var activity = ctx.Npc.CurrentActivity ?? "current duties";
        var needValue = ctx.Npc.Needs?.ActiveNeeds.GetValueOrDefault(need, 0f) ?? 0f;
        var urgency = needValue >= 85 ? MemoryUrgency.High : MemoryUrgency.Normal;
        var weight = Math.Clamp(needValue * 0.8, 40, 90);

        var framing = need switch
        {
            "tiredness" => $"Exhausted but still {activity} — may slip, snap, or ask for help.",
            "hunger" => $"Ravenous while {activity} — distracted, irritable, or fixated on food.",
            "thirst" => $"Parched while {activity} — strained voice, impatience, or frequent pauses.",
            _ => $"High {need} while {activity} — struggling to keep composure."
        };

        return
        [
            new InitiativeCandidate(
                $"need:{ctx.Npc.Id}:{need}",
                ctx.Npc.Id,
                InitiativeDriver.Need,
                urgency,
                framing,
                weight)
        ];
    }
}