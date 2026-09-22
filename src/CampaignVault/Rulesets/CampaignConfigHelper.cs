using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;

namespace CampaignVault.Rulesets;

internal static class CampaignConfigHelper
{
    public static CampaignConfig EffectiveConfig(IChangeContext context) =>
        context.Config ?? new CampaignConfig();
}