using CampaignVault.Models;

namespace CampaignVault.Data.Initiative;

public interface IInitiativeSuppressionStore
{
    bool IsConsumed(Campaign campaign, string initiativeKey);

    void MarkConsumed(
        Campaign campaign,
        string initiativeKey,
        int surfacedDay,
        string surfacedViaTool);

    void ReArm(Campaign campaign, string initiativeKey);

    void PruneStale(Campaign campaign, int currentDay, int retentionDays);
}