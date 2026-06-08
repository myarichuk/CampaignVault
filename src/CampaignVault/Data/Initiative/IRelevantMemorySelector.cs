using CampaignVault.Models;

namespace CampaignVault.Data.Initiative;

public interface IRelevantMemorySelector
{
    IReadOnlyList<MemoryNode> Select(Character npc, NpcInitiativeContext ctx, int maxCount = 3);
}