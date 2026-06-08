using CampaignVault.Models;

namespace CampaignVault.Data.Initiative;

public interface INpcInitiativeSignalProvider
{
    IReadOnlyList<InitiativeCandidate> GetCandidates(NpcInitiativeContext ctx);
}