using CampaignVault.Models;

namespace CampaignVault.Data.Initiative;

public interface INpcInitiativeService
{
    NpcInitiativeEnrichment Enrich(NpcInitiativeContext ctx, Campaign campaign);
}