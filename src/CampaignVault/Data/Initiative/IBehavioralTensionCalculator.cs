using CampaignVault.Models;

namespace CampaignVault.Data.Initiative;

public interface IBehavioralTensionCalculator
{
    (double Tension, TensionBreakdown Breakdown) Calculate(
        Character npc,
        NpcInitiativeContext ctx,
        IReadOnlyList<MemoryNode> relevantMemories);
}