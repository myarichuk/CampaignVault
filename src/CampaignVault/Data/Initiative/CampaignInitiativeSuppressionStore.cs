using CampaignVault.Models;

namespace CampaignVault.Data.Initiative;

public sealed class CampaignInitiativeSuppressionStore : IInitiativeSuppressionStore
{
    public bool IsConsumed(Campaign campaign, string initiativeKey)
    {
        return campaign.InitiativeSurfaced.TryGetValue(initiativeKey, out var state) && state.Consumed;
    }

    public void MarkConsumed(Campaign campaign, string initiativeKey, int surfacedDay, string surfacedViaTool)
    {
        campaign.InitiativeSurfaced[initiativeKey] = new InitiativeSurfacedState(surfacedDay, surfacedViaTool, true);
    }

    public void ReArm(Campaign campaign, string initiativeKey)
    {
        campaign.InitiativeSurfaced.Remove(initiativeKey);
    }

    public void PruneStale(Campaign campaign, int currentDay, int retentionDays)
    {
        if (retentionDays <= 0)
        {
            return;
        }

        var staleKeys = campaign.InitiativeSurfaced
            .Where(kv => currentDay - kv.Value.SurfacedDay > retentionDays)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var key in staleKeys)
        {
            campaign.InitiativeSurfaced.Remove(key);
        }
    }
}