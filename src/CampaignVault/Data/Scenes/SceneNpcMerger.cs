using CampaignVault.Models;

namespace CampaignVault.Data.Scenes;

internal sealed class SceneNpcMerger
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
        string.IsNullOrEmpty(entityCampaignName)
        || string.Equals(entityCampaignName, effectiveCampaign, StringComparison.OrdinalIgnoreCase);
}
