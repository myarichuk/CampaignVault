using CampaignVault.Data;
using CampaignVault.Models;

namespace CampaignVault.Data.Scenes;

public sealed class SceneNpcMerger
{
    public List<Character> Merge(
        IEnumerable<Character> npcsFromIndex,
        IEnumerable<Character> npcsFromSimulation,
        string effectiveCampaign)
    {
        var npcMap = new Dictionary<string, Character>(StringComparer.OrdinalIgnoreCase);

        foreach (var npc in npcsFromIndex)
        {
            if (!IsVisibleInCampaign(npc.CampaignName, effectiveCampaign))
            {
                continue;
            }

            npcMap[npc.Id] = npc;
        }

        foreach (var npc in npcsFromSimulation)
        {
            if (!IsVisibleInCampaign(npc.CampaignName, effectiveCampaign))
            {
                continue;
            }

            npcMap[npc.Id] = npc;
        }

        return npcMap.Values.ToList();
    }

    private static bool IsVisibleInCampaign(string? entityCampaignName, string effectiveCampaign) =>
        CampaignEntityVisibility.IsVisibleInCampaign(entityCampaignName, effectiveCampaign);
}
